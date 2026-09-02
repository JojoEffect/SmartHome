<#
.SYNOPSIS
    Build and run the unit test suite on real hardware via vstest.console.

.DESCRIPTION
    1. Sources scripts\local.env.ps1 for machine-specific settings.
    2. Builds the unit test project with MSBuild.
    3. Locates the nanoFramework.TestFramework adapter restored under
       packages\ (classic packages.config restore).
    4. Runs vstest.console against the built test assembly using
       src\tests\Unit\nano.runsettings.

    The default run settings file, nano.runsettings, has
    <IsRealHardware>True</IsRealHardware> — this deploys test code to and
    executes it on the physical device on the configured COM port, same as
    Deploy-ToDevice.ps1. Treat it as a hardware-touching action.

    -RunSettings nano.ci.runsettings sets IsRealHardware=False instead and runs
    the same tests on the nanoclr virtual device, touching no COM port and
    deploying nothing. The script reads that flag out of the resolved settings
    file and reports which of the two it did, so a log can be told apart later.

.NOTES
    Requires:
      - MSBuild (Visual Studio Build Tools or full VS installation)
      - vstest.console (ships with Visual Studio)
      - nanoFramework.TestFramework NuGet package restored to packages\
      - scripts\local.env.ps1 populated from the template (COM port)

.EXAMPLE
    .\scripts\Run-Tests.ps1
    .\scripts\Run-Tests.ps1 -Verbose
#>

[CmdletBinding()]
param(
    [string]$Project = 'src\tests\Unit\Unit.nfproj',

    [string]$Configuration = 'Debug',

    # Run settings file, relative to the project directory. The default targets real
    # hardware. CI passes nano.ci.runsettings instead, which sets IsRealHardware=False
    # so the tests execute on the nanoclr virtual device -- the same 34 tests, no board
    # required. Kept as a parameter rather than a switch so the settings stay declarative
    # and reviewable in-repo.
    [string]$RunSettings = 'nano.runsettings'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Common.ps1')
Import-SmartHomeLocalEnv

$repoRoot    = Get-SmartHomeRepoRoot
$projectPath = Join-Path $repoRoot $Project

if (-not (Test-Path $projectPath)) {
    Write-Error "Project not found: $projectPath"
    exit 1
}

# ── Locate MSBuild ────────────────────────────────────────────────────────────
$msbuild = Get-MSBuildPath

# ── Build ─────────────────────────────────────────────────────────────────────
Write-Host "Building $Project ($Configuration)..." -ForegroundColor Cyan
& $msbuild $projectPath /p:Configuration=$Configuration /v:minimal /nologo
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed (exit code $LASTEXITCODE)."
    exit $LASTEXITCODE
}
Write-Host "  Build succeeded." -ForegroundColor Green

# ── Locate build output ───────────────────────────────────────────────────────
$projectDir  = Split-Path $projectPath -Parent
$projectName = Get-NfProjectAssemblyName -ProjectPath $projectPath
$testDll     = Join-Path $projectDir "bin\$Configuration\$projectName.dll"

if (-not (Test-Path $testDll)) {
    Write-Error "Test assembly not found: $testDll"
    exit 1
}

$runSettings = Join-Path $projectDir $RunSettings
if (-not (Test-Path $runSettings)) {
    Write-Error "Run settings not found: $runSettings"
    exit 1
}

# ── Locate vstest.console and the nanoFramework test adapter ─────────────────
$vstest = Get-VsTestPath
$vstestCmd = Get-Command $vstest -ErrorAction SilentlyContinue
if (-not $vstestCmd -and -not (Test-Path $vstest)) {
    Write-Error @"
vstest.console not found.
It ships with Visual Studio (Common7\IDE\CommonExtensions\Microsoft\TestWindow).
Install the "Visual Studio Build Tools" or full Visual Studio, or run the
tests from Visual Studio's Test Explorer instead.
"@
    exit 1
}

$adapterDir = Get-NanoFrameworkTestAdapterDir -RepoRoot $repoRoot
if (-not $adapterDir) {
    Write-Error @"
nanoFramework test adapter not found under packages\.
Restore NuGet packages for $Project (nanoFramework.TestFramework), e.g. via
Visual Studio's "Restore NuGet Packages", then re-run this script.
"@
    exit 1
}

# ── Run tests ─────────────────────────────────────────────────────────────────
# Which target the run uses is declared by the settings file, so read it instead of
# assuming the hardware default: -RunSettings nano.ci.runsettings sets IsRealHardware
# False and executes on the nanoclr virtual device, with no COM port and no deploy.
# CLAUDE.md singles this script out as one of three that talk to the physical ESP32,
# and this line is the only thing in the output saying which of the two a given run
# was -- a virtual run announcing "on hardware" misleads exactly the person trying to
# reconcile what was and was not exercised on the device.
#
# SelectSingleNode rather than $xml.RunSettings.nanoFrameworkAdapter.IsRealHardware:
# under Set-StrictMode a missing element is an error on property access, and a
# caller-supplied settings file is allowed to omit it.
#
# Name it from the resolved path, not from $RunSettings: PowerShell variables are
# case-insensitive, so the $runSettings assignment above has already overwritten the
# parameter of that name with the full path.
$runSettingsName = Split-Path $runSettings -Leaf
[xml]$runSettingsXml = Get-Content -Path $runSettings -Raw
$hardwareNode = $runSettingsXml.SelectSingleNode('/RunSettings/nanoFrameworkAdapter/IsRealHardware')
$portNode     = $runSettingsXml.SelectSingleNode('/RunSettings/nanoFrameworkAdapter/RealHardwarePort')
$hardwareFlag = if ($hardwareNode) { $hardwareNode.InnerText.Trim() } else { '' }
$hardwarePort = if ($portNode) { $portNode.InnerText.Trim() } else { '' }

if ($hardwareFlag -eq 'True') {
    $target = 'real hardware'
    $targetDetail = if ($hardwarePort) {
        "COM port $hardwarePort from $runSettingsName"
    } else {
        # An empty RealHardwarePort means the adapter takes the first nanoDevice it
        # finds, which is not necessarily SMARTHOME_COM_PORT. Don't name a port the
        # run may not have used.
        "first device found; $runSettingsName names no COM port"
    }
} elseif ($hardwareFlag -eq 'False') {
    $target = 'the nanoclr virtual device'
    $targetDetail = "no COM port, no deploy; from $runSettingsName"
} else {
    # Only reachable through a -RunSettings file declaring neither value, in which case
    # the adapter picks and this script genuinely cannot say which target ran.
    $target = 'an undeclared target'
    $targetDetail = "$runSettingsName sets no IsRealHardware, so the adapter chooses"
}

Write-Host ""
Write-Host "Running tests on $target ($targetDetail) via vstest.console..." -ForegroundColor Cyan
Write-Host "  Adapter: $adapterDir"
Write-Host "  Settings: $runSettings"

# A TRX logger, not just the exit code. vstest exits 0 when every test is SKIPPED,
# and skipping everything is exactly what happens when the device-side launcher
# can't load the test assembly -- which this repo hit for real when the assembly
# was renamed. An exit code alone reported that as a pass. The TRX counters are
# also locale-independent, unlike vstest's console summary.
$resultsDir = Join-Path $projectDir 'TestResults'
$trxName = 'nano-tests.trx'
$trxPath = Join-Path $resultsDir $trxName
Remove-Item -Path $trxPath -Force -ErrorAction SilentlyContinue

& $vstest $testDll "/Settings:$runSettings" "/TestAdapterPath:$adapterDir" "/ResultsDirectory:$resultsDir" "/logger:trx;LogFileName=$trxName"
$testExit = $LASTEXITCODE

if (-not (Test-Path $trxPath)) {
    Write-Error "vstest.console produced no test results at $trxPath (exit code $testExit). Nothing can be concluded from this run."
    exit 1
}

[xml]$trx = Get-Content -Path $trxPath -Raw
$counters = $trx.TestRun.ResultSummary.Counters
$total    = [int]$counters.total
$executed = [int]$counters.executed
$passed   = [int]$counters.passed
$failed   = [int]$counters.failed

Write-Host ""
Write-Host ("Results: {0} passed, {1} failed, {2} of {3} executed." -f $passed, $failed, $executed, $total) -ForegroundColor Cyan

if ($testExit -ne 0 -or $failed -gt 0) {
    Write-Error "Tests failed (exit code $testExit, $failed failed). Results: $trxPath"
    exit 1
}

if ($executed -eq 0 -or $passed -eq 0) {
    Write-Error @"
No test actually ran ($total discovered, $executed executed) -- this is NOT a pass.
The usual cause is the device-side launcher failing to load the test assembly, which
it reports by skipping every test while vstest still exits 0. Check the run output for
'Assembly::Load' / ArgumentException, and confirm the test project's AssemblyName is
still the one nanoFramework.TestFramework expects.
Results: $trxPath
"@
    exit 1
}

if ($executed -lt $total) {
    # A partial skip is a failure too, for the same reason the all-skipped case above is:
    # the device-side launcher skips what it cannot load, and vstest still exits 0. This
    # used to warn and then print "Tests passed", so 27 of 28 tests silently vanishing
    # reported success.
    Write-Error @"
$($total - $executed) of $total tests were skipped -- this is NOT a pass.
Tests are skipped when the device-side launcher cannot load or resolve them, so a
partial skip usually means a test class or method failed to load rather than that it
was deliberately excluded. Check the run output for 'Assembly::Load' / ArgumentException.
Results: $trxPath
"@
    exit 1
}

Write-Host ""
Write-Host "Tests passed on $target." -ForegroundColor Green

# Explicit success code -- see the note in Sync-NanoFrameworkRepos.ps1.
exit 0
