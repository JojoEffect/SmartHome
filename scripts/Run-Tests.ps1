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

    nano.runsettings has <IsRealHardware>True</IsRealHardware> — this
    deploys test code to and executes it on the physical device on the
    configured COM port, same as Deploy-ToDevice.ps1. Treat it as a
    hardware-touching action.

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

    [string]$Configuration = 'Debug'
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

$runSettings = Join-Path $projectDir 'nano.runsettings'
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

# ── Run tests on hardware ─────────────────────────────────────────────────────
Write-Host ""
Write-Host "Running tests on hardware via vstest.console..." -ForegroundColor Cyan
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
Write-Host "Tests passed." -ForegroundColor Green

# Explicit success code -- see the note in Sync-NanoFrameworkRepos.ps1.
exit 0
