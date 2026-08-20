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

    A local Mosquitto broker is started detached for the run (MqttTest needs one) and
    stopped again at the end, even if the suite fails.

    On success this prints a one-line-per-test summary and exits 0 -- nothing else to
    look at. On failure it exits 1 and prints the captured device log path for the
    failing test, which is where any real investigation starts.

    *** HARDWARE: this flashes and runs code on the physical device on the configured
    COM port, once per test. Treat it exactly like Deploy-ToDevice.ps1. ***

.PARAMETER Tests
    Subset of tests to run, by name (WifiTest, MqttTest, BMP280Test). Default: all,
    in dependency order -- WiFi first, since MqttTest can only fail confusingly if
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
    .\scripts\Run-IntegrationTests.ps1 -Tests WifiTest,MqttTest
    .\scripts\Run-IntegrationTests.ps1 -NoBroker -Configuration Release
#>

[CmdletBinding()]
param(
    [ValidateSet('WifiTest', 'MqttTest', 'BMP280Test')]
    [string[]]$Tests = @('WifiTest', 'MqttTest', 'BMP280Test'),

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

# Per-test capture window. These are "long enough that a healthy device has already
# reported", not "how long the test takes" -- every test emits its marker as early as
# the outcome is known, and then idles. WifiTest gets the longest window because
# NetworkHelper's own connect timeout is 60s.
$testCatalog = @{
    'WifiTest'   = @{ Project = 'src\integrationTests\WifiTest\WifiTest.nfproj';     CaptureSeconds = 75 }
    'MqttTest'   = @{ Project = 'src\integrationTests\MqttTest\MqttTest.nfproj';     CaptureSeconds = 90 }
    'BMP280Test' = @{ Project = 'src\integrationTests\BMP280Test\BMP280Test.nfproj'; CaptureSeconds = 45 }
}

if (-not $LogDirectory) {
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $LogDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "smarthome-integration-$stamp"
}
New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null

$deployScript = Join-Path $PSScriptRoot 'Deploy-ToDevice.ps1'
$watchScript  = Join-Path $PSScriptRoot 'Watch-DeviceDebugOutput.ps1'

# ── Pre-flight ────────────────────────────────────────────────────────────────
# MqttTest's broker address is a compile-time constant in its Program.cs, not a
# local.env value -- a stale constant is by far the most common reason for that
# test to "fail" on a perfectly healthy device, so say so up front rather than
# leaving it to be rediscovered from the log.
if ($Tests -contains 'MqttTest') {
    $mqttProgram = Join-Path $repoRoot 'src\integrationTests\MqttTest\Program.cs'
    $brokerMatch = Select-String -Path $mqttProgram -Pattern 'BrokerHost\s*=\s*"([^"]+)"' | Select-Object -First 1
    if ($brokerMatch) {
        $codeBroker = $brokerMatch.Matches[0].Groups[1].Value
        $localAddresses = @()
        try {
            $localAddresses = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Select-Object -ExpandProperty IPAddress)
        }
        catch {
            # No Get-NetIPAddress here -- skip the check rather than fail the run.
        }
        if ($localAddresses.Count -gt 0 -and ($localAddresses -notcontains $codeBroker)) {
            Write-Warning "MqttTest targets broker $codeBroker, which is not an address of this machine ($($localAddresses -join ', ')). If MqttTest fails to connect, update BrokerHost in src\integrationTests\MqttTest\Program.cs."
        }
    }
}

# ── Broker ────────────────────────────────────────────────────────────────────
$brokerStarted = $false
if (-not $NoBroker) {
    # Clear any environment left behind by an earlier run (or a Ctrl+C'd foreground
    # Start-DevEnv.ps1) first -- otherwise its stale state file makes Start-DevEnv.ps1
    # refuse, and "run the suite" would need a manual cleanup step to become callable.
    & (Join-Path $PSScriptRoot 'Stop-DevEnv.ps1') | Out-Null

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
        $test = $testCatalog[$testName]
        $logFile = Join-Path $LogDirectory "$testName.log"

        Write-Host ('=' * 69)
        Write-Host ("Integration test: {0}" -f $testName) -ForegroundColor Cyan
        Write-Host ('=' * 69)

        # One failing test must not abort the rest of the suite: the sub-scripts
        # use Write-Error under $ErrorActionPreference = 'Stop', so a deploy or
        # capture failure arrives here as a terminating error, not an exit code.
        $output = $null
        try {
            # ── Deploy ────────────────────────────────────────────────────────
            & $deployScript -Project $test.Project -Configuration $Configuration
            if ($LASTEXITCODE -ne 0) {
                throw "Deploy-ToDevice.ps1 exit code $LASTEXITCODE"
            }

            # ── Capture ───────────────────────────────────────────────────────
            # Let the monitor reboot the device itself (no -NoReboot): the build
            # step inside Watch-DeviceDebugOutput.ps1 costs enough time that
            # attaching to the post-flash boot is a race, and a missed boot looks
            # identical to a test that never reported.
            Write-Host ""
            Write-Host ("Capturing device output for {0}s..." -f $test.CaptureSeconds) -ForegroundColor Cyan

            # Write the log as it streams (so a capture that dies partway still leaves
            # something to read) and explicitly as UTF-8 -- Tee-Object -FilePath on
            # Windows PowerShell 5.1 writes UTF-16, which grep and most other tools
            # see as an empty file, exactly when someone is trying to investigate.
            $captured = $null
            & $watchScript -DurationSeconds $test.CaptureSeconds |
                Tee-Object -Variable captured |
                Out-File -FilePath $logFile -Encoding utf8
            $watchExit = $LASTEXITCODE
            $output = $captured

            if ($watchExit -ne 0) {
                throw "Watch-DeviceDebugOutput.ps1 exit code $watchExit"
            }
        }
        catch {
            $logIfAny = if (Test-Path $logFile) { $logFile } else { $null }
            $results += [pscustomobject]@{ Test = $testName; Outcome = 'ERROR'; Detail = $_.Exception.Message; Log = $logIfAny }
            Write-Host ("{0}: ERROR -- {1}" -f $testName, $_.Exception.Message) -ForegroundColor Red
            Write-Host ""
            continue
        }

        # ── Verdict ───────────────────────────────────────────────────────────
        $passLine = $output | Select-String -Pattern ("\[ITEST\]\s+{0}\s+PASS" -f [regex]::Escape($testName)) | Select-Object -First 1
        $failLine = $output | Select-String -Pattern ("\[ITEST\]\s+{0}\s+FAIL:\s*(.*)$" -f [regex]::Escape($testName)) | Select-Object -First 1

        if ($failLine) {
            $reason = $failLine.Matches[0].Groups[1].Value.Trim()
            $results += [pscustomobject]@{ Test = $testName; Outcome = 'FAIL'; Detail = $reason; Log = $logFile }
            Write-Host ("{0}: FAIL -- {1}" -f $testName, $reason) -ForegroundColor Red
        }
        elseif ($passLine) {
            $detail = $passLine.Line.Trim()
            $results += [pscustomobject]@{ Test = $testName; Outcome = 'PASS'; Detail = $detail; Log = $logFile }
            Write-Host ("{0}: PASS" -f $testName) -ForegroundColor Green
        }
        else {
            # No marker at all: the app crashed before reporting, never got deployed
            # where the CLR could find it, or the capture window was too short.
            $results += [pscustomobject]@{ Test = $testName; Outcome = 'NO-RESULT'; Detail = "No [ITEST] marker within $($test.CaptureSeconds)s"; Log = $logFile }
            Write-Host ("{0}: NO RESULT (no marker in {1}s)" -f $testName, $test.CaptureSeconds) -ForegroundColor Red
        }

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
    Write-Host ("  {0,-12} {1,-14} {2}" -f $result.Test, $result.Outcome, $result.Detail) -ForegroundColor $color
}

$failed = @($results | Where-Object { $_.Outcome -ne 'PASS' })

Write-Host ""
Write-Host ("Device logs: {0}" -f $LogDirectory) -ForegroundColor DarkGray

if ($failed.Count -gt 0) {
    Write-Host ""
    Write-Host ("{0} of {1} integration tests did not pass." -f $failed.Count, $results.Count) -ForegroundColor Red
    foreach ($result in $failed) {
        if ($result.Log) {
            Write-Host ("  {0}: {1}" -f $result.Test, $result.Log) -ForegroundColor Red
        }
    }
    exit 1
}

Write-Host ""
Write-Host ("All {0} integration tests passed." -f $results.Count) -ForegroundColor Green
exit 0
