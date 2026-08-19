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

. (Join-Path $PSScriptRoot 'Common.ps1')
Import-SmartHomeLocalEnv

$mosquittoDir = Get-RequiredEnvValue -Name 'SMARTHOME_MOSQUITTO_DIR'
$mqttBroker = Get-OptionalEnvValue -Name 'SMARTHOME_MQTT_BROKER' -DefaultValue 'localhost'
$mqttPort = Get-OptionalEnvValue -Name 'SMARTHOME_MQTT_PORT' -DefaultValue '1883'
$mosquittoExe = Join-Path $mosquittoDir 'mosquitto.exe'
$mosquittoSub = Join-Path $mosquittoDir 'mosquitto_sub.exe'

foreach ($exe in @($mosquittoExe, $mosquittoSub)) {
    if (-not (Test-Path $exe)) {
        Write-Error ("Not found: {0}`nCheck SMARTHOME_MOSQUITTO_DIR in local.env.ps1." -f $exe)
        exit 1
    }
}

Write-Host ("Starting Mosquitto broker on port {0} ..." -f $mqttPort) -ForegroundColor Cyan
$mosquittoArgs = @('-p', $mqttPort, '-v')
$broker = Start-Process -FilePath $mosquittoExe `
                        -ArgumentList $mosquittoArgs `
                        -PassThru `
                        -WindowStyle Minimized
Write-Host ("  Broker PID: {0}" -f $broker.Id) -ForegroundColor Green

Start-Sleep -Seconds 2

Write-Host ""
Write-Host ("Subscribing to homie/# on localhost:{0}  (Ctrl+C to stop)" -f $mqttPort) -ForegroundColor Cyan
Write-Host ('-' * 69)

try {
    & $mosquittoSub -h 'localhost' -p $mqttPort -t 'homie/#' -v
}
finally {
    if (-not $broker.HasExited) {
        Write-Host ("`nStopping Mosquitto (PID {0})..." -f $broker.Id) -ForegroundColor Yellow
        Stop-Process -Id $broker.Id -Force
    }
}
