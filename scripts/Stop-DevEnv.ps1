<#
.SYNOPSIS
    Stop the local SmartHome development environment started by Start-DevEnv.ps1.

.DESCRIPTION
    Thin wrapper around Stop-SmartHomeDevEnv in Common.ps1, which is also what
    Start-DevEnv.ps1's own failure and Ctrl+C paths call -- so there is exactly one
    implementation of "stop the dev environment".

    It stops the processes recorded in the state file for the configured MQTT port
    and deletes the generated Mosquitto config and log files.

    Safe to call unconditionally at the end of a test run: if nothing is recorded as
    running, it reports that and exits 0 rather than failing.

    Processes are matched on pid AND name AND start time before anything is stopped.
    Windows recycles pids, so a state file left behind by a crash can easily name a
    pid that now belongs to something else -- that case is reported and skipped, not
    killed.

.PARAMETER KeepLog
    Leave the log files in place (the detached subscriber's homie/# capture and the
    broker's own log). Without this they are deleted along with the rest of the
    environment.

.PARAMETER IncludeOrphans
    Also stop broker/subscriber processes that this repo started but no state file
    covers -- a run from before the state file existed, or one whose shell was killed
    hard. They are identified by the dev-env file vocabulary in Common.ps1, so a
    broker started by anything else is never touched. Without the switch, finding
    such processes produces a warning rather than a silent "nothing to stop".

.EXAMPLE
    .\scripts\Stop-DevEnv.ps1
    .\scripts\Stop-DevEnv.ps1 -KeepLog
    .\scripts\Stop-DevEnv.ps1 -IncludeOrphans
#>

[CmdletBinding()]
param(
    [switch]$KeepLog,

    [switch]$IncludeOrphans
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Common.ps1')
Import-SmartHomeLocalEnv

$mqttPort = Get-OptionalEnvValue -Name 'SMARTHOME_MQTT_PORT' -DefaultValue '1883'

if (Stop-SmartHomeDevEnv -Port $mqttPort -KeepLog:$KeepLog -IncludeOrphans:$IncludeOrphans) {
    Write-Host "Dev environment stopped." -ForegroundColor Green
}
else {
    Write-Host "Nothing was running for port $mqttPort." -ForegroundColor DarkGray
}

exit 0
