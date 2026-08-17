<#
.SYNOPSIS
    Start the local SmartHome development environment.

.DESCRIPTION
    1. Sources scripts\local.env.ps1 for machine-specific settings.
    2. Starts Mosquitto as a background process (not the Windows service).
    3. Opens mosquitto_sub to stream all Homie device messages to the console.

.NOTES
    Copy scripts\local.env.template.ps1 to scripts\local.env.ps1 and fill in
    your COM port and Mosquitto installation path before running this script.
    The local.env.ps1 file is git-ignored and will never be committed.

.EXAMPLE
    .\scripts\Start-DevEnv.ps1
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Locate the repo root (one level up from the scripts folder) ──────────────
$repoRoot   = Split-Path $PSScriptRoot -Parent
$localEnv   = Join-Path $PSScriptRoot 'local.env.ps1'
$template   = Join-Path $PSScriptRoot 'local.env.template.ps1'

if (-not (Test-Path $localEnv)) {
    Write-Error @"
Missing: $localEnv
Copy the template and fill in your machine settings:
    Copy-Item "$template" "$localEnv"
Then edit local.env.ps1 before running this script.
"@
    exit 1
}

. $localEnv   # dot-source to populate env vars

$mosquittoDir  = $env:SMARTHOME_MOSQUITTO_DIR
$mqttBroker    = if ($env:SMARTHOME_MQTT_BROKER) { $env:SMARTHOME_MQTT_BROKER } else { 'localhost' }
$mqttPort      = if ($env:SMARTHOME_MQTT_PORT)   { $env:SMARTHOME_MQTT_PORT }   else { '1883' }
$mosquittoExe  = Join-Path $mosquittoDir 'mosquitto.exe'
$mosquittoSub  = Join-Path $mosquittoDir 'mosquitto_sub.exe'

foreach ($exe in $mosquittoExe, $mosquittoSub) {
    if (-not (Test-Path $exe)) {
        Write-Error "Not found: $exe`nCheck SMARTHOME_MOSQUITTO_DIR in local.env.ps1."
        exit 1
    }
}

# ── Start Mosquitto broker ───────────────────────────────────────────────────
Write-Host "Starting Mosquitto broker on port $mqttPort ..." -ForegroundColor Cyan
$mosquittoArgs = @('-p', $mqttPort, '-v')
$broker = Start-Process -FilePath $mosquittoExe `
                        -ArgumentList $mosquittoArgs `
                        -PassThru `
                        -WindowStyle Minimized
Write-Host "  Broker PID: $($broker.Id)" -ForegroundColor Green

# Give it a moment to bind the port
Start-Sleep -Seconds 2

# ── Subscribe to all Homie topics ────────────────────────────────────────────
Write-Host ""
Write-Host "Subscribing to homie/# on $mqttBroker:$mqttPort  (Ctrl+C to stop)" -ForegroundColor Cyan
Write-Host "─────────────────────────────────────────────────────────────────────"

try {
    & $mosquittoSub -h $mqttBroker -p $mqttPort -t 'homie/#' -v
}
finally {
    # Clean up the broker when the user exits the subscriber
    if (-not $broker.HasExited) {
        Write-Host "`nStopping Mosquitto (PID $($broker.Id))..." -ForegroundColor Yellow
        Stop-Process -Id $broker.Id -Force
    }
}
