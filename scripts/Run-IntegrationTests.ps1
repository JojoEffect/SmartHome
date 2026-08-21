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

function Test-DeviceConstant {
    # Compile-time constants in a device project can't be read from local.env.ps1,
    # so they drift. Comparing them up front turns "the test failed on a healthy
    # device" into a warning that names the two values.
    param(
        [string]$TestName,
        [string]$Pattern,
        [string]$Expected,
        [string]$What
    )

    $program = Join-Path $repoRoot "src\integrationTests\$TestName\Program.cs"
    if (-not (Test-Path $program)) {
        return
    }

    $match = Select-String -Path $program -Pattern $Pattern | Select-Object -First 1
    if (-not $match) {
        return
    }

    $actual = $match.Matches[0].Groups[1].Value
    if ($actual -ne $Expected) {
        Write-Warning ("{0}: {1} is '{2}' in Program.cs but '{3}' here. If it fails to connect, one of the two is stale." -f $TestName, $What, $actual, $Expected)
    }
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
        [string]$TestName,
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
    & $stopEnvScript | Out-Null
    & $startEnvScript -Detached -NoSync | Out-Null
    if ($LASTEXITCODE -ne 0) {
        return @{ Outcome = 'ERROR'; Detail = "could not restart the broker before measuring (exit code $LASTEXITCODE)" }
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
        & $stopEnvScript | Out-Null
        Start-Sleep -Seconds $outage

        Write-Host "Bringing a fresh broker up..." -ForegroundColor Cyan
        & $startEnvScript -Detached -NoSync | Out-Null
        if ($LASTEXITCODE -ne 0) {
            return @{ Outcome = 'ERROR'; Detail = "could not restart the broker after the ${outage}s outage (exit code $LASTEXITCODE)" }
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

    return @{
        Outcome = 'PASS'
        Detail  = "republished after outages of {0}s" -f ($Settings.OutageSeconds -join 's, ')
    }
}

# ── Pre-flight ────────────────────────────────────────────────────────────────
$expectedBroker = Get-OptionalEnvValue -Name 'SMARTHOME_MQTT_BROKER' -DefaultValue 'localhost'
foreach ($testName in $Tests) {
    Test-DeviceConstant -TestName $testName -Pattern 'BrokerHost\s*=\s*"([^"]+)"' -Expected $expectedBroker -What 'BrokerHost'

    $settings = $testCatalog[$testName]
    if ($settings.Contains('HeartbeatTopic')) {
        Test-DeviceConstant -TestName $testName -Pattern 'HeartbeatTopic\s*=\s*"([^"]+)"' -Expected $settings.HeartbeatTopic -What 'HeartbeatTopic'
    }

    if ($settings.Kind -eq 'BrokerOutage' -and $NoBroker) {
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
        & $stopEnvScript | Out-Null
    }

    Write-Host "Starting the local MQTT broker (detached)..." -ForegroundColor Cyan
    & $startEnvScript -Detached -NoSync
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to start the local dev environment (exit code $LASTEXITCODE)."
        exit $LASTEXITCODE
    }
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

        $outcome = $null
        $detail = $null

        # One failing test must not abort the rest of the suite: the sub-scripts
        # use Write-Error under $ErrorActionPreference = 'Stop', so a deploy or
        # capture failure arrives here as a terminating error, not an exit code.
        try {
            # ── Deploy ────────────────────────────────────────────────────────
            & $deployScript -Project (Get-TestProjectPath -TestName $testName) -Configuration $Configuration
            if ($LASTEXITCODE -ne 0) {
                throw "Deploy-ToDevice.ps1 exit code $LASTEXITCODE"
            }

            Write-Host ""

            if ($settings.Kind -eq 'BrokerOutage') {
                # ── Host-decided ──────────────────────────────────────────────
                $verdict = Invoke-BrokerOutageCheck -TestName $testName -Settings $settings -Port $mqttPort
                $outcome = $verdict.Outcome
                $detail = $verdict.Detail

                # Whatever happened, leave a broker up for the tests that follow.
                if (-not (Get-SmartHomeDevEnvState -Port $mqttPort)) {
                    & $startEnvScript -Detached -NoSync | Out-Null
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
                    & $watchScript -DurationSeconds $settings.CaptureSeconds -NoBuild |
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
        }

        $color = if ($outcome -eq 'PASS') { 'Green' } else { 'Red' }
        Write-Host ("{0}: {1} -- {2}" -f $testName, $outcome, $detail) -ForegroundColor $color
        Write-Host ""
    }
}
finally {
    if ($brokerStarted) {
        Write-Host ""
        & $stopEnvScript
    }
}

# ── Summary ───────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host ('=' * 69)
Write-Host "Integration test summary" -ForegroundColor Cyan
Write-Host ('=' * 69)

foreach ($result in $results) {
    $color = if ($result.Outcome -eq 'PASS') { 'Green' } else { 'Red' }
    Write-Host ("  {0,-20} {1,-12} {2}" -f $result.Test, $result.Outcome, $result.Detail) -ForegroundColor $color
}

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
