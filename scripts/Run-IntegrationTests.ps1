<#
.SYNOPSIS
    Run the whole on-device integration test suite in one call.

.DESCRIPTION
    One entry point for every project under src\integrationTests. Every test is
    deployed to the ESP32 first; how its verdict is reached depends on its kind:

      DeviceMarker  the device decides. The runner reboots it, captures managed
                    debug output, and reads the "[ITEST] <name> PASS/FAIL" marker
                    the test emits (see
                    src\integrationTests\TestSupport\IntegrationTest.cs).

      BrokerOutage  the host decides. The device just publishes a heartbeat; the
                    runner takes the broker away, brings it back, and asserts that
                    heartbeats reappear on homie/#. A device claiming it
                    reconnected is weaker evidence than a message actually
                    arriving at the recreated broker.

    A local Mosquitto broker is started detached for the run and stopped again at
    the end, even if the suite fails.

    On success this prints a one-line-per-test summary and exits 0 -- nothing else to
    look at. On failure it exits 1 and prints the captured device log path for the
    failing test, which is where any real investigation starts. Alongside it in the same
    directory are the broker's own log and the homie/# log for every broker generation
    the run went through, kept because the dev environment deletes both at teardown.

    *** HARDWARE: this flashes and runs code on the physical device on the configured
    COM port, once per test. Treat it exactly like Deploy-ToDevice.ps1. ***

.PARAMETER Tests
    Subset of tests to run, by name. Defaults to every entry of $testCatalog below,
    in dependency order -- WiFi first, since the MQTT checks can only fail
    confusingly if the network itself is broken.

.PARAMETER Configuration
    Build configuration passed through to Deploy-ToDevice.ps1. Default Debug.

.PARAMETER NoBroker
    Don't start/stop Mosquitto. Use when a broker is already running (started by
    Start-DevEnv.ps1, or a service), or when running only non-MQTT tests. BrokerOutage
    tests cannot run under this switch -- they need to own the broker's lifetime, and
    tearing down someone else's broker is not theirs to do.

.PARAMETER LogDirectory
    Where to write the per-test device logs, and the preserved broker and homie/# logs
    that go with them. Defaults to a timestamped folder under the system temp directory;
    the path is printed at the end of the run.

.EXAMPLE
    .\scripts\Run-IntegrationTests.ps1
    .\scripts\Run-IntegrationTests.ps1 -Tests WifiCheck,MqttCheck
    .\scripts\Run-IntegrationTests.ps1 -NoBroker -Configuration Release
#>

[CmdletBinding()]
param(
    [string[]]$Tests,

    [string]$Configuration = 'Debug',

    [switch]$NoBroker,

    [string]$LogDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Common.ps1')
Import-SmartHomeLocalEnv

$repoRoot = Get-SmartHomeRepoRoot
$mqttPort = Get-OptionalEnvValue -Name 'SMARTHOME_MQTT_PORT' -DefaultValue '1883'

# The catalog is the single source of truth for what the suite runs: the -Tests
# default and its validation both come from here, and each project's path follows
# from its name (src\integrationTests\<Name>\<Name>.nfproj). Adding a test is one
# line here plus the project itself.
#
# CaptureSeconds is a window -- "long enough that a healthy device has already
# reported", not how long the test takes. Every DeviceMarker test emits its marker
# as soon as the outcome is known and then idles. WifiCheck gets the longest window
# because NetworkHelper's own connect timeout is 60s.
$testCatalog = [ordered]@{
    'WifiCheck'   = @{ Kind = 'DeviceMarker'; CaptureSeconds = 75 }
    'MqttCheck'   = @{ Kind = 'DeviceMarker'; CaptureSeconds = 90 }
    'Bmp280Check' = @{ Kind = 'DeviceMarker'; CaptureSeconds = 45 }
    'HomieClientCheck' = @{
        Kind = 'HomieConformance'
        # The device this check deploys is built for the convention, not for a room:
        # one property of every datatype, settable and not, retained and not. These
        # must match src\integrationTests\HomieClientCheck\Program.cs.
        DeviceId = 'homie-client-check'
        NodeId = 'matrix'
        SettleSeconds = 90
        CommandTimeoutSeconds = 30
        RecoverySeconds = 90
    }
    'MqttReconnectCheck' = @{
        Kind = 'BrokerOutage'
        # Topic the device publishes its heartbeat on. Must match HeartbeatTopic in
        # that project's Program.cs -- the pre-flight below checks that it does.
        HeartbeatTopic = 'homie/mqtt-reconnect-check/heartbeat'
        # Time to reach the first heartbeat after a flash: boot + WiFi + connect.
        SettleSeconds = 90
        # Outage lengths to test, in order. The first is shorter than one 5s
        # reconnect cycle, the second spans several failed attempts. On Windows there
        # is no graceful mosquitto shutdown available to us (Stop-Process is
        # TerminateProcess either way), so outage length is the real variable.
        OutageSeconds = @(3, 20)
        # How long to wait for heartbeats to resume once the broker is back.
        RecoverySeconds = 90
        # The device subscribes to EchoCommandTopic and republishes whatever arrives to
        # EchoTopic. Heartbeats only prove the connection came back; this pair is what
        # proves the *subscriptions* were replayed with it. Must match the constants in
        # that project's Program.cs.
        EchoCommandTopic = 'homie/mqtt-reconnect-check/echo/set'
        EchoTopic = 'homie/mqtt-reconnect-check/echo'
        # How long to wait for the echo to come back.
        CommandTimeoutSeconds = 30
    }
}

if (-not $Tests) {
    $Tests = @($testCatalog.Keys)
}

$unknown = @($Tests | Where-Object { -not $testCatalog.Contains($_) })
if ($unknown.Count -gt 0) {
    Write-Error ("Unknown test(s): {0}. Known tests: {1}." -f ($unknown -join ', '), (@($testCatalog.Keys) -join ', '))
    exit 1
}

# Validate the catalog before anything is built or flashed. Under
# Set-StrictMode -Version Latest a missing key read as $settings.Foo throws
# PropertyNotFoundException -- which surfaces 90s into a run as Outcome 'ERROR' with a
# property-not-found message, rather than as the one-line configuration mistake it is.
# Switching those reads to $settings['Foo'] would be worse, not better: that returns
# $null, so a forgotten CaptureSeconds would silently become a zero-length window.
# Adding a test is advertised above as "one line here", so that line gets checked.
$requiredCatalogKeys = @{
    'DeviceMarker'     = @('CaptureSeconds')
    'HomieConformance' = @('DeviceId', 'NodeId', 'SettleSeconds', 'CommandTimeoutSeconds', 'RecoverySeconds')
    'BrokerOutage'     = @('HeartbeatTopic', 'SettleSeconds', 'OutageSeconds', 'RecoverySeconds', 'EchoCommandTopic', 'EchoTopic', 'CommandTimeoutSeconds')
}
$knownKinds = (@($requiredCatalogKeys.Keys) | Sort-Object) -join ', '

foreach ($catalogTestName in $Tests) {
    $catalogEntry = $testCatalog[$catalogTestName]

    if (-not $catalogEntry.Contains('Kind')) {
        Write-Error ("Catalog entry '{0}' declares no Kind. Known kinds: {1}." -f $catalogTestName, $knownKinds)
        exit 1
    }

    if (-not $requiredCatalogKeys.Contains($catalogEntry.Kind)) {
        Write-Error ("Catalog entry '{0}' has unknown Kind '{1}'. Known kinds: {2}." -f $catalogTestName, $catalogEntry.Kind, $knownKinds)
        exit 1
    }

    $missingKeys = @($requiredCatalogKeys[$catalogEntry.Kind] | Where-Object { -not $catalogEntry.Contains($_) })
    if ($missingKeys.Count -gt 0) {
        Write-Error ("Catalog entry '{0}' (Kind '{1}') is missing required setting(s): {2}." -f $catalogTestName, $catalogEntry.Kind, ($missingKeys -join ', '))
        exit 1
    }
}

if (-not $LogDirectory) {
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $LogDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "smarthome-integration-$stamp"
}
New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null

$deployScript   = Join-Path $PSScriptRoot 'Deploy-ToDevice.ps1'
$watchScript    = Join-Path $PSScriptRoot 'Watch-DeviceDebugOutput.ps1'
$startEnvScript = Join-Path $PSScriptRoot 'Start-DevEnv.ps1'
$stopEnvScript  = Join-Path $PSScriptRoot 'Stop-DevEnv.ps1'

function Get-TestProjectPath {
    param([string]$TestName)

    return "src\integrationTests\$TestName\$TestName.nfproj"
}

# ── Evidence ──────────────────────────────────────────────────────────────────
# What a failed run leaves behind to be read afterwards.
#
# Both broker-side logs are transient by design: Start-DevEnv.ps1 truncates them on
# every start and Stop-DevEnv.ps1 deletes them on every stop, and the host-decided
# checks cycle the broker mid-test. So by the time a verdict is being investigated the
# wire evidence behind it is already gone -- which is why the HomieClientCheck run that
# lost all five /set commands (issue #35) could not be chased any further than the
# summary line. Each generation is copied into $LogDirectory before it is taken away.

# Which test the next preserved generation belongs to, and how many have been kept.
# Declared here rather than left to the first assignment because Set-StrictMode
# -Version Latest makes reading an unset variable a terminating error, and the first
# read happens in the pre-flight teardown, before any test has run.
$script:evidenceLabel = 'suite'
$script:evidenceGeneration = 0

function Save-BrokerEvidence {
    param([Parameter(Mandatory = $true)][string]$Port)

    $script:evidenceGeneration++

    # Numbered, not timestamped: the number is also the count of broker generations a
    # run went through, which is the thing a reader wants to line up against the phase
    # breakdown.
    foreach ($log in @(
        @{ Kind = 'BrokerLog';     Name = 'broker' }
        @{ Kind = 'SubscriberLog'; Name = 'homie' }
    )) {
        $source = Get-SmartHomeDevEnvPath -Port $Port -Kind $log.Kind
        if (-not (Test-Path -LiteralPath $source)) {
            continue
        }

        $destination = Join-Path $LogDirectory ("{0}-{1:d2}-{2}.log" -f $script:evidenceLabel, $script:evidenceGeneration, $log.Name)
        try {
            # Both files are still open for writing by the processes producing them.
            # Copy-Item reads them anyway -- measured against a live Mosquitto 2.0.22 log
            # and the cmd.exe redirect behind the homie/# subscriber.
            Copy-Item -LiteralPath $source -Destination $destination -ErrorAction Stop
        }
        catch {
            # Preserving evidence must never become the reason a run fails, and this is
            # called from a finally where a throw would replace the suite's own outcome.
            Write-Warning ("Could not preserve {0}: {1}" -f $source, $_.Exception.Message)
        }
    }
}

function Start-DeviceDebugCapture {
    # Managed debug output captured alongside a host-decided check, whose verdict comes
    # from the broker rather than from the device.
    #
    # Only the device's own log can say whether it saw a command at all, and the
    # conformance path never took one -- so a lost /set left the device side of issue #35
    # entirely unobserved.
    #
    # -NoReboot, unlike the DeviceMarker captures further down. Those reboot because a
    # missed boot is indistinguishable there from a device that never reported, and their
    # verdict is read out of the log. Here a few missed lines cost only evidence, while a
    # reboot would restart the announce the check is about to measure.
    #
    # Launched through cmd.exe with ShellExecute for the handle-inheritance reason
    # documented in Start-DevEnv.ps1: -RedirectStandardOutput forces UseShellExecute=false
    # and hands this script's own stdout to a child that outlives the call.
    param(
        [Parameter(Mandatory = $true)][string]$LogPath,

        # A backstop, not a measurement. The capture is stopped when the check returns;
        # this only bounds a monitor left behind by a runner that died.
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    Remove-Item -Path $LogPath -Force -ErrorAction SilentlyContinue

    # The host this script is running under, so a pwsh session doesn't spawn a
    # Windows PowerShell child (or the reverse) with a different view of the module path.
    $powerShellExe = (Get-Process -Id $PID).Path
    if (-not $powerShellExe) {
        $powerShellExe = Join-Path $PSHOME 'powershell.exe'
    }

    # -ExecutionPolicy Bypass because a fresh process does not inherit the policy the
    # caller was started under: a machine set to Restricted runs this suite as
    # `powershell -ExecutionPolicy Bypass -File ...`, and the child would otherwise
    # refuse to load a script the parent is already executing.
    $command = '/c ""{0}" -NoProfile -ExecutionPolicy Bypass -File "{1}" -DurationSeconds {2} -NoBuild -NoReboot > "{3}" 2>&1"' -f `
        $powerShellExe, $watchScript, $TimeoutSeconds, $LogPath
    $process = Start-Process -FilePath 'cmd.exe' -ArgumentList $command -PassThru -WindowStyle Hidden

    # A record, not a bare pid. This launcher can legitimately be dead by the time the
    # check returns -- a monitor that cannot reach the device gives up after 15s and
    # takes its cmd.exe with it -- and minutes then pass before anything stops it, which
    # is long enough for Windows to hand the pid to something else. That is the case
    # New-SmartHomeProcessRecord carries a name and a start time for. -Tree because the
    # real work is in the grandchild.
    return @{
        Record = New-SmartHomeProcessRecord -Label 'device debug monitor' -Process $process -Tree
        Path   = $LogPath
    }
}

function Stop-DeviceDebugCapture {
    # Killed rather than asked to stop: the monitor listens for a fixed duration and has
    # no way to be told the check is over. The COM port is released with the process, so
    # the next test's deploy is not affected.
    param([hashtable]$Capture)

    if ($null -eq $Capture) {
        return
    }

    # Through the recorded-process helper, which stops the tree only when pid, name and
    # start time all still match, and warns instead of killing when the pid has been
    # recycled. It also covers the already-exited case: taskkill on a dead pid writes to
    # stderr, and under this script's $ErrorActionPreference = 'Stop' that is a
    # terminating NativeCommandError -- thrown out of the finally that calls this, and
    # replacing the verdict the check had already reached. Measured, not assumed:
    # Stop-SmartHomeProcessTree on a dead pid throws here.
    Stop-SmartHomeRecordedProcess -Record $Capture.Record | Out-Null

    # A capture that recorded nothing must not change a verdict the broker already
    # decided, so this warns rather than throwing -- but it says so, because an empty log
    # is otherwise indistinguishable from a device that said nothing.
    #
    # It does not name an attach failure as the cause, because that is the one thing it
    # cannot be: the child's stderr is redirected into this same file, so a monitor that
    # found no device leaves its "No nanoFramework device found on ..." in here. Nothing
    # at all on either stream means the child never got that far.
    $captured = Get-Item -LiteralPath $Capture.Path -ErrorAction SilentlyContinue
    if ($null -eq $captured -or $captured.Length -eq 0) {
        Write-Warning ("No managed debug output was captured, and the monitor reported nothing either ({0}) -- so it never ran. Check that dotnet is on PATH. The verdict is unaffected, but there is no device-side evidence behind it." -f $Capture.Path)
    }
}

# ── Broker lifetime ───────────────────────────────────────────────────────────
# The stop/start recipe the host-decided checks need, in one place instead of six.
#
# These throw rather than returning an exit code, because an exit code was never
# available: Start-DevEnv.ps1 and Stop-DevEnv.ps1 set $ErrorActionPreference = 'Stop'
# and report failures with Write-Error, which is terminating -- so their `exit 1` never
# runs and $LASTEXITCODE is never assigned. Every `if ($LASTEXITCODE -ne 0)` around
# these calls was unreachable, and the carefully worded ERROR verdicts behind them could
# not be produced; the exception surfaced instead as a raw PowerShell message blamed on
# the test. Callers now catch and turn the message into the verdict they intended.

function Start-SuiteBroker {
    param([Parameter(Mandatory = $true)][string]$Port)

    try {
        & $startEnvScript -Detached -NoSync | Out-Null
    }
    catch {
        throw "could not start the broker on port ${Port}: $($_.Exception.Message)"
    }
}

function Stop-SuiteBroker {
    param([Parameter(Mandatory = $true)][string]$Port)

    # Before the stop, not after: Stop-DevEnv.ps1 deletes the broker and homie/# logs
    # as part of tearing the environment down, and a restart truncates them again.
    Save-BrokerEvidence -Port $Port

    try {
        & $stopEnvScript | Out-Null
    }
    catch {
        throw "could not stop the broker on port ${Port}: $($_.Exception.Message)"
    }
}

function Restart-SuiteBroker {
    param(
        [Parameter(Mandatory = $true)][string]$Port,

        # Pause between stop and start. mosquitto is terminated rather than asked to
        # exit and nothing waits for the listener to be released, so a caller that has
        # seen the port still held can ask for a gap.
        [int]$SettleSeconds = 0
    )

    Stop-SuiteBroker -Port $Port

    if ($SettleSeconds -gt 0) {
        Start-Sleep -Seconds $SettleSeconds
    }

    Start-SuiteBroker -Port $Port
}

function Test-DeviceConstant {
    # Compile-time constants in a device project can't be read from local.env.ps1,
    # so they drift. Comparing them up front turns "the test failed on a healthy
    # device" into a warning that names the two values.
    #
    # Takes an explicit path rather than deriving one under src\integrationTests. That
    # derivation silently excluded the one app that actually ships: RoomSensor lives in
    # src\devices, so Test-Path returned false and the check returned without a word --
    # leaving the shipped device as the only one with no stale-broker warning.
    param(
        [string]$Label,
        [string]$ProgramPath,
        [string]$Pattern,
        [string]$Expected,
        [string]$What
    )

    if (-not (Test-Path $ProgramPath)) {
        return
    }

    $match = Select-String -Path $ProgramPath -Pattern $Pattern | Select-Object -First 1
    if (-not $match) {
        return
    }

    $actual = $match.Matches[0].Groups[1].Value
    if ($actual -ne $Expected) {
        Write-Warning ("{0}: {1} is '{2}' in Program.cs but '{3}' here. If it fails to connect, one of the two is stale." -f $Label, $What, $actual, $Expected)
    }
}

function Get-IntegrationTestProgramPath {
    param([string]$TestName)

    return (Join-Path $repoRoot "src\integrationTests\$TestName\Program.cs")
}

function Wait-Heartbeat {
    # Polls the detached subscriber's homie/# log for a heartbeat on $Topic.
    # Returns @{ Line; Counter } for the first one seen, or $null if none arrived
    # inside $TimeoutSeconds. The counter is the trailing integer of the payload
    # ("<topic> heartbeat 12"), and it is what separates a device that reconnected
    # from one that died and came back -- see Invoke-BrokerOutageCheck.
    param(
        [string]$Topic,
        [int]$TimeoutSeconds,
        [string]$Port
    )

    $log = Get-SmartHomeDevEnvPath -Port $Port -Kind SubscriberLog
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        $hit = Get-Content -Path $log -ErrorAction SilentlyContinue |
            Where-Object { $_ -like "$Topic*" } |
            Select-Object -First 1
        if ($hit) {
            $counter = $null
            $match = [regex]::Match($hit, '(\d+)\s*$')
            if ($match.Success) {
                $counter = [int]$match.Groups[1].Value
            }

            return @{ Line = $hit; Counter = $counter }
        }

        Start-Sleep -Milliseconds 500
    }

    return $null
}

function Invoke-BrokerOutageCheck {
    # Kills the broker under a running device and asserts the device publishes
    # again once a fresh broker is up. Start-DevEnv.ps1 truncates the subscriber
    # log on every start, so each phase reads a log that can only contain
    # heartbeats published after that phase's broker came up -- no stale hits.
    param(
        [hashtable]$Settings,
        [string]$Port
    )

    $topic = $Settings.HeartbeatTopic

    # Cycle the environment before measuring anything. The just-deployed app is not
    # the only thing that has been publishing on this topic: whatever was flashed
    # before it kept running, and kept publishing, right through the build and flash
    # -- so the subscriber log can hold a high counter from a previous instance.
    # Start-DevEnv.ps1 truncates the log, which makes the baseline provably belong to
    # the instance now on the device.
    Write-Host "Cycling the broker so the baseline can only come from the new deploy..." -ForegroundColor DarkGray
    try {
        Restart-SuiteBroker -Port $Port
    }
    catch {
        return @{ Outcome = 'ERROR'; Detail = "$($_.Exception.Message) (before measuring)" }
    }

    Write-Host ("Waiting up to {0}s for the first heartbeat on {1}..." -f $Settings.SettleSeconds, $topic) -ForegroundColor Cyan
    $latest = Wait-Heartbeat -Topic $topic -TimeoutSeconds $Settings.SettleSeconds -Port $Port
    if (-not $latest) {
        return @{
            Outcome = 'NO-RESULT'
            Detail  = "no heartbeat on $topic within $($Settings.SettleSeconds)s -- the device never reached the broker, so there is nothing to disconnect"
        }
    }
    Write-Host ("  baseline: {0}" -f $latest.Line) -ForegroundColor DarkGray

    foreach ($outage in $Settings.OutageSeconds) {
        $before = $latest.Counter

        Write-Host ("Taking the broker down for {0}s..." -f $outage) -ForegroundColor Cyan
        try {
            Stop-SuiteBroker -Port $Port
        }
        catch {
            return @{ Outcome = 'ERROR'; Detail = "$($_.Exception.Message) (starting the ${outage}s outage)" }
        }
        Start-Sleep -Seconds $outage

        Write-Host "Bringing a fresh broker up..." -ForegroundColor Cyan
        try {
            Start-SuiteBroker -Port $Port
        }
        catch {
            return @{ Outcome = 'ERROR'; Detail = "$($_.Exception.Message) (after the ${outage}s outage)" }
        }

        $latest = Wait-Heartbeat -Topic $topic -TimeoutSeconds $Settings.RecoverySeconds -Port $Port
        if (-not $latest) {
            return @{
                Outcome = 'FAIL'
                Detail  = "no heartbeat within $($Settings.RecoverySeconds)s of the broker returning after a ${outage}s outage -- the device did not reconnect"
            }
        }

        # The counter is what makes this a reconnect test rather than a "does it
        # publish eventually" test. It only ever climbs within one run of the app,
        # so a value at or below the pre-outage one means the app started over --
        # the device recovered by dying and rebooting, not by reconnecting while
        # connected, which is the thing under test.
        if ($null -ne $before -and $null -ne $latest.Counter -and $latest.Counter -le $before) {
            return @{
                Outcome = 'RESTARTED'
                Detail  = "heartbeat counter went $before -> $($latest.Counter) across the ${outage}s outage: the device restarted instead of reconnecting"
            }
        }

        Write-Host ("  recovered after {0}s outage: {1}" -f $outage, $latest.Line) -ForegroundColor Green
    }

    # Heartbeats prove the connection came back. They do NOT prove the subscriptions
    # did: publishing resumes the moment the socket is up, so a reconnect that restored
    # the session and replayed nothing looks exactly the same from here. That is not
    # hypothetical -- a throw out of ResubscribeCachedTopics used to leave precisely
    # that state, connected and deaf, with one log line as evidence.
    #
    # So: send the device something and require it back. Only a replayed subscription
    # can produce the echo.
    $nonce = "echo-{0}" -f $latest.Counter
    Write-Host ("  checking the subscription survived: publishing '{0}'..." -f $nonce) -ForegroundColor Cyan

    # Republished on a schedule rather than sent once. A heartbeat only tells us the
    # device's *publish* path is back: its loop resumes the moment IsConnected goes
    # true, which is before the reconnect thread has finished replaying subscriptions.
    # A single QoS-0 publish into that window reaches a broker with no subscriber for
    # the topic and is dropped forever, so a healthy device would be reported FAIL.
    if (-not (Wait-ForEcho -Topic $Settings.EchoTopic -Payload $nonce -TimeoutSeconds $Settings.CommandTimeoutSeconds -Port $Port -CommandTopic $Settings.EchoCommandTopic)) {
        return @{
            Outcome = 'FAIL'
            Detail  = "heartbeats resumed but '$nonce' was never echoed on $($Settings.EchoTopic) -- the client reconnected without replaying its subscriptions"
        }
    }

    Write-Host "  subscription replayed after the outage." -ForegroundColor Green

    return @{
        Outcome = 'PASS'
        Detail  = "republished and stayed subscribed across outages of {0}s" -f ($Settings.OutageSeconds -join 's, ')
    }
}

function Wait-ForEcho {
    # Polls the detached subscriber's homie/# log for $Topic carrying exactly $Payload,
    # republishing the command each round until it comes back.
    # Log lines are "<topic> <0|1> <payload>", per Get-SmartHomeSubscriberArguments.
    #
    # The republish is the point. The device's subscription is replayed by the reconnect
    # thread *after* the connect succeeds, while its publish loop resumes as soon as the
    # socket is up -- so there is a window in which heartbeats are flowing and the
    # command topic has no subscriber. A QoS-0 publish into that window is dropped by
    # the broker with no trace, and waiting alone would then report a healthy device as
    # FAIL. Re-sending costs nothing and closes the race.
    param(
        [string]$Topic,
        [string]$Payload,
        [int]$TimeoutSeconds,
        [string]$Port,
        [string]$CommandTopic
    )

    $log = Get-SmartHomeDevEnvPath -Port $Port -Kind SubscriberLog
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        Publish-HomieCommand -Port $Port -Topic $CommandTopic -Payload $Payload

        # Give the round trip a moment before reading, so the common case costs one
        # publish and one read rather than spinning.
        Start-Sleep -Milliseconds 500

        $hit = Get-Content -Path $log -ErrorAction SilentlyContinue |
            Where-Object { $_ -like "$Topic *" -and $_ -like "*$Payload" } |
            Select-Object -First 1
        if ($hit) {
            return $true
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

function Get-MosquittoTool {
    param([string]$Name)

    $dir = Get-RequiredEnvValue -Name 'SMARTHOME_MOSQUITTO_DIR'
    $path = Join-Path $dir $Name

    # Same guard and remediation Start-DevEnv.ps1 gives the same two binaries. Without
    # it a wrong SMARTHOME_MOSQUITTO_DIR surfaced as a raw CommandNotFoundException --
    # or, for the retained snapshot, as an empty read reported as a conformance FAIL,
    # i.e. a machine-config problem blamed on the device.
    if (-not (Test-Path $path)) {
        Write-Error ("Not found: {0}`nCheck SMARTHOME_MOSQUITTO_DIR in local.env.ps1." -f $path)
        exit 1
    }

    return $path
}

# How long a snapshot subscriber listens. Three seconds, and it stays three seconds --
# a fixed value rather than a parameter, because there is nothing here for a caller to
# choose. Two attempts at shortening it both broke the conformance check, and the
# measurements are worth keeping so nobody repeats them:
#
#   - Ending the wait after 250ms of quiet output: 7 failures. Retained replay and live
#     traffic arrive with gaps in them, so "quiet" does not mean "finished". (This used
#     to blame stdio buffering. It is not that -- see Stop-HomieCapture, where the
#     buffering was measured and found not to exist. The 7 failures stand; the
#     explanation for them was wrong.)
#   - A flat 1s: 3 failures, 54 topics where a healthy run sees ~64.
#   - mosquitto_sub -W 1, waiting for it to exit rather than killing it: complete, but
#     6.3s -- slower than what it replaced.
#
# Against a *static* store a 1s kill captures everything (verified: 80/80 seeded topics),
# which is what made the short window look safe in isolation. The real snapshot runs while
# the device is mid-announce, so live publishes interleave with the retained replay and
# there is simply more to receive.
#
# Wait-ForRetainedValue now detects a mid-announce snapshot by its retain flag and retries,
# so for that path the window is no longer the only defence. It still is for the callers
# that read a snapshot without a flag guard -- Test-Attribute and the /set verification --
# which is why it stays at three.
#
# With the IPv6 timeout gone from Get-SmartHomeSubscriberArguments these are three seconds
# of *listening*, where they used to be two of connecting and one of listening.
$SnapshotSettleSeconds = 3

# Snapshots taken so far, across the whole run. Declared here rather than left to the
# first ++ because Set-StrictMode -Version Latest makes incrementing an unset variable a
# terminating error, which would arrive as an 'ERROR' verdict blamed on the device.
$script:snapshotsTaken = 0

function Get-HomieRetainedSnapshot {
    # What a controller joining *now* would see: the broker's retained store.
    #
    # It has to be a fresh subscriber. A retained message delivered live carries
    # retain=0 -- MQTT only sets the flag when replaying from the store to a new
    # subscriber -- so the long-running homie/# log cannot answer "is this retained",
    # which is a rule the convention states outright.
    #
    # mosquitto_sub is wrapped in cmd.exe for the redirect and then killed as a tree,
    # for the handle-inheritance reason documented in Start-DevEnv.ps1.
    #
    # Both retained and live deliveries come back, flag intact. What the flag *means* is
    # the caller's decision, and the four callers deliberately differ: Wait-ForRetainedValue
    # requires it when it hands the snapshot on, Test-Attribute reports it as data, the
    # $retained=false check needs unretained messages to be present at all, and the /set
    # verification ignores it because a live echo is equally good evidence the command was
    # applied. Do not filter here.
    param(
        [string]$Port
    )

    $capture = Start-HomieCapture -Port $Port
    return ConvertTo-HomieSnapshot -Lines (Stop-HomieCapture -Capture $capture)
}

function Start-HomieCapture {
    # Opens the fresh-subscriber window that Get-HomieRetainedSnapshot measures through,
    # without closing it. Split out so a caller can publish *inside* the window: the
    # refused-transition step has to observe messages the device emits in response to a
    # command, and a window opened after the command would miss them.
    #
    # Callers that only want the settled result should use Get-HomieRetainedSnapshot,
    # which is this plus Stop-HomieCapture plus ConvertTo-HomieSnapshot.
    param(
        [string]$Port,

        # Block until the subscriber has actually connected, for a caller that publishes
        # inside the window. Start-Process returns as soon as cmd.exe exists, well before
        # mosquitto_sub has a session, so a publish issued immediately after this returns
        # can reach the broker first and the response it triggers is then missed entirely
        # -- which would read as "the command never arrived" and fail the refused step on
        # a healthy device.
        #
        # A subscriber to homie/# gets the whole retained store the moment it subscribes,
        # so the first byte in the output file is the connection being live. Callers that
        # only want the settled result do not need this: their 3s sleep starts before the
        # connection and is a window, not a measurement of anything published inside it.
        [int]$WaitForConnectSeconds = 0
    )

    $out = Get-SmartHomeDevEnvPath -Port $Port -Kind Snapshot

    # Removed, and *proved* removed. The previous capture is torn down with taskkill /F,
    # which returns before Windows has released cmd.exe's handle on this file, so
    # Remove-Item can fail with a sharing violation -- and -ErrorAction SilentlyContinue
    # would swallow that. A surviving file is not merely untidy: the connect wait below
    # takes "the file has bytes" as proof that THIS subscriber is live, and the previous
    # capture's bytes satisfy it instantly, defeating the wait entirely.
    $removeDeadline = (Get-Date).AddSeconds(5)
    while (Test-Path -Path $out) {
        Remove-Item -Path $out -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path -Path $out)) {
            break
        }

        if ((Get-Date) -ge $removeDeadline) {
            throw "Could not clear the snapshot capture file '$out': a previous subscriber still holds it open."
        }

        Start-Sleep -Milliseconds 50
    }

    $sub = Get-MosquittoTool -Name 'mosquitto_sub.exe'

    # Arguments come from Common.ps1, not from a second copy of them here. The parser
    # depends on the exact '%t %r %p' layout, and that layout is chosen and explained in
    # Get-SmartHomeSubscriberArguments -- spelling it out again meant a one-line change in
    # the file that owns it would silently break this reader.
    # Quoting idiom matches Start-DevEnv.ps1's subscriber launch.
    $subscriberArgs = (Get-SmartHomeSubscriberArguments -Port $Port |
        ForEach-Object { if ($_ -match '[\s/#]') { '"{0}"' -f $_ } else { $_ } }) -join ' '
    $command = '/c ""{0}" {1} > "{2}" 2>&1"' -f $sub, $subscriberArgs, $out
    $process = Start-Process -FilePath 'cmd.exe' -ArgumentList $command -PassThru -WindowStyle Hidden

    if ($WaitForConnectSeconds -gt 0) {
        $connectDeadline = (Get-Date).AddSeconds($WaitForConnectSeconds)
        while ((Get-Date) -lt $connectDeadline) {
            $captured = Get-Item -Path $out -ErrorAction SilentlyContinue
            if ($null -ne $captured -and $captured.Length -gt 0) {
                break
            }

            Start-Sleep -Milliseconds 100
        }
    }

    return @{ ProcessId = $process.Id; Path = $out }
}

function Stop-HomieCapture {
    # Closes the window and returns every line it caught, in arrival order.
    param(
        [hashtable]$Capture,

        # Time to leave the window open before closing it. Defaults to the same settle
        # used by a plain snapshot, so a caller that publishes inside the window gets the
        # same amount of time for the response as one that publishes before it.
        [int]$SettleSeconds = $SnapshotSettleSeconds
    )

    # Counted here rather than where the lines are parsed: this is the function that
    # spends the time, and one closed window is one snapshot however many ways its lines
    # are later read. Nearly the whole wall clock of a conformance run is these windows,
    # and the poll loops decide at runtime how many they need -- so the count is the one
    # figure that explains a run's duration, and it cannot be read off the code.
    $script:snapshotsTaken++

    Start-Sleep -Seconds $SettleSeconds
    # taskkill /F loses nothing here, and that is measured rather than assumed.
    #
    # #36 item 2 argued the opposite: stdout is redirected to a file, so the MSVC CRT
    # buffers fully (~4KB) rather than by line, and a tail still sitting in that buffer
    # would die with the process -- making the callers that read the END of a window
    # (the refused-transition step, the out-of-format step) work only by accident of how
    # much retained replay happened to precede it. That was a plausible mechanism and it
    # is not what mosquitto_sub does.
    #
    # Measured on this machine against Mosquitto 2.0.22, no hardware involved:
    #
    #   - With the subscriber still RUNNING, the redirected file grows by exactly one
    #     line per message (50, 100, 150 bytes for three publishes). A full buffer would
    #     hold all three; there is nothing to lose because nothing is retained in it.
    #   - taskkill /F immediately after the publish (0ms, 50ms, 250ms, 3s) keeps the tail
    #     every time, in a file of 377 bytes -- a tenth of the supposed 4KB boundary.
    #   - Repeated across retained-store sizes of 0, 5, 53 and 120 topics: the tail is
    #     present at every size, including an EMPTY store, which is the case the buffer
    #     theory says must fail.
    #
    # So mosquitto_sub flushes stdout per message and the window's tail is as reliable as
    # its bulk. Do not reintroduce -W to "fix" this: it was measured at 15.2s for a 3s
    # window here (and 6.3s for a 1s one, see $SnapshotSettleSeconds), which would cost
    # minutes across a run to solve a problem that does not exist.
    Stop-SmartHomeProcessTree -ProcessId $Capture.ProcessId

    return @(Get-Content -Path $Capture.Path -ErrorAction SilentlyContinue)
}

function ConvertFrom-HomieCaptureLine {
    # The one reader of the '%t %r %p' line layout, for the same reason Start-HomieCapture
    # takes the subscriber arguments from Common.ps1 rather than respelling them: two
    # callers now read these lines -- the per-topic collapse below and the refused step's
    # ordered read -- and a second copy of the regex is a place where a change to the
    # format could be fixed in one reader and silently keep parsing in the other.
    #
    # Returns $null for a line that is not a message: mosquitto_sub's stderr shares the
    # capture file.
    param(
        [string]$Line
    )

    # "<topic> <0|1> <payload>", and the payload may itself contain spaces.
    $match = [regex]::Match($Line, '^(\S+)\s+([01])\s?(.*)$')
    if (-not $match.Success) {
        return $null
    }

    return @{
        Topic    = $match.Groups[1].Value
        Retained = $match.Groups[2].Value -eq '1'
        Payload  = $match.Groups[3].Value
    }
}

function ConvertTo-HomieSnapshot {
    # Collapses captured lines to one entry per topic. Kept apart from the capture so the
    # same lines can be read twice: as a settled per-topic view, and as the ordered
    # sequence that view deliberately throws away.
    param(
        [string[]]$Lines
    )

    # Last message per topic wins -- except that a repeat of the SAME payload only adds
    # to what is known about it.
    #
    # A QoS-1 publish whose PUBACK is late gets retransmitted: M2Mqtt resends an in-flight
    # message with DupFlag set, up to MqttSettings.MaximumAttemptsRetry (3). So a snapshot
    # can hold a live duplicate of a value the broker also replayed from its store,
    # arriving after the replayed copy. Overwriting on payload equality threw away the
    # retain flag that replay had just proved, and Test-Attribute reported "not retained"
    # for three attributes of 53 that plainly were.
    #
    # Measured, not guessed: a 40s capture of one re-announce holds a retransmitted tail
    # of the last property's attributes, and counter values arriving out of order (146
    # after 147) -- which nothing but retransmission explains.
    #
    # A DIFFERENT payload still replaces both fields. That is what keeps the flag
    # meaningful: a value delivered only live must not inherit the retained-ness of the
    # value it replaced, which is exactly the bug Wait-ForRetainedValue's flag check
    # exists to catch.
    $snapshot = @{}
    foreach ($line in $Lines) {
        $parsed = ConvertFrom-HomieCaptureLine -Line $line
        if ($null -eq $parsed) {
            continue
        }

        $topic = $parsed.Topic
        $retained = $parsed.Retained
        $payload = $parsed.Payload

        # -ceq, not -eq: PowerShell's -eq is case-insensitive, and "the SAME payload"
        # has to mean byte-for-byte here. A live 'TRUE' merging with a replayed 'true'
        # would inherit a retain flag it never earned, which is the very inheritance the
        # replace-on-difference branch below exists to prevent.
        if ($snapshot.Contains($topic) -and $snapshot[$topic].Payload -ceq $payload) {
            $snapshot[$topic].Retained = $snapshot[$topic].Retained -or $retained
            continue
        }

        $snapshot[$topic] = @{
            Retained = $retained
            Payload  = $payload
        }
    }

    return $snapshot
}

function Get-HomieLivePayloads {
    # The payloads published to one topic inside a capture window, in arrival order,
    # with the retained replay dropped: the replayed copy is the value from before the
    # window opened and says nothing about what happened inside it.
    #
    # ConvertTo-HomieSnapshot answers "where did this settle". This answers "what went
    # past, in what order", which is the only thing that tells a command that was refused
    # apart from one that was never delivered -- both leave the store exactly as it was.
    param(
        [string[]]$Lines,
        [string]$Topic
    )

    $payloads = @()
    foreach ($line in $Lines) {
        # Through ConvertFrom-HomieCaptureLine rather than a second spelling of the
        # "<topic> <0|1> <payload>" layout, for the reason stated there: the layout is
        # chosen in Get-SmartHomeSubscriberArguments, and a copy of it here is a place
        # where a change to the format could be fixed in one reader and silently keep
        # parsing in the other.
        $parsed = ConvertFrom-HomieCaptureLine -Line $line
        if ($null -ne $parsed -and $parsed.Topic -eq $Topic -and -not $parsed.Retained) {
            $payloads += $parsed.Payload
        }
    }

    # Comma so a zero- or one-element result still arrives as an array.
    return ,$payloads
}

function Publish-HomieCommand {
    param(
        [string]$Port,
        [string]$Topic,
        [string]$Payload
    )

    # Non-retained, as the convention requires of a controller: "A Homie controller
    # publishes to the set command topic with non-retained messages only."
    $pub = Get-MosquittoTool -Name 'mosquitto_pub.exe'
    # Host from Common.ps1, which owns the reasoning. Not a literal here: the broker
    # address is shared vocabulary, and a second spelling is one a future change to the
    # listener binding would miss.
    & $pub -h $SmartHomeLocalBrokerHost -p $Port -t $Topic -m $Payload | Out-Null
}

function Wait-ForRetainedValue {
    # Polls fresh snapshots until $Topic is *retained* with $Expected, or the timeout
    # expires.
    #
    # By default the retain flag is part of the condition, for the retain=0 reason in
    # Get-HomieRetainedSnapshot: a subscriber that connects mid-announce receives the rest
    # of that announce live, and accepting such a snapshot hands the caller one where most
    # topics look unretained.
    #
    # This went unnoticed for as long as it did because 'localhost' cost a two-second IPv6
    # connect timeout, which reliably delayed the subscriber past the end of the announce.
    # The check was passing on an accident of timing; removing that delay produced 31
    # spurious "not retained" failures, which is how it came to light.
    #
    # It is also what makes returning the snapshot sound. The device publishes its full
    # device info while in Init and only then transitions out, so $state arrives last: a
    # subscriber that saw $state *replayed* necessarily connected after everything before
    # it was already in the store. $state arriving retained is, in effect, the "device has
    # finished announcing" signal, and this turned it from an assumed invariant into an
    # enforced one.
    param(
        [string]$Port,
        [string]$Topic,
        [string]$Expected,
        [int]$TimeoutSeconds,

        # Turn off when only Ok/Seen are read and the snapshot is discarded. Waiting for a
        # *replayed* value costs a whole extra 3s snapshot whenever the device happens to
        # publish it just after the subscriber connected -- a coin flip for a value the
        # device writes within milliseconds of a command. Nothing is lost by accepting the
        # live delivery there: the payload is the same, and $state's retained-ness is
        # already asserted once against the announce snapshot.
        [bool]$RequireRetained = $true,

        # A controller command to (re)publish at the top of every poll round, for the
        # reason set out at the /set round-trip loop: a /set is non-retained, so one that
        # arrives while the device is not subscribed is dropped and no amount of further
        # polling can recover it. Callers waiting on something the device produces by
        # itself -- the announce, the re-announce -- pass neither of these and nothing is
        # published.
        [string]$RepublishTopic,
        [string]$RepublishPayload
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $seen = '<nothing>'
    $snapshot = @{}

    while ((Get-Date) -lt $deadline) {
        if ($RepublishTopic) {
            Publish-HomieCommand -Port $Port -Topic $RepublishTopic -Payload $RepublishPayload
        }

        $snapshot = Get-HomieRetainedSnapshot -Port $Port
        if ($snapshot.Contains($Topic)) {
            $seen = $snapshot[$Topic].Payload
            if ($seen -eq $Expected -and ((-not $RequireRetained) -or $snapshot[$Topic].Retained)) {
                return @{ Ok = $true; Seen = $seen; Snapshot = $snapshot }
            }
        }
    }

    return @{ Ok = $false; Seen = $seen; Snapshot = $snapshot }
}

# ── Phase timing ──────────────────────────────────────────────────────────────
# The summary reports one number per test, and that number covers the deploy as well as
# the measurement -- so when this check went from 59s to 73s between two runs there was
# nothing to attribute the difference to, only arithmetic about how many snapshots a new
# step "ought" to cost. That arithmetic was wrong by 14s and could not be checked.
#
# What actually varies is how many times a poll loop has to take a 3s snapshot before the
# device catches up, and that is a runtime fact. Recording seconds and snapshots per phase
# turns the next unexplained delta into a line someone can point at.
#
# Start/stop rather than a scriptblock wrapper: the phases share $snapshot and the
# failure list, and & { } would run them in a child scope where those assignments are
# invisible to the phases that follow.
$script:conformancePhases = @()
$script:currentPhase = $null

function Start-ConformancePhase {
    param([Parameter(Mandatory = $true)][string]$Name)

    Stop-ConformancePhase
    $script:currentPhase = @{
        Name             = $Name
        Started          = Get-Date
        SnapshotsAtStart = $script:snapshotsTaken
    }
}

function Stop-ConformancePhase {
    if ($null -eq $script:currentPhase) {
        return
    }

    # Rendered here, invariant, rather than left as a double for the -f below to format.
    # These lines get pasted into pull requests and issues, and -f formats in the host's
    # culture -- on this machine that produced '16,7s', which reads as a thousands
    # separator to everyone who wasn't sitting at the machine.
    $elapsed = ((Get-Date) - $script:currentPhase.Started).TotalSeconds

    $script:conformancePhases += [pscustomobject]@{
        Name      = $script:currentPhase.Name
        Seconds   = $elapsed.ToString('0.0', [cultureinfo]::InvariantCulture)
        Snapshots = $script:snapshotsTaken - $script:currentPhase.SnapshotsAtStart
    }
    $script:currentPhase = $null
}

function Write-ConformancePhaseBreakdown {
    # Called from the test loop, not from the check itself: the check returns from four
    # places (two of them failure paths), and those are exactly the runs whose timing is
    # worth seeing. One call after it returns covers all four.
    Stop-ConformancePhase

    if ($script:conformancePhases.Count -eq 0) {
        return
    }

    Write-Host ("  phase breakdown ({0}s per snapshot):" -f $SnapshotSettleSeconds) -ForegroundColor DarkGray
    foreach ($phase in $script:conformancePhases) {
        Write-Host ("    {0,-22} {1,6}s  {2,2} snapshot(s)" -f $phase.Name, $phase.Seconds, $phase.Snapshots) -ForegroundColor DarkGray
    }
}

function Get-ConformanceCaptureSeconds {
    # A ceiling for the debug capture that runs alongside the check, derived from the
    # check's own timeouts rather than guessed: the announce wait, the /set round trip,
    # the out-of-format round, one per lifecycle step, and the re-announce wait, plus
    # slack for the snapshot windows and the broker restart between them.
    #
    # Deliberately loose. A capture that ends early takes the device-side evidence with
    # it exactly when the run was slow enough to be worth reading, and nothing waits out
    # this window -- Stop-DeviceDebugCapture closes it as soon as the check returns.
    param([hashtable]$Settings)

    $lifecycleSteps = 5

    # +2 rather than +1: the /set round trip and the out-of-format phase each poll for up
    # to CommandTimeoutSeconds on top of the lifecycle steps.
    return $Settings.SettleSeconds +
           $Settings.RecoverySeconds +
           (($lifecycleSteps + 2) * $Settings.CommandTimeoutSeconds) +
           60
}

function Add-ConformanceWarning {
    # Both at once, on purpose: to the console while the run is happening, and to the
    # list the verdict's detail is built from so it survives into the summary and the
    # log. Either alone is a way to lose it.
    param([Parameter(Mandatory = $true)][string]$Message)

    $script:conformanceWarnings += $Message
    Write-Warning $Message
}

function Invoke-HomieConformanceCheck {
    # Measures a purpose-built device against the Homie v4 convention, from the
    # broker's side. Every assertion is collected rather than thrown, so one run
    # reports everything that is wrong instead of only the first thing.
    param(
        [hashtable]$Settings,
        [string]$Port
    )

    $deviceId = $Settings.DeviceId
    $nodeId = $Settings.NodeId
    $root = "homie/$deviceId"
    $node = "$root/$nodeId"

    # One failure list, deliberately. There used to be a local $failures here as well,
    # never appended to and never read -- so a reader had two candidate accumulators to
    # reconcile, and an assertion added against the wrong one would have been collected
    # into a list the verdict never reads: a silently passing conformance test.
    $script:conformanceFailures = @()
    # Things a run should report without failing on: observed, attributable to something
    # other than the device, and therefore not a conformance verdict. Kept next to the
    # failures rather than left to Write-Warning alone, because a warning only exists in
    # the console -- it reaches neither the summary nor the run's log directory, which is
    # the "left no evidence" gap #35 is about.
    $script:conformanceWarnings = @()
    $script:conformancePhases = @()
    $script:currentPhase = $null

    Start-ConformancePhase -Name 'announce'
    Write-Host ("Waiting up to {0}s for {1} to announce..." -f $Settings.SettleSeconds, $deviceId) -ForegroundColor Cyan
    $ready = Wait-ForRetainedValue -Port $Port -Topic "$root/`$state" -Expected 'ready' -TimeoutSeconds $Settings.SettleSeconds
    if (-not $ready.Ok) {
        return @{
            Outcome = 'NO-RESULT'
            Detail  = "device never reached `$state=ready within $($Settings.SettleSeconds)s (saw '$($ready.Seen)')"
        }
    }

    # The snapshot the wait already paid for, not a fresh 3s one.
    $snapshot = $ready.Snapshot
    Write-Host ("  retained store holds {0} topics" -f $snapshot.Count) -ForegroundColor DarkGray

    function Test-Attribute {
        param([string]$Topic, [string]$Expected, [switch]$AnyValue)

        if (-not $snapshot.Contains($Topic)) {
            $script:conformanceFailures += "missing: $Topic"
            return
        }

        if (-not $snapshot[$Topic].Retained) {
            $script:conformanceFailures += "not retained: $Topic"
        }

        if (-not $AnyValue -and $snapshot[$Topic].Payload -ne $Expected) {
            $script:conformanceFailures += "$Topic is '$($snapshot[$Topic].Payload)', expected '$Expected'"
        }
    }

    Start-ConformancePhase -Name 'attributes'

    # ── mandatory device attributes ──────────────────────────────────────────
    Test-Attribute -Topic "$root/`$homie" -Expected '4'
    Test-Attribute -Topic "$root/`$name" -AnyValue
    Test-Attribute -Topic "$root/`$state" -Expected 'ready'
    Test-Attribute -Topic "$root/`$nodes" -Expected $nodeId
    # $extensions is asserted from the LIVE log, not the retained store. The spec says
    # it "MUST be sent, even if it is just an empty string", but MQTT defines a
    # zero-length retained payload as a delete of the retained message -- so an empty
    # $extensions is published and then provably absent from the store. That is the
    # convention and MQTT disagreeing, not the device misbehaving.
    $liveLog = @(Get-Content -Path (Get-SmartHomeDevEnvPath -Port $Port -Kind SubscriberLog) -ErrorAction SilentlyContinue)
    if (-not ($liveLog | Where-Object { $_ -like "$root/`$extensions*" })) {
        $script:conformanceFailures += "never published: $root/`$extensions"
    }

    # ── node attributes ──────────────────────────────────────────────────────
    Test-Attribute -Topic "$node/`$name" -AnyValue
    Test-Attribute -Topic "$node/`$type" -AnyValue
    Test-Attribute -Topic "$node/`$properties" -AnyValue

    # ── one property per datatype, with its declared metadata ────────────────
    $expectedTypes = @{
        'integer-value' = 'integer'
        'float-value'   = 'float'
        'boolean-value' = 'boolean'
        'string-value'  = 'string'
        'enum-value'    = 'enum'
        'color-value'   = 'color'
        'counter'       = 'integer'
        'lifecycle'     = 'enum'
    }

    foreach ($property in $expectedTypes.Keys) {
        Test-Attribute -Topic "$node/$property/`$name" -AnyValue
        Test-Attribute -Topic "$node/$property/`$datatype" -Expected $expectedTypes[$property]
    }

    Test-Attribute -Topic "$node/integer-value/`$settable" -Expected 'true'
    Test-Attribute -Topic "$node/integer-value/`$format" -Expected '0:100'
    Test-Attribute -Topic "$node/enum-value/`$format" -Expected 'low,medium,high'
    Test-Attribute -Topic "$node/color-value/`$format" -Expected 'rgb'
    # Built by HomieClientCheck from the State enum rather than spelled out there, so
    # that the vocabulary a controller is offered cannot drift from the vocabulary
    # $state is published in. This assertion is what notices if that derivation breaks.
    Test-Attribute -Topic "$node/lifecycle/`$format" -Expected 'ready,alert,sleeping'
    Test-Attribute -Topic "$node/lifecycle/`$settable" -Expected 'true'
    Test-Attribute -Topic "$node/integer-value/`$unit" -Expected '#'
    Test-Attribute -Topic "$node/float-value/`$unit" -AnyValue

    # ── $retained=false is honoured, not just declared ───────────────────────
    Test-Attribute -Topic "$node/counter/`$retained" -Expected 'false'
    Test-Attribute -Topic "$node/counter/`$settable" -Expected 'false'
    # The counter publishes every 2s, so a LIVE message can easily land inside the
    # snapshot window. Absence would be the wrong assertion; what matters is that the
    # broker never marks it retained.
    if ($snapshot.Contains("$node/counter") -and $snapshot["$node/counter"].Retained) {
        $script:conformanceFailures += "counter declares `$retained=false but the broker replayed it as retained"
    }

    # ── a controller command is applied and reflected back ───────────────────
    Start-ConformancePhase -Name '/set round-trip'
    Write-Host "  driving /set commands..." -ForegroundColor DarkGray
    $commands = @{
        'integer-value' = '42'
        'float-value'   = '21.5'
        'boolean-value' = 'true'
        'string-value'  = 'commanded'
        'enum-value'    = 'high'
    }

    # What the device is expected to publish back, where that differs from what was sent.
    # A float property renders at its declared precision, so 21.5 comes back as '21.50'
    # from a two-decimal property -- that is the point, not an artefact.
    #
    # Compared as an exact string, deliberately. This used to compare floats numerically
    # with a 0.001 tolerance, to tolerate nanoFramework's double.ToString() rendering 21.5
    # as '21.499999999999999'. That tolerance was hiding the bug rather than measuring it:
    # the payload a controller reads is a string, and '21.499999999999999' is a defect
    # whatever it parses to. Exact comparison is what makes this test guard the fix.
    $expectedEcho = @{
        'float-value' = '21.50'
    }

    # One snapshot per round, not one per property. Every reflection is checked
    # against the same snapshot, so five properties cost one snapshot instead of
    # five -- the snapshot has to be a fresh subscriber (retain flags are only set on
    # replay), which is what makes it expensive.
    $pending = @($commands.Keys)
    $lastSeen = @{}
    $deadline = (Get-Date).AddSeconds($Settings.CommandTimeoutSeconds)

    # The commands are published at the top of every round, not once before the loop.
    #
    # A /set is non-retained, as the convention requires of a controller, so one that
    # reaches the broker while the device is not yet subscribed is dropped outright --
    # there is nothing left in the store for the device to pick up when it does
    # subscribe. Published once, that single lost message becomes the full
    # CommandTimeoutSeconds of polling for an echo that can never arrive, and five
    # conformance failures against a device that is working correctly.
    #
    # Measured, not hypothetical: 1 run in 8 on 2026-08-26, and it was the only run of
    # the eight whose announce wait was satisfied by its first snapshot rather than its
    # third -- i.e. the only one that reached this loop early. The /set phase then spent
    # 33.4s over 10 snapshots and reported all five properties still holding their boot
    # values.
    #
    # Retrying is the only fix available at this layer. MQTT gives a publisher no signal
    # about who is subscribed, and $state=ready is the device's claim about itself, not
    # about the broker's routing table -- so the host cannot wait for the condition it
    # actually needs. On a healthy run this costs nothing: the first round succeeds and
    # nothing is ever re-sent. Only still-pending properties are republished, and a
    # repeated command is idempotent -- the device applies the same value again and
    # publishes back the same reflection.
    while ($pending.Count -gt 0 -and (Get-Date) -lt $deadline) {
        foreach ($property in $pending) {
            Publish-HomieCommand -Port $Port -Topic "$node/$property/set" -Payload $commands[$property]
        }

        $snapshot = Get-HomieRetainedSnapshot -Port $Port
        $stillPending = @()

        foreach ($property in $pending) {
            $expected = if ($expectedEcho.Contains($property)) { $expectedEcho[$property] } else { $commands[$property] }
            $topic = "$node/$property"
            $seen = if ($snapshot.Contains($topic)) { $snapshot[$topic].Payload } else { $null }
            $lastSeen[$property] = $seen

            # Every datatype, floats included, must match exactly.
            #
            # The retain flag is deliberately not part of this. Unlike the waits above,
            # what is being proven here is that the device applied the command -- a live
            # echo is equally good evidence of that, and requiring a replayed copy would
            # cost an extra snapshot round for nothing.
            if ($seen -eq $expected) {
                continue
            }

            $stillPending += $property
        }

        $pending = $stillPending
    }

    foreach ($property in $pending) {
        $wanted = if ($expectedEcho.Contains($property)) { $expectedEcho[$property] } else { $commands[$property] }
        $script:conformanceFailures += "/set on $property did not come back on the property topic (saw '$($lastSeen[$property])', expected '$wanted')"
    }

    # ── payloads the properties' own $datatype/$format forbid ────────────────
    Start-ConformancePhase -Name 'out-of-format /set'
    Write-Host "  driving /set commands the datatypes forbid..." -ForegroundColor DarkGray
    # The library refuses these before anything is applied: the value does not move and
    # nothing is published, so nothing lands in the retained store and the controller's
    # only feedback is a property that did not change (issue #39).
    #
    # Which is also precisely what a command that never arrived looks like. A /set is
    # non-retained, so a lost one leaves no trace whatsoever -- the same reasoning the
    # refused-transition step below sets out at length.
    #
    # So each forbidden payload is followed by a valid one on the same property, inside
    # one capture window. The valid payload's reflection is the proof that the device was
    # subscribed and receiving while the forbidden one went past: seeing it, and NOT
    # seeing the forbidden payload before it, is a refusal. Seeing neither is a lost
    # command, and is reported as exactly that rather than passing.
    #
    # One case per datatype, each the behaviour that type used to have: an integer
    # outside a declared range and a float that does not parse were applied or dropped
    # silently, an enum value $format does not list was accepted, an unparseable colour
    # was dropped, and a boolean that was neither 'true' nor 'false' was turned into
    # 'false' -- fabricated, not refused.
    #
    # That last one is why the boolean's valid payload is 'true' rather than the more
    # obvious 'false'. A fabricating boolean never publishes 'on' back, it publishes
    # 'false', so a case whose valid payload is also 'false' cannot tell the fabrication
    # from the refusal -- both leave a single 'false' on the topic and the "was the
    # forbidden payload applied" check below looks for 'on', which never appears. With
    # 'true' as the valid payload the fabricated 'false' lands *before* the reflection,
    # which is what the ordering check below measures.
    #
    # string-value has no case here: every payload is a valid Homie string and $format
    # carries no meaning for that datatype, so there is nothing for a payload to violate.
    $outOfFormatCases = @(
        @{ Property = 'integer-value'; Bad = '101';    Good = '43';        Echo = '43'        }
        @{ Property = 'float-value';   Bad = 'warm';   Good = '22.5';      Echo = '22.50'     }
        @{ Property = 'boolean-value'; Bad = 'on';     Good = 'true';      Echo = 'true'      }
        @{ Property = 'enum-value';    Bad = 'purple'; Good = 'medium';    Echo = 'medium'    }
        @{ Property = 'color-value';   Bad = 'FF8000'; Good = '255,128,0'; Echo = '255,128,0' }
    )

    $pending = @($outOfFormatCases)
    $seenPayloads = @{}
    $deadline = (Get-Date).AddSeconds($Settings.CommandTimeoutSeconds)

    while ($pending.Count -gt 0 -and (Get-Date) -lt $deadline) {
        $capture = Start-HomieCapture -Port $Port -WaitForConnectSeconds 5

        foreach ($case in $pending) {
            Publish-HomieCommand -Port $Port -Topic "$node/$($case.Property)/set" -Payload $case.Bad
            Publish-HomieCommand -Port $Port -Topic "$node/$($case.Property)/set" -Payload $case.Good
        }

        $lines = Stop-HomieCapture -Capture $capture
        $stillPending = @()

        foreach ($case in $pending) {
            $payloads = Get-HomieLivePayloads -Lines $lines -Topic "$node/$($case.Property)"
            $seenPayloads[$case.Property] = $payloads

            # Retried only while NEITHER payload was seen, i.e. while nothing has been
            # measured. A forbidden payload that did land is a real failure and must not
            # be retried away.
            if ([array]::IndexOf($payloads, $case.Echo) -lt 0 -and [array]::IndexOf($payloads, $case.Bad) -lt 0) {
                $stillPending += $case
            }
        }

        $pending = $stillPending
    }

    foreach ($case in $outOfFormatCases) {
        $payloads = if ($seenPayloads.Contains($case.Property)) { $seenPayloads[$case.Property] } else { @() }

        if ([array]::IndexOf($payloads, $case.Bad) -ge 0) {
            $script:conformanceFailures += "out-of-format '$($case.Bad)' was applied to $($case.Property) and published back (saw: $($payloads -join ', '))"
            continue
        }

        $echoIndex = [array]::IndexOf($payloads, $case.Echo)

        if ($echoIndex -lt 0) {
            $script:conformanceFailures += "neither the out-of-format '$($case.Bad)' nor the valid '$($case.Good)' reached $($case.Property), so the refusal was never measured (saw: $($payloads -join ', '))"
            continue
        }

        # A forbidden payload does not have to come back verbatim to have been applied.
        # BooleanProperty's old behaviour turned anything unrecognised into 'false' and
        # published *that*, which the verbatim check above cannot see at all -- so the
        # measurement is the stronger one: a refusal publishes nothing, therefore the
        # valid payload's reflection must be the FIRST thing on the property topic inside
        # the window. Anything ahead of it is the forbidden payload having moved the
        # value, whatever it was rendered as.
        if ($echoIndex -gt 0) {
            $before = $payloads[0..($echoIndex - 1)] -join ', '
            $script:conformanceFailures += "$($case.Property) published '$before' before the valid '$($case.Good)', so the out-of-format '$($case.Bad)' was applied rather than refused (saw: $($payloads -join ', '))"
        }
    }

    # ── the lifecycle states a device can be driven into ─────────────────────
    Start-ConformancePhase -Name 'lifecycle'
    Write-Host "  driving `$state through alert, sleeping and a refused transition..." -ForegroundColor DarkGray
    # ready -> alert -> ready -> sleeping -> ready, with the one transition the
    # convention's own state machine forbids asked for in the middle. alert may only
    # return to ready (or disconnect), so alert -> sleeping must be refused -- and a
    # device that advertises it as done anyway is the defect the Refused step measures.
    $lifecycleSteps = @(
        @{ Command = 'alert';    Expect = 'alert';    Refused = $false }
        @{ Command = 'sleeping'; Expect = 'alert';    Refused = $true  }
        @{ Command = 'ready';    Expect = 'ready';    Refused = $false }
        @{ Command = 'sleeping'; Expect = 'sleeping'; Refused = $false }
        @{ Command = 'ready';    Expect = 'ready';    Refused = $false }
    )

    foreach ($step in $lifecycleSteps) {
        if ($step.Refused) {
            # Nothing to wait for here -- the assertion is that nothing changed -- so one
            # capture window IS the measurement rather than a poll for it, and it carries
            # both topics as well as the sequence on the property.
            #
            # HomieClient reflects a /set payload onto its property before the app ever
            # sees it: right for an ordinary property, wrong for one whose value is a
            # request that can be turned down. Uncorrected, the retained store ends up
            # advertising matrix/lifecycle=sleeping beside $state=alert, and every
            # controller that connects afterwards reads that contradiction out of the
            # store rather than seeing it go by.
            #
            # The window is opened BEFORE the command and closed after, rather than
            # taken afterwards, because this step needs the messages the device emits in
            # response -- not just where things settled.
            #
            # Settling on 'alert' is not by itself evidence of a refusal. A /set is
            # non-retained, so a command dropped because the device was not subscribed
            # leaves $state and the property exactly as a refusal does, and this step
            # cannot retry its way past that the way the polls around it now do: its
            # whole assertion is that nothing changes, so there is no echo to wait on.
            # Read only from a settled snapshot, a lost command passes as a refusal.
            #
            # What separates them is on the wire. HomieClient reflects the /set payload
            # onto the property before the app sees it, and HomieClientCheck then
            # publishes the device's real state over that reflection -- so a command that
            # arrived and was refused puts 'sleeping' and then 'alert' on the property
            # topic, in that order. A command that never arrived puts neither.
            #
            # That sequence is also the behaviour this test exists to guard: it is the
            # correction itself, observed rather than inferred from where the store ended
            # up.
            # try/finally, so the window is closed even if the publish throws. The
            # capture file is one fixed path per port, shared by every snapshot in the
            # run: an orphaned mosquitto_sub keeps appending homie/# traffic to it, and
            # every later snapshot would then read a file that is no longer the record of
            # one fresh subscriber -- silently wrong retain flags for the rest of the suite.
            $capture = Start-HomieCapture -Port $Port -WaitForConnectSeconds 5
            try {
                Publish-HomieCommand -Port $Port -Topic "$node/lifecycle/set" -Payload $step.Command
            }
            finally {
                $lines = Stop-HomieCapture -Capture $capture
            }

            $refused = ConvertTo-HomieSnapshot -Lines $lines

            # What this step asserts is THE DEVICE'S BEHAVIOUR, read from the wire. It is
            # deliberately not an assertion about the retained store's final contents, and
            # #36 item 1 is the decision to make that explicit rather than leave the two
            # claims tangled together.
            #
            # They came apart because a settled per-topic read can be flipped by a QoS-1
            # retransmission. ConvertTo-HomieSnapshot only merges on an EQUAL payload, so a
            # DUP of the reflected 'sleeping' arriving after the correction replaces it:
            # wire order [sleeping, alert, sleeping-dup] settles on 'sleeping' while every
            # ordered assertion passes. And the store really does end up holding it --
            # MQTT 3.1.1 3.3.1.1 says a receiver "cannot assume that it has seen an earlier
            # copy" of a DUP packet, and QoS 1 has no dedup, so the broker re-processes it.
            #
            # That is a true statement about the store and a false one about the device: it
            # reflected and corrected exactly as required, and the retransmission is the
            # transport's, not its. Worse, the runner cannot tell that sequence apart from a
            # device that genuinely republished the refused value after correcting it -- the
            # subscriber sees a broker-forwarded DUP as an ordinary message. Asserting on it
            # therefore names a defect this window cannot distinguish, which is the same
            # call 2af2b12 already made for the "corrected too early" branch.
            #
            # So the settled reads below are used for two things only: presence (was
            # anything measured at all) and evidence in the messages. The verdicts come from
            # the ordered reads.
            $stateAfter = if ($refused.Contains("$root/`$state")) { $refused["$root/`$state"].Payload } else { $null }
            $lifecycleAfter = if ($refused.Contains("$node/lifecycle")) { $refused["$node/lifecycle"].Payload } else { $null }

            # Absent, not merely wrong: neither topic being in the window means nothing
            # was measured -- a subscriber that never came up, not a device that took the
            # forbidden transition. Reported as such rather than as a device defect, the
            # way the wire assertions below already do for a lost command.
            #
            # Presence is the one thing the collapse answers soundly whatever arrives: a
            # duplicate can change WHICH payload a topic settles on, never whether the
            # topic was seen.
            if ($null -eq $stateAfter -or $null -eq $lifecycleAfter) {
                $script:conformanceFailures += "refused '$($step.Command)': the capture window caught no `$state or $nodeId/lifecycle at all, so nothing about the refusal was measured"
            }
            else {
                # Published values, not the last value. A refusal publishes nothing on
                # $state, so anything the device puts there live inside this window is a
                # move it should not have made -- and reading the published payloads
                # rather than where the topic settled is what makes that immune to the
                # flip above.
                #
                # $step.Expect is the one payload tolerated, and only that one. It is the
                # value $state already holds, and the preceding step republishes its
                # command every poll round while M2Mqtt retransmits an unacknowledged
                # QoS-1 publish for MaximumAttemptsRetry (3) x DelayOnRetry (1000ms), so
                # a fresh copy of it can legitimately still be arriving. Nothing else can:
                # every older $state value was published well outside that ~3s budget --
                # the preceding step polls in 3s snapshots before it returns.
                #
                # Which is why this is NOT narrowed to $step.Command. Looking only for the
                # commanded value would pass a device that answered the refused command by
                # moving $state somewhere else entirely (a CanChangeState regression
                # leaving it at 'init' or 'lost'), and that is a defect this step used to
                # catch before it read the wire.
                $statePayloads = Get-HomieLivePayloads -Lines $lines -Topic "$root/`$state"
                $moved = @($statePayloads | Where-Object { $_ -ne $step.Expect })

                if ([array]::IndexOf($moved, $step.Command) -ge 0) {
                    $script:conformanceFailures += "forbidden $($step.Expect) -> $($step.Command) transition was applied (`$state went to '$($step.Command)'; saw: $($statePayloads -join ', '))"
                }
                elseif ($moved.Count -gt 0) {
                    $script:conformanceFailures += "refused '$($step.Command)' moved `$state off '$($step.Expect)' to '$($moved -join "', '")' -- not the forbidden transition, but not a refusal either (saw: $($statePayloads -join ', '))"
                }
            }

            $lifecyclePayloads = Get-HomieLivePayloads -Lines $lines -Topic "$node/lifecycle"

            # The correction is looked for AFTER the reflection, not anywhere in the
            # window. The window is deliberately opened before the command, so it can
            # also hold payloads that predate it: the preceding step republishes its own
            # command every poll round, and M2Mqtt retransmits an unacknowledged QoS-1
            # publish every MqttSettings.DelayOnRetry (1s), up to three times -- the same
            # retransmission ConvertTo-HomieSnapshot's merge rule exists to absorb. A
            # first-occurrence search for the expected value would find one of those and
            # report a correct device as having published the correction too early.
            $reflected = [array]::IndexOf($lifecyclePayloads, $step.Command)
            $corrected = -1
            if ($reflected -ge 0 -and ($reflected + 1) -lt $lifecyclePayloads.Length) {
                $corrected = [array]::IndexOf($lifecyclePayloads, $step.Expect, $reflected + 1)
            }

            # One message for "no correction after the reflection", not two.
            #
            # Splitting it on whether $Expect appears anywhere in the window would name a
            # second defect -- "corrected, but before the reflection it was supposed to
            # overwrite" -- that the window cannot actually distinguish. It is the same
            # unordered first-occurrence search the paragraph above rejects for $corrected:
            # the preceding step drove the device to $Expect and its publishes (republished
            # per poll round, retransmitted per DelayOnRetry) can still be arriving when
            # this window opens. A device that simply never corrected would then be
            # reported as having corrected too early, sending the next reader after a
            # publish ordering bug that never happened.
            #
            # The observed sequence is in the message either way, so a genuinely early
            # correction is still visible -- as evidence, not as a claim the runner cannot
            # support.
            if ($reflected -lt 0) {
                $script:conformanceFailures += "refused '$($step.Command)' never reached $nodeId/lifecycle -- the command was lost, so nothing about the refusal was measured (saw: $($lifecyclePayloads -join ', '))"
            }
            elseif ($corrected -lt 0) {
                $script:conformanceFailures += "device left the reflected '$($step.Command)' on $nodeId/lifecycle and never published '$($step.Expect)' over it (saw: $($lifecyclePayloads -join ', '))"
            }
            elseif ($lifecycleAfter -eq $step.Command) {
                # The device corrected, and the topic still settled back on the value it
                # corrected AWAY from. That is the duplicate #36 item 1 describes: a DUP of
                # the reflection re-processed by the broker after the correction. The store
                # disagrees with the device, and the device is not what is wrong -- so it is
                # reported and not counted.
                #
                # Not swallowed, because it is a real contradiction in the retained store
                # while it lasts: a controller connecting before the next lifecycle publish
                # reads the refused value beside a $state that never took it. It is
                # transient (the next command overwrites it) and outside this device's
                # control, but a run that hits it should say so rather than look clean --
                # so it also goes into the verdict's detail below, where the summary and
                # the run's log can carry it. A console-only warning is exactly the kind of
                # evidence #35 was unable to go back and read.
                Add-ConformanceWarning ("refused '{0}': {1}/lifecycle settled back on '{2}' beside `$state='{3}' even though the correction to '{4}' is on the wire -- a retransmitted duplicate re-processed by the broker, not a device defect (saw: {5})" -f $step.Command, $nodeId, $lifecycleAfter, $stateAfter, $step.Expect, ($lifecyclePayloads -join ', '))
            }
            elseif ($lifecycleAfter -ne $step.Expect) {
                # Settled on something that is neither the corrected value nor the value it
                # corrected away from. A retransmission can only ever repeat a payload the
                # device already published, and everything it published before this window
                # is outside M2Mqtt's ~3s retry budget (see the $state read above), so this
                # is a genuine new publish onto the property after the correction -- the
                # store left advertising a state the device is not in, which is the defect
                # this step exists for. Counted, not demoted: the duplicate reasoning above
                # does not reach it.
                $script:conformanceFailures += "refused '$($step.Command)' left $nodeId/lifecycle advertising '$lifecycleAfter' after correcting to '$($step.Expect)', while `$state is '$stateAfter' (saw: $($lifecyclePayloads -join ', '))"
            }

            continue
        }

        # No publish here: the wait republishes the command itself at the top of every
        # round, so a command dropped because the device was not subscribed yet is
        # retried instead of turning into a timeout. Same reasoning as the /set
        # round-trip loop above.
        #
        # -RequireRetained $false: this reads Ok/Seen only and discards the snapshot. The
        # device writes $state within milliseconds of the command, so insisting on a
        # replayed copy would spend a second full snapshot re-observing the same value,
        # four times per run.
        $reached = Wait-ForRetainedValue -Port $Port -Topic "$root/`$state" -Expected $step.Expect -TimeoutSeconds $Settings.CommandTimeoutSeconds -RequireRetained $false -RepublishTopic "$node/lifecycle/set" -RepublishPayload $step.Command
        if (-not $reached.Ok) {
            $script:conformanceFailures += "device did not reach `$state='$($step.Expect)' on '$($step.Command)' (saw '$($reached.Seen)')"
        }
    }

    # ── a replaced broker gets the whole announcement again ──────────────────
    Start-ConformancePhase -Name 're-announce'
    Write-Host "  replacing the broker to check the re-announce..." -ForegroundColor DarkGray
    try {
        Restart-SuiteBroker -Port $Port -SettleSeconds 5
    }
    catch {
        return @{ Outcome = 'ERROR'; Detail = $_.Exception.Message }
    }

    $reannounced = Wait-ForRetainedValue -Port $Port -Topic "$root/`$state" -Expected 'ready' -TimeoutSeconds $Settings.RecoverySeconds
    if (-not $reannounced.Ok) {
        $script:conformanceFailures += "no re-announce after the broker was replaced (saw '$($reannounced.Seen)')"
    }
    else {
        $after = $reannounced.Snapshot
        foreach ($topic in "$root/`$homie", "$root/`$nodes", "$node/`$properties", "$node/integer-value/`$datatype") {
            if (-not $after.Contains($topic)) {
                $script:conformanceFailures += "re-announce did not republish $topic"
            }
        }
    }

    # Appended to whichever verdict is returned, so a run that observed one carries it
    # into the summary line and the log instead of only into scrollback. A PASS with a
    # note is still a PASS: nothing here is a conformance failure.
    $warningDetail = if ($script:conformanceWarnings.Count -gt 0) {
        " [$($script:conformanceWarnings.Count) warning(s): " + ($script:conformanceWarnings -join '; ') + "]"
    }
    else {
        ''
    }

    if ($script:conformanceFailures.Count -gt 0) {
        return @{
            Outcome = 'FAIL'
            Detail  = "$($script:conformanceFailures.Count) conformance failure(s): " + ($script:conformanceFailures -join '; ') + $warningDetail
        }
    }

    return @{
        Outcome = 'PASS'
        Detail  = "attributes, datatypes, retained flags, /set round-trip, refused out-of-format payloads, alert/sleeping, a refused transition and re-announce all conform" + $warningDetail
    }
}

# ── Pre-flight ────────────────────────────────────────────────────────────────
$expectedBroker = Get-OptionalEnvValue -Name 'SMARTHOME_MQTT_BROKER' -DefaultValue 'localhost'

# RoomSensor is checked too, and first. It is the app that ships, it carries the same
# hardcoded broker address, and the suite leaves it on the device -- so a stale constant
# there outlives the run that would have warned about it.
Test-DeviceConstant -Label 'RoomSensor' `
                    -ProgramPath (Join-Path $repoRoot 'src\devices\RoomSensor\Program.cs') `
                    -Pattern 'BrokerHost\s*=\s*"([^"]+)"' `
                    -Expected $expectedBroker `
                    -What 'BrokerHost'

foreach ($testName in $Tests) {
    Test-DeviceConstant -Label $testName -ProgramPath (Get-IntegrationTestProgramPath -TestName $testName) -Pattern 'BrokerHost\s*=\s*"([^"]+)"' -Expected $expectedBroker -What 'BrokerHost'

    $settings = $testCatalog[$testName]

    # Every topic the host and the device have to agree on, checked the same way.
    foreach ($constant in 'HeartbeatTopic', 'EchoCommandTopic', 'EchoTopic') {
        if ($settings.Contains($constant)) {
            Test-DeviceConstant -Label $testName `
                                -ProgramPath (Get-IntegrationTestProgramPath -TestName $testName) `
                                -Pattern ('{0}\s*=\s*"([^"]+)"' -f $constant) `
                                -Expected $settings[$constant] `
                                -What $constant
        }
    }

    if (($settings.Kind -eq 'BrokerOutage' -or $settings.Kind -eq 'HomieConformance') -and $NoBroker) {
        Write-Error "$testName needs to own the broker's lifetime, so it cannot run with -NoBroker. Drop the switch, or exclude it with -Tests."
        exit 1
    }
}

# Build the debug monitor once up front. Watch-DeviceDebugOutput.ps1 would otherwise
# rebuild this host-side tool before every capture -- ~1.8s of no-op build per test.
Write-Host "Building the device debug monitor..." -ForegroundColor DarkGray
& $watchScript -BuildOnly
if ($LASTEXITCODE -ne 0) {
    Write-Error "DeviceDebugMonitor failed to build (exit code $LASTEXITCODE)."
    exit $LASTEXITCODE
}

# ── Broker ────────────────────────────────────────────────────────────────────
$brokerStarted = $false
if (-not $NoBroker) {
    # Only tear down when something is actually recorded: Start-DevEnv.ps1 clears a
    # stale state file itself, and an unconditional stop would enumerate every
    # process on the machine (~280ms) just to find nothing on the common path.
    if (Get-SmartHomeDevEnvState -Port $mqttPort) {
        Stop-SuiteBroker -Port $mqttPort
    }

    Write-Host "Starting the local MQTT broker (detached)..." -ForegroundColor Cyan
    # Pre-flight, outside the per-test try: a broker that will not start is not a test
    # failure, so let the message from Start-SuiteBroker end the run.
    Start-SuiteBroker -Port $mqttPort
    $brokerStarted = $true
    Write-Host ""
}

$results = @()

try {
    foreach ($testName in $Tests) {
        $settings = $testCatalog[$testName]
        $logFile = Join-Path $LogDirectory "$testName.log"
        $script:evidenceLabel = $testName

        Write-Host ('=' * 69)
        Write-Host ("Integration test: {0}" -f $testName) -ForegroundColor Cyan
        Write-Host ('=' * 69)

        $testStarted = Get-Date
        # Always defined, so the result object below can read it on every path -- including
        # the one where the deploy itself is what threw.
        $deploySeconds = 0
        $outcome = $null
        $detail = $null

        # One failing test must not abort the rest of the suite: the sub-scripts
        # use Write-Error under $ErrorActionPreference = 'Stop', so a deploy or
        # capture failure arrives here as a terminating error, not an exit code.
        try {
            # ── Deploy ────────────────────────────────────────────────────────
            # No exit-code check: per the comment above, a failure arrives here as a
            # terminating error. One used to sit here and could never run.
            & $deployScript -Project (Get-TestProjectPath -TestName $testName) -Configuration $Configuration

            # Timed apart from the measurement that follows. Deploy-ToDevice.ps1 always
            # /t:Rebuild's, so this is a full build plus a flash and swings by tens of
            # seconds with nothing to do with the test -- folded into one number, it is
            # indistinguishable from the test getting slower.
            $deploySeconds = [int]((Get-Date) - $testStarted).TotalSeconds

            Write-Host ""

            # ── Host-decided ──────────────────────────────────────────────────
            # Dispatch first, then handle the verdict once. The two kinds differ only in
            # which function produces it; everything after was duplicated line for line,
            # and had already drifted -- only one copy carried the comment explaining why
            # the broker is restarted.
            $verdict = $null
            if ($settings.Kind -eq 'HomieConformance') {
                $debugCapture = Start-DeviceDebugCapture -LogPath $logFile `
                                                         -TimeoutSeconds (Get-ConformanceCaptureSeconds -Settings $settings)
                try {
                    $verdict = Invoke-HomieConformanceCheck -Settings $settings -Port $mqttPort
                }
                finally {
                    Stop-DeviceDebugCapture -Capture $debugCapture

                    # In the finally with it, because returning is not the check's only
                    # way out: a missing mosquitto tool, a capture file that cannot be
                    # cleared or a publish that fails all propagate under
                    # $ErrorActionPreference = 'Stop' and land in the catch below as an
                    # ERROR verdict. That is the run whose phase timings are worth
                    # reading most, and outside the finally it was the one run that
                    # printed none.
                    #
                    # After Stop-DeviceDebugCapture, not before: that call releases the
                    # COM port the next deploy needs, and it is documented not to throw
                    # -- it warns on a recycled or already-dead pid -- so ordering it
                    # first does not cost the breakdown.
                    Write-ConformancePhaseBreakdown
                }
            }
            elseif ($settings.Kind -eq 'BrokerOutage') {
                $verdict = Invoke-BrokerOutageCheck -Settings $settings -Port $mqttPort
            }

            if ($null -ne $verdict) {
                $outcome = $verdict.Outcome
                $detail = $verdict.Detail

                # Both host-decided kinds take the broker away to make their measurement.
                # Whatever happened, leave one up for the tests that follow.
                if (-not (Get-SmartHomeDevEnvState -Port $mqttPort)) {
                    Start-SuiteBroker -Port $mqttPort
                }
            }
            else {
                # ── Device-decided ────────────────────────────────────────────
                # Let the monitor reboot the device itself (no -NoReboot): attaching
                # to the post-flash boot is a race, and a missed boot looks identical
                # to a test that never reported.
                Write-Host ("Capturing device output for {0}s..." -f $settings.CaptureSeconds) -ForegroundColor Cyan

                # Write the log as it streams (so a capture that dies partway still
                # leaves something to read) and explicitly as UTF-8 -- Tee-Object
                # -FilePath on Windows PowerShell 5.1 writes UTF-16, which grep and
                # most other tools see as an empty file, exactly when someone is
                # investigating.
                # One retry: the monitor gives the device 15s to enumerate, and right
                # after a flash it can miss that window (or lose the race with Visual
                # Studio, which grabs the device when it is open). That failure produces
                # no device output at all, which is distinguishable from a real capture,
                # so retrying it costs nothing but a second window and stops a healthy
                # device being reported as ERROR.
                $captured = $null
                $watchExit = 0
                foreach ($attempt in 1, 2) {
                    # -Until turns CaptureSeconds into a timeout instead of a sleep. A
                    # healthy device reports in seconds; waiting out the full window
                    # anyway was about half this suite's wall clock.
                    & $watchScript -DurationSeconds $settings.CaptureSeconds -NoBuild -Until "[ITEST] $testName" |
                        Tee-Object -Variable captured |
                        Out-File -FilePath $logFile -Encoding utf8
                    $watchExit = $LASTEXITCODE

                    if ($watchExit -eq 0) {
                        break
                    }

                    if ($attempt -eq 1) {
                        Write-Warning "Could not capture device output (exit code $watchExit). Retrying once."
                    }
                }

                if ($watchExit -ne 0) {
                    throw "Watch-DeviceDebugOutput.ps1 exit code $watchExit (after a retry)"
                }

                # Match the marker protocol, not this test's own name: reading back
                # the name the device actually emitted turns a mismatch into a clear
                # message instead of a NO-RESULT that reads like a crashed device or
                # a stale deploy address.
                $marker = $captured |
                    Select-String -Pattern '\[ITEST\]\s+(\S+)\s+(PASS|FAIL)\s*:?\s*(.*)$' |
                    Select-Object -First 1

                if (-not $marker) {
                    $outcome = 'NO-RESULT'
                    $detail = "No [ITEST] marker within $($settings.CaptureSeconds)s"
                }
                else {
                    $reportedName = $marker.Matches[0].Groups[1].Value
                    $reportedDetail = $marker.Matches[0].Groups[3].Value.Trim()

                    if ($reportedName -ne $testName) {
                        $outcome = 'WRONG-TEST'
                        $detail = "device reported '$reportedName' -- a stale deploy, or that project's TestName constant doesn't match its project name"
                    }
                    else {
                        $outcome = $marker.Matches[0].Groups[2].Value
                        $detail = if ($reportedDetail) { $reportedDetail } else { $marker.Line.Trim() }
                    }
                }
            }
        }
        catch {
            $outcome = 'ERROR'
            $detail = $_.Exception.Message
        }

        $results += [pscustomobject]@{
            Test          = $testName
            Outcome       = $outcome
            Detail        = $detail
            Log           = if (Test-Path $logFile) { $logFile } else { $null }
            Seconds       = [int]((Get-Date) - $testStarted).TotalSeconds
            DeploySeconds = $deploySeconds
        }

        $color = if ($outcome -eq 'PASS') { 'Green' } else { 'Red' }
        Write-Host ("{0}: {1} -- {2}" -f $testName, $outcome, $detail) -ForegroundColor $color
        Write-Host ""
    }
}
finally {
    if ($brokerStarted) {
        Write-Host ""
        # Raw call, deliberately not Stop-SuiteBroker: this is a finally, and a throw
        # here would replace whatever outcome the suite had already reached. Which also
        # means the archiving Stop-SuiteBroker does has to be repeated here, for the
        # generation that covers the end of the last test.
        # Kept apart from the teardown below so the two failures stay distinguishable,
        # and both inside a catch: this is the finally, and a throw from either would
        # escape it and take the summary and the exit code with it.
        try {
            Save-BrokerEvidence -Port $mqttPort
        }
        catch {
            Write-Warning "Could not preserve the last broker logs: $($_.Exception.Message)"
        }

        try {
            & $stopEnvScript
        }
        catch {
            Write-Warning "Broker teardown failed: $($_.Exception.Message)"
        }
    }
}

# ── Summary ───────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host ('=' * 69)
Write-Host "Integration test summary" -ForegroundColor Cyan
Write-Host ('=' * 69)

# Total, then how much of it was the deploy. A test that "got slower" is usually a build
# that did, and the two used to be one number -- which is how a 14s swing on
# HomieClientCheck came to be attributed to the check itself.
foreach ($result in $results) {
    $color = if ($result.Outcome -eq 'PASS') { 'Green' } else { 'Red' }
    $timing = "{0}s ({1}s deploy)" -f $result.Seconds, $result.DeploySeconds
    Write-Host ("  {0,-20} {1,-12} {2,-16} {3}" -f $result.Test, $result.Outcome, $timing, $result.Detail) -ForegroundColor $color
}

$totalSeconds = ($results | Measure-Object -Property Seconds -Sum).Sum
$totalDeploySeconds = ($results | Measure-Object -Property DeploySeconds -Sum).Sum
Write-Host ("  {0,-20} {1,-12} {2,-16}" -f 'TOTAL', '', ("{0}s ({1}s deploy)" -f $totalSeconds, $totalDeploySeconds)) -ForegroundColor DarkGray

$failed = @($results | Where-Object { $_.Outcome -ne 'PASS' })

Write-Host ""
Write-Host ("Logs: {0}" -f $LogDirectory) -ForegroundColor DarkGray
Write-Host "  <test>.log, plus <test>-NN-broker.log / -homie.log per broker generation" -ForegroundColor DarkGray

if ($failed.Count -gt 0) {
    Write-Host ""
    Write-Host ("{0} of {1} integration tests did not pass." -f $failed.Count, $results.Count) -ForegroundColor Red
    foreach ($result in @($failed | Where-Object { $_.Log })) {
        Write-Host ("  {0}: {1}" -f $result.Test, $result.Log) -ForegroundColor Red
    }
    exit 1
}

Write-Host ""
Write-Host ("All {0} integration tests passed." -f $results.Count) -ForegroundColor Green
exit 0
