<#
.SYNOPSIS
    Build and run the NFUnitTest suite on real hardware via vstest.console.

.DESCRIPTION
    1. Sources scripts\local.env.ps1 for machine-specific settings.
    2. Builds the NFUnitTest project with MSBuild.
    3. Locates the nanoFramework.TestFramework adapter restored under
       packages\ (classic packages.config restore).
    4. Runs vstest.console against the built test assembly using
       src\tests\NFUnitTest\nano.runsettings.

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
    [string]$Project = 'src\tests\NFUnitTest\NFUnitTest.nfproj',

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
$projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
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

& $vstest $testDll "/Settings:$runSettings" "/TestAdapterPath:$adapterDir"
$testExit = $LASTEXITCODE

if ($testExit -ne 0) {
    Write-Error "Tests failed or vstest.console reported errors (exit code $testExit)."
    exit $testExit
}

Write-Host ""
Write-Host "Tests passed." -ForegroundColor Green

# Explicit success code -- see the note in Sync-NanoFrameworkRepos.ps1.
exit 0
