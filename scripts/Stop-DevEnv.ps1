<#
.SYNOPSIS
    Stop the local SmartHome development environment started by Start-DevEnv.ps1.

.DESCRIPTION
    Reads the state file Start-DevEnv.ps1 wrote for the configured MQTT port, stops
    the homie/# subscriber and the Mosquitto broker it recorded, and deletes the
    generated Mosquitto config.

    Safe to call unconditionally at the end of a test run: if nothing is recorded as
    running, it reports that and exits 0 rather than failing.

.PARAMETER KeepLog
    Leave the detached subscriber's homie/# log file in place. Without this the log
    is deleted along with the rest of the environment.

.EXAMPLE
    .\scripts\Stop-DevEnv.ps1
    .\scripts\Stop-DevEnv.ps1 -KeepLog
#>

[CmdletBinding()]
param(
    [switch]$KeepLog
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Common.ps1')
Import-SmartHomeLocalEnv

$mqttPort = Get-OptionalEnvValue -Name 'SMARTHOME_MQTT_PORT' -DefaultValue '1883'
$state = Get-SmartHomeDevEnvState -Port $mqttPort

if ($null -eq $state) {
    Write-Host "No dev environment recorded as running for port $mqttPort. Nothing to stop." -ForegroundColor DarkGray
    exit 0
}

function Stop-RecordedProcess {
    param(
        [string]$Label,
        $ProcessId
    )

    if ($null -eq $ProcessId) {
        return
    }

    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        Write-Host ("  {0} (PID {1}) already gone." -f $Label, $ProcessId) -ForegroundColor DarkGray
        return
    }

    Write-Host ("  Stopping {0} (PID {1})..." -f $Label, $ProcessId) -ForegroundColor Yellow
    Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
}

Write-Host "Stopping the local dev environment (port $mqttPort)..." -ForegroundColor Cyan
Stop-RecordedProcess -Label 'homie/# subscriber' -ProcessId $state.SubscriberPid
Stop-RecordedProcess -Label 'Mosquitto broker' -ProcessId $state.BrokerPid

if ($state.ConfigFile) {
    Remove-Item -Path $state.ConfigFile -Force -ErrorAction SilentlyContinue
}

if ($state.LogFile -and -not $KeepLog) {
    Remove-Item -Path $state.LogFile -Force -ErrorAction SilentlyContinue
}
elseif ($state.LogFile) {
    Write-Host ("  Kept homie/# log: {0}" -f $state.LogFile) -ForegroundColor DarkGray
}

Clear-SmartHomeDevEnvState -Port $mqttPort

Write-Host "Dev environment stopped." -ForegroundColor Green

exit 0
