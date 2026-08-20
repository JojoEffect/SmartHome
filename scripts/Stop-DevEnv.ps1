<#
.SYNOPSIS
    Stop the local SmartHome development environment started by Start-DevEnv.ps1.

.DESCRIPTION
    Reads the state file Start-DevEnv.ps1 wrote for the configured MQTT port, stops
    the homie/# subscriber and the Mosquitto broker it recorded, and deletes the
    generated Mosquitto config and log files.

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
    Also stop Mosquitto processes that this repo started but no state file covers --
    a run from before the state file existed, or one whose shell was killed hard.
    They are identified by the generated `smarthome-mosquitto-<port>.conf` on their
    command line, so a broker started by anything else is never touched.

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
$state = Get-SmartHomeDevEnvState -Port $mqttPort

$stoppedSomething = $false

if ($state) {
    Write-Host "Stopping the local dev environment (port $mqttPort)..." -ForegroundColor Cyan

    if (Test-SmartHomeDevEnvRunning -State $state) {
        $stoppedSomething = $true
    }

    # Subscriber first: stopping the broker out from under it would make it log a
    # connection error on the way out, for no reason.
    Stop-SmartHomeRecordedProcess -Record (Get-SmartHomeRecordValue -Record $state -Name 'Subscriber') -Label 'homie/# subscriber'
    Stop-SmartHomeRecordedProcess -Record (Get-SmartHomeRecordValue -Record $state -Name 'Broker') -Label 'Mosquitto broker'

    $configFile = Get-SmartHomeRecordValue -Record $state -Name 'ConfigFile'
    if ($configFile) {
        Remove-Item -Path $configFile -Force -ErrorAction SilentlyContinue
    }

    # LogFiles is the current field; LogFile is what older state files carried.
    $logFiles = @(Get-SmartHomeRecordValue -Record $state -Name 'LogFiles')
    if ($logFiles.Count -eq 0) {
        $single = Get-SmartHomeRecordValue -Record $state -Name 'LogFile'
        if ($single) {
            $logFiles = @($single)
        }
    }

    $logFiles = @($logFiles | Where-Object { $_ })
    if ($logFiles.Count -gt 0) {
        if ($KeepLog) {
            foreach ($logFile in $logFiles) {
                if (Test-Path $logFile) {
                    Write-Host ("  Kept log: {0}" -f $logFile) -ForegroundColor DarkGray
                }
            }
        }
        else {
            foreach ($logFile in $logFiles) {
                Remove-Item -Path $logFile -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Clear-SmartHomeDevEnvState -Port $mqttPort
}
else {
    Write-Host "No dev environment recorded as running for port $mqttPort." -ForegroundColor DarkGray
}

if ($IncludeOrphans) {
    # Subscribers first, same reason as above.
    $orphans = @(Get-SmartHomeOrphanSubscriber) + @(Get-SmartHomeOrphanBroker)
    if ($orphans.Count -eq 0) {
        Write-Host "No orphaned SmartHome broker or subscriber processes found." -ForegroundColor DarkGray
    }
    foreach ($orphan in $orphans) {
        Write-Host ("  Stopping orphaned {0} (PID {1})..." -f $orphan.Name, $orphan.ProcessId) -ForegroundColor Yellow
        & taskkill.exe /PID $orphan.ProcessId /T /F *> $null
        $stoppedSomething = $true
    }
}
elseif (-not $state) {
    # Don't leave someone staring at "nothing to stop" while a broker this repo
    # started is demonstrably still holding the port.
    $orphans = @(Get-SmartHomeOrphanBroker)
    if ($orphans.Count -gt 0) {
        Write-Warning ("Found {0} Mosquitto process(es) started by this repo that no state file covers (PID {1}). Re-run with -IncludeOrphans to stop them." -f $orphans.Count, (($orphans | ForEach-Object { $_.ProcessId }) -join ', '))
    }
}

if ($stoppedSomething) {
    Write-Host "Dev environment stopped." -ForegroundColor Green
}
else {
    Write-Host "Nothing was running." -ForegroundColor DarkGray
}

exit 0
