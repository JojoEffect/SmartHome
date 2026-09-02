<#
.SYNOPSIS
    Build and deploy RoomSensor to the ESP32 via nanoff.

.DESCRIPTION
    1. Sources scripts\local.env.ps1 for machine-specific settings.
    2. Builds the RoomSensor nanoFramework project with MSBuild.
    3. Asks the device to erase whatever sits past the end of the image about to
       be flashed, so no previous app's tail survives it.
    4. Flashes the compiled binaries to the device on the configured COM port
       using the nanoff CLI tool.

    Step 3 needs the debugger connection, and so does Visual Studio -- close its
    device window if a deploy reports it could not clear the deployment area.
    That is a warning rather than a failure: the flash still goes ahead, padded
    to the whole partition instead, which is slower and equally safe.

    ALTERNATIVE – Visual Studio deploy:
    Open SmartHome.sln, set RoomSensor as the startup project, choose the
    correct COM port in Project > Properties > nanoFramework, and press F5 or
    Deploy from the Build menu. This is NOT the same mechanism as this script --
    VS pushes assemblies directly over the debugger's WireProtocol connection
    (DebugEngine.DeploymentExecute), while this script flashes a merged .bin via
    nanoff at a fixed flash address. They can behave differently if that address
    ever stops matching the device's actual "deploy" partition offset -- see
    -DeployAddress below.

.NOTES
    Requires:
      - nanoff CLI:  dotnet tool install -g nanoff
      - MSBuild (Visual Studio Build Tools or full VS installation)
      - scripts\local.env.ps1 populated from the template

.EXAMPLE
    .\scripts\Deploy-ToDevice.ps1
    .\scripts\Deploy-ToDevice.ps1 -Verbose
    .\scripts\Deploy-ToDevice.ps1 -Project src\devices\IrrigationControl\IrrigationControl.nfproj
#>

[CmdletBinding()]
param(
    # nfproj to build and deploy (relative to repo root)
    [string]$Project = 'src\devices\RoomSensor\RoomSensor.nfproj',

    # Build configuration
    [string]$Configuration = 'Debug',

    # Flash address of the device's "deploy" partition. nanoFirmwareFlasher's own
    # default (0x1B0000, from its Esp32Firmware.cs) landed inside the "factory"
    # partition instead on this device's current firmware/partition layout --
    # confirmed by watching a cold boot after deploy: the CLR found zero
    # assemblies (CLR_E_WRONG_TYPE) at nanoff's default address, and the correct
    # ones at 0x1E0000. That mismatch meant every nanoff-deployed app silently
    # never ran at all, while VS's debugger-based deploy (a different mechanism
    # entirely) worked fine -- which is what made this so confusing to diagnose.
    # If this ever goes stale again (firmware update changes the partition
    # layout), re-derive it from a boot log: .\scripts\Watch-DeviceSerial.ps1
    # and read the "deploy" line's Offset column from the printed partition table.
    [string]$DeployAddress = '0x1E0000',

    # The deploy partition's size. Every deploy has to leave the flash past its own
    # image erased -- nanoff's erase+write only covers the image file's own byte
    # length, so deploying a SMALLER app after a LARGER one leaves that previous app's
    # trailing assembly bytes sitting unerased past the new image's end, and the CLR
    # loads BOTH on boot (confirmed: saw WifiCheck's own small assembly list plus
    # leftover Bmp280Check assemblies in the same resolution pass, failing to link
    # because bytes past WifiCheck's real end were stale Bmp280Check data, not blank
    # flash). The device does that erase itself now -- see the "Device deployment area"
    # section of Common.ps1 -- and reports this same length while it is at it, so this
    # value is the fit check, and the size of the 0xFF pad in the one case where the
    # device could not be asked.
    #
    # Measured 2026-08-30 off the device from the same boot-log partition table as
    # Measured 2026-08-30 off the device from the same boot-log partition table as
    # -DeployAddress above (.\scripts\Watch-DeviceSerial.ps1), this time the "deploy"
    # line's Length column rather than its Offset:
    #   2 factory   factory app    00 00 00010000 001d0000
    #   3 deploy    Unknown data   01 84 001e0000 001c0000
    #   4 config    Unknown data   01 83 003c0000 00040000
    # on a 4MB flash running nanoCLR 1.17.0.339 / ESP-IDF v5.5.4. So deploy runs
    # 0x1E0000..0x3A0000, and what follows it is NOT the next partition: config does
    # not start until 0x3C0000, leaving 0x20000 (128KB) of unallocated flash in
    # between. Don't reach for that headroom -- it is outside the partition the CLR
    # scans and outside what nanoff was told to write, so it buys nothing, and the
    # next firmware image is free to lay its partitions out differently.
    # Until this was measured the value had only ever been copied from a prose
    # comment of unclear provenance -- issue #47. Re-read that "deploy" line if a
    # firmware update changes the layout.
    [int]$DeployPartitionSize = 0x1C0000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Common.ps1')
Import-SmartHomeLocalEnv

$repoRoot    = Get-SmartHomeRepoRoot
$comPort     = Get-RequiredEnvValue -Name 'SMARTHOME_COM_PORT'
$projectPath = Join-Path $repoRoot $Project

if (-not (Test-Path $projectPath)) {
    Write-Error "Project not found: $projectPath"
    exit 1
}

# ── Locate MSBuild ────────────────────────────────────────────────────────────
$msbuild = Get-MSBuildPath

# ── Build ─────────────────────────────────────────────────────────────────────
Write-Host "Building $Project ($Configuration)..." -ForegroundColor Cyan

# A plain incremental /t:Build removes the deployment .bin when nothing else
# changed (nanoFramework's deployment-image task doesn't run, and the stale
# .bin from a prior build gets cleaned up) -- always force a full rebuild so
# the .bin actually exists afterward, even when the source is unchanged.
& $msbuild $projectPath /t:Rebuild /p:Configuration=$Configuration /v:minimal /nologo
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed (exit code $LASTEXITCODE)."
    exit $LASTEXITCODE
}
Write-Host "  Build succeeded." -ForegroundColor Green

# ── Locate build output ───────────────────────────────────────────────────────
$projectDir = Split-Path $projectPath -Parent
$binDir     = Join-Path $projectDir "bin\$Configuration"
$projectName = Get-NfProjectAssemblyName -ProjectPath $projectPath

if (-not (Test-Path $binDir)) {
    Write-Error "Build output directory not found: $binDir"
    exit 1
}

# ── Flash via nanoff ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Flashing to $comPort via nanoff..." -ForegroundColor Cyan

$nanoff = Get-Command nanoff -ErrorAction SilentlyContinue
if (-not $nanoff) {
    Write-Error @"
nanoff not found on PATH.
Install it with:  dotnet tool install -g nanoff
Then restart this shell so the new PATH takes effect.
"@
    exit 1
}

$deployImage = Join-Path $binDir ($projectName + '.bin')
if (-not (Test-Path $deployImage)) {
    Write-Error "Deploy image not found: $deployImage"
    exit 1
}

$imageBytes = [System.IO.File]::ReadAllBytes($deployImage)

# Ahead of the erase, deliberately: the erase takes the device's current app with it,
# so an image that was never going to fit has to be refused while the device still has
# something to run. Against the constant, because the device's own figure only arrives
# with the erase -- the check below repeats this against that one.
if ($imageBytes.Length -gt $DeployPartitionSize) {
    Write-Error @"
Deploy image ($($imageBytes.Length) bytes) does not fit the deploy partition ($DeployPartitionSize bytes).
Either the app has outgrown the partition, or -DeployPartitionSize is stale -- re-read the
"deploy" line of the table printed by .\scripts\Watch-DeviceSerial.ps1 and pass the real size.
"@
    exit 1
}

# ── Clear whatever is already on the device ───────────────────────────────────
# The invariant is "nothing but erased flash past the end of the image about to be
# written", and the device is the only thing that knows whether that already holds:
# Visual Studio's F5 deploy and the nanoFramework test adapter write this same
# partition without this script ever seeing it (issue #46). So it is asked, rather
# than guessed at from a record of what THIS script last flashed.
#
# See the "Device deployment area" section of Common.ps1 for what the firmware will
# and will not answer, and why this is an erase rather than a read.
$erase = Clear-SmartHomeDeviceDeployment -ComPort $comPort -KeepBytes $imageBytes.Length

# What the device says its deploy partition is, when it answered. Ground truth for the
# two values this script otherwise carries as constants measured off a boot log.
$partitionSize = $DeployPartitionSize

if ($erase.Contains('Start')) {
    # The failure this catches is the one that cost a full debugging session: nanoff's
    # own default address landed in the "factory" partition on this device's layout, so
    # every deploy silently never ran. Nothing about that is visible in nanoff's output
    # -- it reports a successful write either way -- and until the device could be
    # asked, the only way to notice was to read a boot log.
    #
    # Read whether or not the erase itself worked: the monitor prints the geometry
    # before it touches anything, so a failed erase still carries a usable answer, and a
    # wrong flash address is worth refusing either way.
    $reportedAddress = '0x{0:X}' -f $erase['Start']
    if ([Convert]::ToInt64($DeployAddress, 16) -ne $erase['Start']) {
        Write-Error @"
The device reports its deploy partition at $reportedAddress, but -DeployAddress is $DeployAddress.
Flashing at the wrong address writes into a partition the CLR never scans, and nanoff
reports success anyway -- so this stops here rather than producing a device that boots
without the app that was just deployed. Pass -DeployAddress $reportedAddress, or re-read the
"deploy" line of the partition table printed by .\scripts\Watch-DeviceSerial.ps1.
"@
        exit 1
    }

    if ($erase['Length'] -ne $DeployPartitionSize) {
        # The device wins: -DeployPartitionSize is a value measured off a boot log on
        # 2026-08-30, and a firmware update is free to lay the partitions out again.
        Write-Warning ("The device reports a deploy partition of {0} bytes, not the -DeployPartitionSize {1}. Using the device's. Update the default if the firmware layout has changed for good." -f $erase['Length'], $DeployPartitionSize)
        $partitionSize = $erase['Length']

        if ($imageBytes.Length -gt $partitionSize) {
            Write-Error @"
Deploy image ($($imageBytes.Length) bytes) does not fit the deploy partition the device reports ($partitionSize bytes).
-DeployPartitionSize said it would fit, so the partition layout has changed under this script.
Nothing was flashed; the device may already have been erased. Pass the real size and redeploy.
"@
            exit 1
        }
    }
}

$flashImage = $deployImage
$paddedImage = $null

if ($erase.Ok) {
    Write-Host "  Deployment area past $($imageBytes.Length) bytes is erased; flashing the image as built." -ForegroundColor DarkGray
}
else {
    # The device could not be asked -- it is unreachable, or something else already
    # holds the debugger connection (Visual Studio open on the same port is the usual
    # one). Fall back to what this script did before it could ask: pad the image with
    # 0xFF far enough that the write itself erases anything that could be behind it.
    # The whole partition, because without the device there is no smaller number that
    # is honestly a worst case -- over-padding costs seconds, under-padding costs a
    # device booting somebody else's assemblies.
    Write-Warning ("Could not clear the deployment area ({0}). Padding the image to the full partition instead -- slower, same guarantee." -f $erase.Detail)

    $paddedImage = Join-Path $binDir ($projectName + '.padded.bin')
    $padded = New-Object byte[] $partitionSize

    # Fill with 0xFF by doubling an already-filled prefix rather than looping over
    # hundreds of KB one byte at a time: the scalar loop measured 560ms per deploy, this
    # is ~10ms. ([Array]::Fill would be simpler but is .NET Core only, and this runs on
    # Windows PowerShell 5.1.)
    $padded[0] = 0xFF
    $filled = 1
    while ($filled -lt $partitionSize) {
        $chunk = [Math]::Min($filled, $partitionSize - $filled)
        [Array]::Copy($padded, 0, $padded, $filled, $chunk)
        $filled += $chunk
    }

    [Array]::Copy($imageBytes, $padded, $imageBytes.Length)
    [System.IO.File]::WriteAllBytes($paddedImage, $padded)

    Write-Host "  Padded deploy image: $($imageBytes.Length) -> $partitionSize bytes (0xFF fill)." -ForegroundColor DarkGray
    $flashImage = $paddedImage
}

nanoff --deploy --serialport $comPort --image "$flashImage" --address $DeployAddress
$nanoffExit = $LASTEXITCODE

if ($null -ne $paddedImage) {
    # nanoff has read the image by now, so the padded copy is dead weight in bin\Debug.
    # Leaving it also puts a stale .padded.bin next to a freshly built .bin, which is the
    # same shape as the stale-deployment problem the padding exists to fix.
    Remove-Item -Path $paddedImage -Force -ErrorAction SilentlyContinue
}

if ($nanoffExit -ne 0) {
    Write-Error "nanoff deploy failed (exit code $nanoffExit)."
    exit $nanoffExit
}

Write-Host ""
Write-Host "Deploy complete!  Device is rebooting on $comPort." -ForegroundColor Green
Write-Host "Run .\scripts\Start-DevEnv.ps1 to watch MQTT output." -ForegroundColor Cyan

# Explicit success code: Run-IntegrationTests.ps1 checks $LASTEXITCODE after each deploy.
exit 0
