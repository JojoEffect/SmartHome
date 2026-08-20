<#
.SYNOPSIS
    Start the local SmartHome development environment (sibling repo sync + Mosquitto).

.DESCRIPTION
    1. Syncs the nanoFramework companion repositories beside SmartHome (skip with -NoSync).
    2. Sources scripts\local.env.ps1 for machine-specific settings.
    3. Starts Mosquitto as a background process (not the Windows service), with its
       own output captured to a log file.
    4. Streams all Homie device messages to the console -- or, with -Detached, leaves
       both the broker and the subscriber running in the background and returns
       immediately, so a script can keep working and call Stop-DevEnv.ps1 afterwards.

    This is the single dev-environment entry point: it absorbed the old
    Start-AgentWorkspace.ps1, which was nothing more than
    Sync-NanoFrameworkRepos.ps1 followed by this script.

    Background children are deliberately NOT started with Start-Process's own
    -RedirectStandardOutput/-RedirectStandardError. Those switches force
    UseShellExecute=false, and .NET then creates the process with
    bInheritHandles=TRUE -- which hands the child EVERY inheritable handle this
    process holds, including its stdout. When this script's own output is piped
    somewhere (`... | tail`, `| Select-Object -First 3`), that inherited handle
    keeps the pipe's write end open after the script exits, and the reader blocks
    forever waiting for an EOF that never comes. Redirecting the standard streams
    does not help: the extra handle is inherited alongside them.

    So each child is started through ShellExecute instead (-WindowStyle, no
    -Redirect*), which does not inherit handles, and writes its own log:
      - the broker via Mosquitto's own `log_dest file` config directive
      - the subscriber via a cmd.exe wrapper that does the `>` redirect itself
    The wrapper means the recorded subscriber pid is cmd's, so it is stopped as a
    process TREE rather than a single pid.

    File names and the subscriber's argument list come from Common.ps1, which also
    uses them to recognise leftover processes -- see the file-vocabulary section there.

.PARAMETER NoSync
    Skip the companion-repo sync and go straight to the broker. Use when the siblings
    are known current, or when working offline.

.PARAMETER Detached
    Start the broker and the homie/# subscriber in the background, write a state file
    recording their pids, and return. Subscriber output goes to a log file whose path
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

# ── Existing environment ──────────────────────────────────────────────────────
# A state file alone doesn't mean anything is running: a Ctrl+C at the wrong
# moment, a killed shell or a reboot all leave one behind. Refuse only when the
# recorded processes are genuinely alive; otherwise clear the stale file and
# carry on, so the common case never needs a manual cleanup step.
$existingState = Get-SmartHomeDevEnvState -Port $mqttPort
if ($existingState) {
    if (Test-SmartHomeDevEnvRunning -State $existingState) {
        Write-Error @"
A dev environment is already running for port $mqttPort.
Stop it first:  .\scripts\Stop-DevEnv.ps1
"@
        exit 1
    }

    Write-Host "Clearing a stale dev-env state file (its processes are gone)." -ForegroundColor DarkGray
    Clear-SmartHomeDevEnvState -Port $mqttPort
}

$mosquittoConf     = Get-SmartHomeDevEnvPath -Port $mqttPort -Kind Config
$brokerLog         = Get-SmartHomeDevEnvPath -Port $mqttPort -Kind BrokerLog
$subscriberLog     = Get-SmartHomeDevEnvPath -Port $mqttPort -Kind SubscriberLog
$subscriberErrLog  = Get-SmartHomeDevEnvPath -Port $mqttPort -Kind SubscriberErrorLog

Write-Host ("Starting Mosquitto broker on port {0} ..." -f $mqttPort) -ForegroundColor Cyan

# Mosquitto 2.x binds to localhost only unless a listener is set explicitly
# via a config file -- `-p` alone is not enough for devices on the LAN (like
# the ESP32) to reach it. Declaring an explicit listener also switches
# Mosquitto's default to requiring auth, so allow_anonymous is needed too.
#
# log_dest file is how the broker's own output reaches a file without this
# script redirecting its streams (see the handle-inheritance note in the header).
# Mosquitto reads the rest of the line as the value, so an unquoted path with
# spaces is fine here.
@"
listener $mqttPort 0.0.0.0
allow_anonymous true
log_dest file $brokerLog
log_type all
"@ | Set-Content -Path $mosquittoConf -Encoding ascii

Remove-Item -Path $brokerLog -Force -ErrorAction SilentlyContinue

$broker = Start-Process -FilePath $mosquittoExe `
                        -ArgumentList @('-c', $mosquittoConf, '-v') `
                        -PassThru `
                        -WindowStyle Hidden

# Poll for the outcome rather than sleeping a flat 2s: the broker announces its
# listen socket in the log about 120ms after launch, and a bind failure exits
# almost as fast. A fixed wait is both slower on every start and less reliable on
# a machine that needs longer than the window.
$deadline = (Get-Date).AddSeconds(5)
$brokerReady = $false
while ((Get-Date) -lt $deadline) {
    if ($broker.HasExited) {
        break
    }

    $log = Get-Content -Path $brokerLog -ErrorAction SilentlyContinue
    if ($log -match 'listen socket|mosquitto version .* running') {
        $brokerReady = $true
        break
    }

    Start-Sleep -Milliseconds 100
}

# Mosquitto exits immediately when it can't bind (another broker -- a leftover
# instance, or the Windows service -- already owns the port). Start-Process still
# hands back a process object for that dead process, so without this check the
# script would report success, the subscriber would silently attach to whatever
# OTHER broker is listening, and Stop-DevEnv.ps1 would later "stop" a process
# that never ran.
if (-not $brokerReady) {
    $occupant = Get-NetTCPConnection -LocalPort $mqttPort -State Listen -ErrorAction SilentlyContinue |
        Select-Object -First 1

    # Only quote the log when no other broker holds the port. Both instances log
    # to the same per-port path, so with an incumbent running that file holds ITS
    # healthy output -- printing it as "why we failed" is worse than saying
    # nothing.
    $reason = $null
    $ownerNote = if ($occupant) {
        $ownerProcess = Get-Process -Id $occupant.OwningProcess -ErrorAction SilentlyContinue
        $ownerName = if ($ownerProcess) { $ownerProcess.ProcessName } else { 'unknown process' }
        "Port $mqttPort is already held by $ownerName (PID $($occupant.OwningProcess)) on $($occupant.LocalAddress)."
    }
    else {
        $reason = (Get-Content -Path $brokerLog -Tail 5 -ErrorAction SilentlyContinue) -join "`n"
        'Nothing else appears to be listening on that port.'
    }

    $orphanNote = if (@(Get-SmartHomeOrphanProcess -Port $mqttPort).Count -gt 0) {
        "That is a Mosquitto this repo started earlier; clear it with:  .\scripts\Stop-DevEnv.ps1 -IncludeOrphans"
    }
    else {
        "If it isn't yours, set SMARTHOME_MQTT_PORT in local.env.ps1 to a free port."
    }

    if (-not $broker.HasExited) {
        Stop-Process -Id $broker.Id -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -Path $mosquittoConf -Force -ErrorAction SilentlyContinue

    Write-Error @"
Mosquitto did not come up on port $mqttPort -- the broker is NOT running.
$ownerNote
$orphanNote
$(if ($reason) { "Broker output:`n$reason" })
"@
    exit 1
}

$processes = @(New-SmartHomeProcessRecord -Label 'Mosquitto broker' -Process $broker)
$logFiles = @($brokerLog)

Write-Host ("  Broker PID: {0}" -f $broker.Id) -ForegroundColor Green
Write-Host ("  Listening on 0.0.0.0:{0} (reachable from other devices on the LAN)" -f $mqttPort) -ForegroundColor DarkGray
Write-Host ("  Broker log: {0}" -f $brokerLog) -ForegroundColor DarkGray

if ($Detached) {
    Remove-Item -Path $subscriberLog, $subscriberErrLog -Force -ErrorAction SilentlyContinue

    # cmd.exe does the redirect so this script doesn't have to (see header). The
    # outer pair of quotes is what cmd /c needs to keep the inner quoted paths
    # intact.
    $subscriberArgs = (Get-SmartHomeSubscriberArguments -Port $mqttPort |
        ForEach-Object { if ($_ -match '[\s/#]') { '"{0}"' -f $_ } else { $_ } }) -join ' '
    $subscriberCommand = '/c ""{0}" {1} > "{2}" 2> "{3}""' -f `
        $mosquittoSub, $subscriberArgs, $subscriberLog, $subscriberErrLog

    $subscriber = Start-Process -FilePath 'cmd.exe' `
                                -ArgumentList $subscriberCommand `
                                -PassThru `
                                -WindowStyle Hidden

    # Short window on purpose. It catches an instant death (bad exe, bad arguments)
    # and costs almost nothing when the subscriber is healthy. It deliberately does
    # NOT wait out a failed connect: mosquitto_sub takes ~4.2s to give up on a dead
    # port (measured), so covering that case would add ~4.5s to every single start
    # to catch something that cannot really happen here -- the broker is on
    # localhost and its own log already confirmed it is listening. If the
    # subscriber does die later, Stop-DevEnv.ps1 reports the pid as already gone
    # and the empty homie/# log is the visible symptom.
    if ($subscriber.WaitForExit(300)) {
        $reason = (Get-Content -Path $subscriberErrLog -Tail 5 -ErrorAction SilentlyContinue) -join "`n"
        Stop-Process -Id $broker.Id -Force -ErrorAction SilentlyContinue
        Remove-Item -Path $mosquittoConf -Force -ErrorAction SilentlyContinue

        Write-Error @"
The homie/# subscriber exited immediately (exit code $($subscriber.ExitCode)); broker stopped again.
$(if ($reason) { "Subscriber output:`n$reason" } else { 'It produced no output. Check that mosquitto_sub can reach localhost:' + $mqttPort + '.' })
"@
        exit 1
    }

    $processes += New-SmartHomeProcessRecord -Label 'homie/# subscriber' -Process $subscriber -Tree
    $logFiles += $subscriberLog, $subscriberErrLog
}

# Foreground mode records state too, so a Stop-DevEnv.ps1 from another shell (or
# a Ctrl+C that killed this one mid-cleanup) can still clean up the broker.
Save-SmartHomeDevEnvState -Port $mqttPort -State @{
    Processes  = $processes
    ConfigFile = $mosquittoConf
    LogFiles   = $logFiles
}

if ($Detached) {
    Write-Host ""
    Write-Host ("Dev environment running detached. homie/# log: {0}" -f $subscriberLog) -ForegroundColor Green
    Write-Host "Stop it with:  .\scripts\Stop-DevEnv.ps1" -ForegroundColor Cyan
    exit 0
}

Write-Host ""
Write-Host ("Subscribing to homie/# on localhost:{0}  (Ctrl+C to stop)" -f $mqttPort) -ForegroundColor Cyan
Write-Host ('-' * 69)

try {
    & $mosquittoSub @(Get-SmartHomeSubscriberArguments -Port $mqttPort)
}
finally {
    Write-Host ""
    Stop-SmartHomeDevEnv -Port $mqttPort | Out-Null
}
