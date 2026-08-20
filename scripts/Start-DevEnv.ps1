<#
.SYNOPSIS
    Start the local SmartHome development environment (sibling repo sync + Mosquitto).

.DESCRIPTION
    1. Syncs the nanoFramework companion repositories beside SmartHome (skip with -NoSync).
    2. Sources scripts\local.env.ps1 for machine-specific settings.
    3. Starts Mosquitto as a background process (not the Windows service).
    4. Streams all Homie device messages to the console -- or, with -Detached, leaves
       both the broker and the subscriber running in the background and returns
       immediately, so a script can keep working and call Stop-DevEnv.ps1 afterwards.

    This is the single dev-environment entry point: it absorbed the old
    Start-AgentWorkspace.ps1, which was nothing more than
    Sync-NanoFrameworkRepos.ps1 followed by this script.

.PARAMETER NoSync
    Skip the companion-repo sync and go straight to the broker. Use when the siblings
    are known current, or when working offline.

.PARAMETER Detached
    Start the broker and the homie/# subscriber in the background, write a state file
    recording their PIDs, and return. Subscriber output goes to a log file whose path
    is printed. Stop everything again with Stop-DevEnv.ps1.

.NOTES
    Copy scripts\local.env.template.ps1 to scripts\local.env.ps1 and fill in
    your COM port and Mosquitto installation path before running this script.
    The local.env.ps1 file is git-ignored and will never be committed.

.EXAMPLE
    .\scripts\Start-DevEnv.ps1
    .\scripts\Start-DevEnv.ps1 -NoSync
    .\scripts\Start-DevEnv.ps1 -Detached -NoSync; .\scripts\Stop-DevEnv.ps1
#>

[CmdletBinding()]
param(
    [switch]$NoSync,

    [switch]$Detached
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Common.ps1')

if (-not $NoSync) {
    Write-Host "Syncing nanoFramework companion repositories..." -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'Sync-NanoFrameworkRepos.ps1')
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Companion repo sync failed (exit code $LASTEXITCODE). Re-run with -NoSync to start the broker anyway."
        exit $LASTEXITCODE
    }
    Write-Host ""
}

Import-SmartHomeLocalEnv

$mosquittoDir = Get-RequiredEnvValue -Name 'SMARTHOME_MOSQUITTO_DIR'
$mqttPort = Get-OptionalEnvValue -Name 'SMARTHOME_MQTT_PORT' -DefaultValue '1883'
$mosquittoExe = Join-Path $mosquittoDir 'mosquitto.exe'
$mosquittoSub = Join-Path $mosquittoDir 'mosquitto_sub.exe'

foreach ($exe in @($mosquittoExe, $mosquittoSub)) {
    if (-not (Test-Path $exe)) {
        Write-Error ("Not found: {0}`nCheck SMARTHOME_MOSQUITTO_DIR in local.env.ps1." -f $exe)
        exit 1
    }
}

$stateFile = Get-SmartHomeDevEnvStateFile -Port $mqttPort
if (Test-Path $stateFile) {
    Write-Error @"
A dev environment is already recorded as running for port $mqttPort ($stateFile).
Stop it first:  .\scripts\Stop-DevEnv.ps1
"@
    exit 1
}

Write-Host ("Starting Mosquitto broker on port {0} ..." -f $mqttPort) -ForegroundColor Cyan

# Mosquitto 2.x binds to localhost only unless a listener is set explicitly
# via a config file -- `-p` alone is not enough for devices on the LAN (like
# the ESP32) to reach it. Declaring an explicit listener also switches
# Mosquitto's default to requiring auth, so allow_anonymous is needed too.
$mosquittoConf = Join-Path ([System.IO.Path]::GetTempPath()) "smarthome-mosquitto-$mqttPort.conf"
@"
listener $mqttPort 0.0.0.0
allow_anonymous true
"@ | Set-Content -Path $mosquittoConf -Encoding ascii

$mosquittoArgs = @('-c', $mosquittoConf, '-v')
$broker = Start-Process -FilePath $mosquittoExe `
                        -ArgumentList $mosquittoArgs `
                        -PassThru `
                        -WindowStyle Minimized
Start-Sleep -Seconds 2

# Mosquitto exits immediately when it can't bind (another broker -- a leftover
# instance, or the Windows service -- already owns the port). Start-Process still
# hands back a PID for that dead process, so without this check the script would
# report success, the subscriber would silently attach to whatever OTHER broker is
# listening, and Stop-DevEnv.ps1 would later "stop" a process that never ran.
if ($broker.HasExited) {
    Remove-Item -Path $mosquittoConf -Force -ErrorAction SilentlyContinue
    $owner = Get-NetTCPConnection -LocalPort $mqttPort -State Listen -ErrorAction SilentlyContinue |
        Select-Object -First 1
    $ownerNote = if ($owner) {
        "Port $mqttPort is already held by PID $($owner.OwningProcess) on $($owner.LocalAddress)."
    }
    else {
        "Check the Mosquitto window for its own error output."
    }

    Write-Error @"
Mosquitto exited immediately (exit code $($broker.ExitCode)) -- the broker is NOT running.
$ownerNote
Stop the other broker first, or set SMARTHOME_MQTT_PORT in local.env.ps1 to a free port.
If that other broker is a previous Start-DevEnv.ps1 run, .\scripts\Stop-DevEnv.ps1 clears it.
"@
    exit 1
}

Write-Host ("  Broker PID: {0}" -f $broker.Id) -ForegroundColor Green
Write-Host ("  Listening on 0.0.0.0:{0} (reachable from other devices on the LAN)" -f $mqttPort) -ForegroundColor DarkGray

if ($Detached) {
    $logFile = Join-Path ([System.IO.Path]::GetTempPath()) "smarthome-homie-$mqttPort.log"
    Remove-Item -Path $logFile -Force -ErrorAction SilentlyContinue

    $subscriber = Start-Process -FilePath $mosquittoSub `
                                -ArgumentList @('-h', 'localhost', '-p', $mqttPort, '-t', 'homie/#', '-v') `
                                -PassThru `
                                -WindowStyle Hidden `
                                -RedirectStandardOutput $logFile

    Save-SmartHomeDevEnvState -Port $mqttPort -State @{
        BrokerPid     = $broker.Id
        SubscriberPid = $subscriber.Id
        ConfigFile    = $mosquittoConf
        LogFile       = $logFile
    }

    Write-Host ""
    Write-Host ("Dev environment running detached. homie/# log: {0}" -f $logFile) -ForegroundColor Green
    Write-Host "Stop it with:  .\scripts\Stop-DevEnv.ps1" -ForegroundColor Cyan
    exit 0
}

# Foreground mode still records state, so a Stop-DevEnv.ps1 from another shell
# (or after a Ctrl+C that killed this one mid-cleanup) can clean up the broker.
Save-SmartHomeDevEnvState -Port $mqttPort -State @{
    BrokerPid     = $broker.Id
    SubscriberPid = $null
    ConfigFile    = $mosquittoConf
    LogFile       = $null
}

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
    Remove-Item -Path $mosquittoConf -Force -ErrorAction SilentlyContinue
    Clear-SmartHomeDevEnvState -Port $mqttPort
}
