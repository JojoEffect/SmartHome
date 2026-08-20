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

    # Every deployed image gets padded with trailing 0xFF (erased-flash value)
    # bytes up to this size before flashing. nanoff's erase+write only covers
    # the image file's own byte length, not the full "deploy" partition -- so
    # deploying a SMALLER app after a LARGER one leaves that previous app's
    # trailing assembly bytes sitting unerased past the new image's end, and
    # the CLR loads BOTH on boot (confirmed: saw WifiTest's own small assembly
    # list plus leftover BMP280Test assemblies in the same resolution pass,
    # failing to link because bytes past WifiTest's real end were stale
    # BMP280Test data, not blank flash). Padding to a fixed size makes every
    # deploy erase+write the same footprint regardless of app size. 400KB
    # comfortably covers every project in this repo today; the partition
    # itself is 0x1C0000 (~1.75MB), so there's plenty of headroom to raise
    # this if a future project's image ever gets close to it.
    [int]$PaddedImageSize = 409600
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
$projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)

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
if ($imageBytes.Length -gt $PaddedImageSize) {
    Write-Error "Deploy image ($($imageBytes.Length) bytes) is larger than -PaddedImageSize ($PaddedImageSize bytes). Raise -PaddedImageSize."
    exit 1
}

$paddedImage = Join-Path $binDir ($projectName + '.padded.bin')
$padded = New-Object byte[] $PaddedImageSize
[Array]::Copy($imageBytes, $padded, $imageBytes.Length)
for ($i = $imageBytes.Length; $i -lt $PaddedImageSize; $i++) {
    $padded[$i] = 0xFF
}
[System.IO.File]::WriteAllBytes($paddedImage, $padded)
Write-Host "  Padded deploy image: $($imageBytes.Length) -> $PaddedImageSize bytes (0xFF fill, clears any stale prior deployment)." -ForegroundColor DarkGray

nanoff --deploy --serialport $comPort --image "$paddedImage" --address $DeployAddress
if ($LASTEXITCODE -ne 0) {
    Write-Error "nanoff deploy failed (exit code $LASTEXITCODE)."
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Deploy complete!  Device is rebooting on $comPort." -ForegroundColor Green
Write-Host "Run .\scripts\Start-DevEnv.ps1 to watch MQTT output." -ForegroundColor Cyan

# Explicit success code: Run-IntegrationTests.ps1 checks $LASTEXITCODE after each deploy.
exit 0
