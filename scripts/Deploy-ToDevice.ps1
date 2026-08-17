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
    Deploy from the Build menu.  Visual Studio uses the same nanoff mechanism
    under the hood and also attaches the managed debugger.

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
    [string]$Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Config ────────────────────────────────────────────────────────────────────
$repoRoot    = Split-Path $PSScriptRoot -Parent
$localEnv    = Join-Path $PSScriptRoot 'local.env.ps1'
$template    = Join-Path $PSScriptRoot 'local.env.template.ps1'

if (-not (Test-Path $localEnv)) {
    Write-Error @"
Missing: $localEnv
Copy the template and fill in your machine settings:
    Copy-Item "$template" "$localEnv"
"@
    exit 1
}

. $localEnv

$comPort     = $env:SMARTHOME_COM_PORT
$projectPath = Join-Path $repoRoot $Project

if (-not (Test-Path $projectPath)) {
    Write-Error "Project not found: $projectPath"
    exit 1
}

# ── Locate MSBuild ────────────────────────────────────────────────────────────
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = if (Test-Path $vswhere) {
    & $vswhere -latest -requires Microsoft.Component.MSBuild `
               -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null |
    Select-Object -First 1
} else { $null }

if (-not $msbuild) { $msbuild = 'msbuild' }   # fall back to PATH

# ── Build ─────────────────────────────────────────────────────────────────────
Write-Host "Building $Project ($Configuration)..." -ForegroundColor Cyan
& $msbuild $projectPath /p:Configuration=$Configuration /v:minimal /nologo
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed (exit code $LASTEXITCODE)."
    exit $LASTEXITCODE
}
Write-Host "  Build succeeded." -ForegroundColor Green

# ── Locate build output ───────────────────────────────────────────────────────
$projectDir = Split-Path $projectPath -Parent
$binDir     = Join-Path $projectDir "bin\$Configuration"

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

nanoff --deploy --serialport $comPort --binfile "$binDir"
if ($LASTEXITCODE -ne 0) {
    Write-Error "nanoff deploy failed (exit code $LASTEXITCODE)."
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Deploy complete!  Device is rebooting on $comPort." -ForegroundColor Green
Write-Host "Run .\scripts\Start-DevEnv.ps1 to watch MQTT output." -ForegroundColor Cyan
