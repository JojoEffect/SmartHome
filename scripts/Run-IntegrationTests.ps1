<#
.SYNOPSIS
    Run the whole on-device integration test suite in one call.

.DESCRIPTION
    One entry point for every project under src\integrationTests. For each test, in
    order, this script:

      1. deploys it to the ESP32 (Deploy-ToDevice.ps1),
      2. reboots the device and captures its managed debug output
         (Watch-DeviceDebugOutput.ps1 -> tools\DeviceDebugMonitor),
      3. decides the outcome by matching the "[ITEST] <name> PASS/FAIL" marker the
         test emits (see src\integrationTests\TestSupport\IntegrationTest.cs).

    A local Mosquitto broker is started detached for the run (MqttCheck needs one)
    and stopped again at the end, even if the suite fails.

    On success this prints a one-line-per-test summary and exits 0 -- nothing else to
    look at. On failure it exits 1 and prints the captured device log path for the
    failing test, which is where any real investigation starts.

    *** HARDWARE: this flashes and runs code on the physical device on the configured
    COM port, once per test. Treat it exactly like Deploy-ToDevice.ps1. ***

.PARAMETER Tests
    Subset of tests to run, by name. Defaults to every entry of $testCatalog below,
    in dependency order -- WiFi first, since MqttCheck can only fail confusingly if
    the network itself is broken.

.PARAMETER Configuration
    Build configuration passed through to Deploy-ToDevice.ps1. Default Debug.

.PARAMETER NoBroker
    Don't start/stop Mosquitto. Use when a broker is already running (started by
    Start-DevEnv.ps1, or a service), or when running only non-MQTT tests.

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
# The value is the capture window -- "long enough that a healthy device has already
# reported", not how long the test takes. Every test emits its marker as soon as the
# outcome is known and then idles. WifiCheck gets the longest window because
# NetworkHelper's own connect timeout is 60s.
$testCatalog = [ordered]@{
    'WifiCheck'   = 75
    'MqttCheck'   = 90
    'Bmp280Check' = 45
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

$deployScript = Join-Path $PSScriptRoot 'Deploy-ToDevice.ps1'
$watchScript  = Join-Path $PSScriptRoot 'Watch-DeviceDebugOutput.ps1'

# ── Pre-flight ────────────────────────────────────────────────────────────────
# MqttCheck's broker address is a compile-time constant in its Program.cs, while
# everything host-side uses SMARTHOME_MQTT_BROKER from local.env.ps1. A stale
# constant is by far the most common reason for that test to "fail" on a perfectly
# healthy device, so compare the two up front rather than leaving it to be
# rediscovered from the log.
if ($Tests -contains 'MqttCheck') {
    $mqttProgram = Join-Path $repoRoot 'src\integrationTests\MqttCheck\Program.cs'
    $brokerMatch = Select-String -Path $mqttProgram -Pattern 'BrokerHost\s*=\s*"([^"]+)"' | Select-Object -First 1
    $expectedBroker = Get-OptionalEnvValue -Name 'SMARTHOME_MQTT_BROKER' -DefaultValue 'localhost'

    if ($brokerMatch) {
        $codeBroker = $brokerMatch.Matches[0].Groups[1].Value
        if ($codeBroker -ne $expectedBroker) {
            Write-Warning ("MqttCheck targets broker {0}, but SMARTHOME_MQTT_BROKER is {1}. If MqttCheck fails to connect, one of the two is stale -- BrokerHost in src\integrationTests\MqttCheck\Program.cs, or local.env.ps1." -f $codeBroker, $expectedBroker)
        }
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
        & (Join-Path $PSScriptRoot 'Stop-DevEnv.ps1') | Out-Null
    }

    Write-Host "Starting the local MQTT broker (detached)..." -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'Start-DevEnv.ps1') -Detached -NoSync
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
        $captureSeconds = $testCatalog[$testName]
        $project = "src\integrationTests\$testName\$testName.nfproj"
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
            & $deployScript -Project $project -Configuration $Configuration
            if ($LASTEXITCODE -ne 0) {
                throw "Deploy-ToDevice.ps1 exit code $LASTEXITCODE"
            }

            # ── Capture ───────────────────────────────────────────────────────
            # Let the monitor reboot the device itself (no -NoReboot): attaching to
            # the post-flash boot is a race, and a missed boot looks identical to a
            # test that never reported.
            Write-Host ""
            Write-Host ("Capturing device output for {0}s..." -f $captureSeconds) -ForegroundColor Cyan

            # Write the log as it streams (so a capture that dies partway still
            # leaves something to read) and explicitly as UTF-8 -- Tee-Object
            # -FilePath on Windows PowerShell 5.1 writes UTF-16, which grep and most
            # other tools see as an empty file, exactly when someone is investigating.
            $captured = $null
            & $watchScript -DurationSeconds $captureSeconds -NoBuild |
                Tee-Object -Variable captured |
                Out-File -FilePath $logFile -Encoding utf8
            if ($LASTEXITCODE -ne 0) {
                throw "Watch-DeviceDebugOutput.ps1 exit code $LASTEXITCODE"
            }

            # ── Verdict ───────────────────────────────────────────────────────
            # Match the marker protocol, not this test's own name: reading back the
            # name the device actually emitted turns a mismatch into a clear message
            # instead of a NO-RESULT that reads like a crashed device or a stale
            # deploy address.
            $marker = $captured |
                Select-String -Pattern '\[ITEST\]\s+(\S+)\s+(PASS|FAIL)\s*:?\s*(.*)$' |
                Select-Object -First 1

            if (-not $marker) {
                $outcome = 'NO-RESULT'
                $detail = "No [ITEST] marker within ${captureSeconds}s"
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
        & (Join-Path $PSScriptRoot 'Stop-DevEnv.ps1')
    }
}

# ── Summary ───────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host ('=' * 69)
Write-Host "Integration test summary" -ForegroundColor Cyan
Write-Host ('=' * 69)

foreach ($result in $results) {
    $color = if ($result.Outcome -eq 'PASS') { 'Green' } else { 'Red' }
    Write-Host ("  {0,-12} {1,-12} {2}" -f $result.Test, $result.Outcome, $result.Detail) -ForegroundColor $color
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
