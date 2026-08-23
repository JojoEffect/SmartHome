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
    failing test, which is where any real investigation starts.

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
    Where to write the per-test device logs. Defaults to a timestamped folder under
    the system temp directory; the path is printed at the end of the run.

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
#   - Ending the wait after 250ms of quiet output: 7 failures. The output is buffered and
#     lands in chunks, so "quiet" does not mean "finished".
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

    $out = Get-SmartHomeDevEnvPath -Port $Port -Kind Snapshot
    Remove-Item -Path $out -Force -ErrorAction SilentlyContinue

    $sub = Get-MosquittoTool -Name 'mosquitto_sub.exe'

    # Arguments come from Common.ps1, not from a second copy of them here. The parser
    # below depends on the exact '%t %r %p' layout, and that layout is chosen and
    # explained in Get-SmartHomeSubscriberArguments -- spelling it out again meant a
    # one-line change in the file that owns it would silently break this reader.
    # Quoting idiom matches Start-DevEnv.ps1's subscriber launch.
    $subscriberArgs = (Get-SmartHomeSubscriberArguments -Port $Port |
        ForEach-Object { if ($_ -match '[\s/#]') { '"{0}"' -f $_ } else { $_ } }) -join ' '
    $command = '/c ""{0}" {1} > "{2}" 2>&1"' -f $sub, $subscriberArgs, $out
    $process = Start-Process -FilePath 'cmd.exe' -ArgumentList $command -PassThru -WindowStyle Hidden

    Start-Sleep -Seconds $SnapshotSettleSeconds
    Stop-SmartHomeProcessTree -ProcessId $process.Id

    $snapshot = @{}
    foreach ($line in @(Get-Content -Path $out -ErrorAction SilentlyContinue)) {
        # "<topic> <0|1> <payload>", and the payload may itself contain spaces.
        $match = [regex]::Match($line, '^(\S+)\s+([01])\s?(.*)$')
        if ($match.Success) {
            $snapshot[$match.Groups[1].Value] = @{
                Retained = $match.Groups[2].Value -eq '1'
                Payload  = $match.Groups[3].Value
            }
        }
    }

    return $snapshot
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
        [bool]$RequireRetained = $true
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $seen = '<nothing>'
    $snapshot = @{}

    while ((Get-Date) -lt $deadline) {
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

    foreach ($property in $commands.Keys) {
        Publish-HomieCommand -Port $Port -Topic "$node/$property/set" -Payload $commands[$property]
    }

    # One snapshot per round, not one per property. Every reflection is checked
    # against the same snapshot, so five properties cost one snapshot instead of
    # five -- the snapshot has to be a fresh subscriber (retain flags are only set on
    # replay), which is what makes it expensive.
    $pending = @($commands.Keys)
    $lastSeen = @{}
    $deadline = (Get-Date).AddSeconds($Settings.CommandTimeoutSeconds)

    while ($pending.Count -gt 0 -and (Get-Date) -lt $deadline) {
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

    # ── the lifecycle states a device can be driven into ─────────────────────
    Write-Host "  driving `$state through alert and sleeping..." -ForegroundColor DarkGray
    # ready -> alert -> ready -> sleeping -> ready. Not ready -> alert -> sleeping:
    # alert may only return to ready (or disconnect), so that would be asking the
    # device for a transition the convention's own state machine forbids.
    foreach ($state in 'alert', 'ready', 'sleeping', 'ready') {
        Publish-HomieCommand -Port $Port -Topic "$node/lifecycle/set" -Payload $state
        # -RequireRetained $false: this reads Ok/Seen only and discards the snapshot. The
        # device writes $state within milliseconds of the command, so insisting on a
        # replayed copy would spend a second full snapshot re-observing the same value,
        # four times per run.
        $reached = Wait-ForRetainedValue -Port $Port -Topic "$root/`$state" -Expected $state -TimeoutSeconds $Settings.CommandTimeoutSeconds -RequireRetained $false
        if (-not $reached.Ok) {
            $script:conformanceFailures += "device did not reach `$state='$state' on command (saw '$($reached.Seen)')"
        }
    }

    # ── a replaced broker gets the whole announcement again ──────────────────
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

    if ($script:conformanceFailures.Count -gt 0) {
        return @{
            Outcome = 'FAIL'
            Detail  = "$($script:conformanceFailures.Count) conformance failure(s): " + ($script:conformanceFailures -join '; ')
        }
    }

    return @{
        Outcome = 'PASS'
        Detail  = "attributes, datatypes, retained flags, /set round-trip, alert/sleeping and re-announce all conform"
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

        Write-Host ('=' * 69)
        Write-Host ("Integration test: {0}" -f $testName) -ForegroundColor Cyan
        Write-Host ('=' * 69)

        $testStarted = Get-Date
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

            Write-Host ""

            # ── Host-decided ──────────────────────────────────────────────────
            # Dispatch first, then handle the verdict once. The two kinds differ only in
            # which function produces it; everything after was duplicated line for line,
            # and had already drifted -- only one copy carried the comment explaining why
            # the broker is restarted.
            $verdict = $null
            if ($settings.Kind -eq 'HomieConformance') {
                $verdict = Invoke-HomieConformanceCheck -Settings $settings -Port $mqttPort
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
            Test    = $testName
            Outcome = $outcome
            Detail  = $detail
            Log     = if (Test-Path $logFile) { $logFile } else { $null }
            Seconds = [int]((Get-Date) - $testStarted).TotalSeconds
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
        # here would replace whatever outcome the suite had already reached.
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

foreach ($result in $results) {
    $color = if ($result.Outcome -eq 'PASS') { 'Green' } else { 'Red' }
    Write-Host ("  {0,-20} {1,-12} {2,5}s  {3}" -f $result.Test, $result.Outcome, $result.Seconds, $result.Detail) -ForegroundColor $color
}

Write-Host ("  {0,-20} {1,-12} {2,5}s" -f 'TOTAL', '', (($results | Measure-Object -Property Seconds -Sum).Sum)) -ForegroundColor DarkGray

$failed = @($results | Where-Object { $_.Outcome -ne 'PASS' })

Write-Host ""
Write-Host ("Device logs: {0}" -f $LogDirectory) -ForegroundColor DarkGray

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
