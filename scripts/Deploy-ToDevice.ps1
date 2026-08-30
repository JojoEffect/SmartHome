<#
.SYNOPSIS
    Build and deploy RoomSensor to the ESP32 via nanoff.

.DESCRIPTION
    1. Sources scripts\local.env.ps1 for machine-specific settings.
    2. Builds the RoomSensor nanoFramework project with MSBuild.
    3. Flashes the compiled binaries to the device on the configured COM port
       using the nanoff CLI tool.

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
    .\scripts\Deploy-ToDevice.ps1 -FullPad   # after a Visual Studio deploy: pad the worst case
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

    # Deployed images get padded with trailing 0xFF (erased-flash value) bytes
    # before flashing. nanoff's erase+write only covers the image file's own byte
    # length, not the full "deploy" partition -- so deploying a SMALLER app after a
    # LARGER one leaves that previous app's trailing assembly bytes sitting unerased
    # past the new image's end, and the CLR loads BOTH on boot (confirmed: saw
    # WifiCheck's own small assembly list plus leftover Bmp280Check assemblies in
    # the same resolution pass, failing to link because bytes past WifiCheck's real
    # end were stale Bmp280Check data, not blank flash).
    #
    # How far to pad is decided per deploy, from the size the previous one recorded
    # for this COM port -- see the "Deploy state" section of Common.ps1 for the
    # invariant, and for why trailing blank flash does not terminate the CLR's scan
    # on its own. THIS value is the fallback used when no trustworthy record exists:
    # the flat size every deploy used to pay unconditionally. It has to stay large
    # enough to cover any image that could already be on the device, so lower it only
    # with the whole of src\devices and src\integrationTests in mind.
    [int]$FallbackPadSize = 409600,

    # Refuse to pad past the deploy partition; writing beyond it would run into the
    # "config" partition that starts where this one ends, at 0x3C0000. Measured
    # 2026-08-30 off the device the same way -DeployAddress above was -- the boot-log
    # partition table from .\scripts\Watch-DeviceSerial.ps1, this time the "deploy"
    # line's Length column rather than its Offset:
    #   3 deploy    Unknown data   01 84 001e0000 001c0000
    #   4 config    Unknown data   01 83 003c0000 00040000
    # (4MB flash, nanoCLR 1.17.0.339, ESP-IDF v5.5.4). Until then the value had only
    # ever been copied from a prose comment of unclear provenance -- issue #47.
    # Re-derive it there if a firmware update changes the layout.
    [int]$DeployPartitionSize = 0x1C0000,

    # Ignore any recorded size and pad to -FallbackPadSize. Use this after something
    # OTHER than this script has flashed the device -- a Visual Studio F5 deploy, most
    # likely -- since the record then describes an image that is no longer the one on
    # the device. Run-Tests.ps1 clears the record itself; Visual Studio cannot.
    [switch]$FullPad
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

# ── Decide how far to pad ─────────────────────────────────────────────────────
# Only the PREVIOUS image's footprint has to be covered, and Common.ps1's deploy
# state records exactly that, per COM port. Every reason the record cannot be
# trusted falls back to the flat -FallbackPadSize rather than to the bare image
# size: this is the flashing path, and over-padding costs seconds where
# under-padding costs a device booting somebody else's assemblies.
$sectorSize = 4096   # ESP32 flash erase granularity; keeps the write sector-aligned.

# Read even under -FullPad. That switch distrusts how TIGHT the record is -- something
# else has flashed the device since -- but a record proving a bigger image was written
# to this same address is still a lower bound, and honouring it is what keeps -FullPad
# the pessimistic option it is documented to be.
$deployState   = Get-SmartHomeDeployState -ComPort $comPort
$recordedBytes = $null
$recordReason  = $null

if ($null -eq $deployState) {
    $recordReason = "no usable deploy record for $comPort"
}
elseif ($deployState['DeployAddress'] -ne $DeployAddress) {
    # A different address is a different region: what that record describes says
    # nothing about what is sitting at this one -- not even as a floor.
    $recordReason = "the deploy record for $comPort covers $($deployState['DeployAddress']), not $DeployAddress"
}
elseif ($deployState['StaleBytes'] -gt $DeployPartitionSize) {
    # Parseable but impossible: nothing this script flashed can be larger than the
    # partition. Rejecting it here keeps a corrupt record costing one full-size deploy
    # -- the documented failure direction -- instead of tripping the partition check
    # below and refusing every deploy to this port until the file is deleted by hand.
    $recordReason = "the deploy record for $comPort claims $($deployState['StaleBytes']) bytes, larger than the deploy partition ($DeployPartitionSize)"
}
else {
    $recordedBytes = $deployState['StaleBytes']
}

$staleBytes = $null
$padReason  = $null

if ($FullPad) {
    $padReason = '-FullPad was passed'
}
elseif ($null -eq $recordedBytes) {
    $padReason = $recordReason
}
else {
    $staleBytes = $recordedBytes
}

# -FallbackPadSize is a worst case only while every image fits under it. An image that
# does not is padded to itself, which is correct for THIS deploy but leaves the fallback
# unable to cover it later -- after Run-Tests.ps1 clears the record, or under -FullPad.
if ($imageBytes.Length -gt $FallbackPadSize) {
    Write-Warning @"
This image is $($imageBytes.Length) bytes, larger than -FallbackPadSize ($FallbackPadSize).
It deploys correctly, but once anything drops the deploy record for $comPort (a hardware
Run-Tests.ps1 run, a corrupt record) the fallback no longer covers this image, and the next
deploy of a smaller app will leave its tail on the device. Raise -FallbackPadSize.
"@
}

$padFloor = if ($null -ne $staleBytes) {
    $staleBytes
}
elseif ($null -ne $recordedBytes) {
    # Falling back, but with a trustworthy floor to fall back to.
    [Math]::Max($FallbackPadSize, $recordedBytes)
}
else {
    $FallbackPadSize
}

$padTarget = [Math]::Max($imageBytes.Length, $padFloor)
$paddedImageSize = [int][Math]::Ceiling($padTarget / [double]$sectorSize) * $sectorSize

if ($paddedImageSize -gt $DeployPartitionSize) {
    Write-Error @"
Padded deploy image ($paddedImageSize bytes) does not fit the deploy partition ($DeployPartitionSize bytes).
The image itself is $($imageBytes.Length) bytes, padded up to a floor of $padFloor bytes. Either the
app has outgrown the partition, or -DeployPartitionSize is stale -- re-read the "deploy" line of the
partition table printed by .\scripts\Watch-DeviceSerial.ps1 and pass the real size.
"@
    exit 1
}

$paddedImage = Join-Path $binDir ($projectName + '.padded.bin')
$padded = New-Object byte[] $paddedImageSize

# Fill with 0xFF by doubling an already-filled prefix rather than looping over
# hundreds of KB one byte at a time: the scalar loop measured 560ms per deploy, this
# is ~10ms. ([Array]::Fill would be simpler but is .NET Core only, and this runs on
# Windows PowerShell 5.1.)
$padded[0] = 0xFF
$filled = 1
while ($filled -lt $paddedImageSize) {
    $chunk = [Math]::Min($filled, $paddedImageSize - $filled)
    [Array]::Copy($padded, 0, $padded, $filled, $chunk)
    $filled += $chunk
}

[Array]::Copy($imageBytes, $padded, $imageBytes.Length)
[System.IO.File]::WriteAllBytes($paddedImage, $padded)

# Names whichever input actually set the size: the pad floor only drives it while the
# image is smaller than the floor, and a message crediting the floor for a size the
# image chose misleads exactly the person reading this line to explain a pad.
$padDriver = if ($imageBytes.Length -ge $padFloor) {
    'the image itself is the larger of the two'
}
elseif ($null -ne $staleBytes) {
    "covering the $staleBytes bytes the last deploy to $comPort left on the device"
}
else {
    "full-size pad -- $padReason"
}
Write-Host "  Padded deploy image: $($imageBytes.Length) -> $paddedImageSize bytes (0xFF fill, $padDriver)." -ForegroundColor DarkGray

# Recorded before the flash, and pessimistically: if nanoff dies partway, or the shell
# is killed, anything up to $paddedImageSize may have landed and the next deploy has to
# cover all of it. Tightened below once nanoff reports success. Deliberately not
# wrapped -- $ErrorActionPreference is 'Stop', so a record that cannot be written
# aborts before flashing rather than flashing against a stale, smaller number.
Save-SmartHomeDeployState -ComPort $comPort `
                          -DeployAddress $DeployAddress `
                          -StaleBytes $paddedImageSize `
                          -Image $projectName

nanoff --deploy --serialport $comPort --image "$paddedImage" --address $DeployAddress
$nanoffExit = $LASTEXITCODE

# nanoff has read the image by now, so the padded copy is dead weight in bin\Debug.
# Leaving it also puts a stale .padded.bin next to a freshly built .bin, which is the
# same shape as the stale-deployment problem the padding exists to fix.
Remove-Item -Path $paddedImage -Force -ErrorAction SilentlyContinue

if ($nanoffExit -ne 0) {
    Write-Error "nanoff deploy failed (exit code $nanoffExit)."
    exit $nanoffExit
}

# The write completed, so everything from the image's end up to $paddedImageSize is
# 0xFF now and the next deploy only has to cover the image itself.
try {
    Save-SmartHomeDeployState -ComPort $comPort `
                              -DeployAddress $DeployAddress `
                              -StaleBytes $imageBytes.Length `
                              -Image $projectName
}
catch {
    # The pessimistic record written before the flash is still there and still
    # correct, just bigger than it needs to be. Not worth failing a deploy that worked.
    Write-Warning "Could not tighten the deploy record for $comPort ($($_.Exception.Message)). The next deploy will pad to $paddedImageSize bytes."
}

Write-Host ""
Write-Host "Deploy complete!  Device is rebooting on $comPort." -ForegroundColor Green
Write-Host "Run .\scripts\Start-DevEnv.ps1 to watch MQTT output." -ForegroundColor Cyan

# Explicit success code: Run-IntegrationTests.ps1 checks $LASTEXITCODE after each deploy.
exit 0
