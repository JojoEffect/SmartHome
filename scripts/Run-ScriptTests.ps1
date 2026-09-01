<#
.SYNOPSIS
    Run the host-side script tests -- the desk-provable half of scripts\.

.DESCRIPTION
    scripts\ decides what the integration suite reports. Invoke-HomieConformanceCheck,
    Invoke-BrokerOutageCheck, Wait-ForRetainedValue, Wait-ForAnnounceWitnessed,
    Start-HomieCapture / Stop-HomieCapture, ConvertFrom-HomieCaptureLine and the catalog
    validation are all pure host-side logic -- no device, no broker, no network -- and
    until this entry point existed they had no test home at all. Proving a change to one
    of them meant running the whole on-device suite, or building a throwaway harness that
    was deleted afterwards. Four of those were written and thrown away in a week
    (issue #74).

    This runs every scripts\tests\*.Tests.ps1 file and reports one line per group plus
    the detail of anything that failed.

    It reads no machine configuration on purpose: no local.env.ps1, no COM port, no
    Mosquitto, no restored packages\. A fresh clone can run it, and so can CI. If a test
    ever needs one of those, it is testing the machine rather than the logic.

    A run that executed zero tests fails. That is not defensive spelling: this repository
    shipped three commits on a green vstest run that executed nothing, because the device
    could not load the test assembly and every test was reported skipped. A filter with a
    typo would do the same thing here.

.PARAMETER File
    Only run test files whose name matches one of these wildcards ('Common', 'Homie*').
    The '.Tests.ps1' suffix is optional.

.PARAMETER Name
    Only run cases whose '<group> :: <case>' name matches this wildcard.

.PARAMETER Detailed
    Print every case, not just the per-group counts.

.EXAMPLE
    .\scripts\Run-ScriptTests.ps1
    .\scripts\Run-ScriptTests.ps1 -File RunIntegrationTests -Detailed
    .\scripts\Run-ScriptTests.ps1 -Name '*retained*'
#>

[CmdletBinding()]
param(
    [string[]]$File,

    [string]$Name,

    [switch]$Detailed
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'tests\TestRunner.ps1')

$testsDir = Join-Path $PSScriptRoot 'tests'

# -LiteralPath, here and everywhere below: '[' and ']' are legal in a Windows directory
# name and -Path reads them as character-class syntax, so a checkout at
# 'C:\repos\SmartHome [wip]' finds nothing at all (issue #71). The tests in this
# directory assert that same rule about Common.ps1; the runner should not break it.
$testFiles = @(Get-ChildItem -LiteralPath $testsDir -Filter '*.Tests.ps1' -File | Sort-Object Name)

if ($File) {
    $testFiles = @($testFiles | Where-Object {
        $candidate = $_.Name
        $bare = $candidate -replace '\.Tests\.ps1$', ''
        @($File | Where-Object { $candidate -like $_ -or $bare -like $_ }).Count -gt 0
    })
}

if ($testFiles.Count -eq 0) {
    Write-Error ("No test files to run under {0}{1}. Files there are named <Subject>.Tests.ps1." -f `
        $testsDir, $(if ($File) { " matching: $($File -join ', ')" } else { '' }))
    exit 1
}

$SmartHomeTestState.Filter = $Name
$SmartHomeTestState.Detailed = [bool]$Detailed

# One temp root per run, so New-TestDirectory fixtures cannot collide between files and a
# single Remove-Item clears all of them. Under the process id, so two runs at once (a
# desk and an editor) do not delete each other's fixtures.
$SmartHomeTestState.TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("smarthome-script-tests-{0}" -f $PID)
if (Test-Path -LiteralPath $SmartHomeTestState.TempRoot) {
    Remove-Item -LiteralPath $SmartHomeTestState.TempRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $SmartHomeTestState.TempRoot | Out-Null

$started = Get-Date

Write-Host ""
Write-Host "Host-side script tests" -ForegroundColor Cyan
Write-Host ('=' * 78)

try {
    foreach ($testFile in $testFiles) {
        Write-Host ""
        Write-Host $testFile.Name -ForegroundColor White

        # & rather than dot-source: each file gets its own scope, so the subject it
        # dot-sources (Common.ps1, Run-IntegrationTests.ps1) and any stub it defines go
        # away with it and cannot leak into the next file. The Describe/It/Assert-*
        # functions are still reachable -- PowerShell resolves a command through the
        # scope chain, and they live in this script's scope.
        try {
            & $testFile.FullName
        }
        catch {
            # A file that fails outside a Describe (a bad dot-source, a syntax-level
            # surprise) is one failure, not the end of the run.
            $SmartHomeTestState.Total++
            $SmartHomeTestState.Failed++
            $SmartHomeTestState.Group = $testFile.Name
            Add-TestFailure -Name '<file>' -Record $_
            $SmartHomeTestState.Group = ''
            Write-Host ("  {0,-62} {1}" -f '<file>', 'FAILED to load') -ForegroundColor Red
        }
    }
}
finally {
    if ($SmartHomeTestState.TempRoot -and (Test-Path -LiteralPath $SmartHomeTestState.TempRoot)) {
        Remove-Item -LiteralPath $SmartHomeTestState.TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$elapsed = ((Get-Date) - $started).TotalSeconds

Write-Host ""
Write-Host ('=' * 78)

if ($SmartHomeTestState.Failures.Count -gt 0) {
    Write-Host "Failures" -ForegroundColor Red
    Write-Host ""
    foreach ($failure in $SmartHomeTestState.Failures) {
        Write-Host ("  {0}" -f $failure.Name) -ForegroundColor Red
        Write-Host ("      {0}" -f $failure.Message)
        Write-Host ("      at {0}" -f $failure.Where) -ForegroundColor DarkGray
        Write-Host ""
    }
}

$summary = "{0} passed, {1} failed" -f $SmartHomeTestState.Passed, $SmartHomeTestState.Failed
if ($SmartHomeTestState.Skipped -gt 0) {
    $summary += ", {0} skipped by -Name" -f $SmartHomeTestState.Skipped
}
$summary += " in {0:n1}s" -f $elapsed

if ($SmartHomeTestState.Total -eq 0) {
    Write-Host $summary -ForegroundColor Yellow
    Write-Error ("No tests ran. {0} file(s) were loaded{1}, so this is a filter or a discovery problem, not a pass." -f `
        $testFiles.Count, $(if ($Name) { " and -Name '$Name' matched nothing" } else { '' }))
    exit 1
}

if ($SmartHomeTestState.Failed -gt 0) {
    Write-Host $summary -ForegroundColor Red
    exit 1
}

Write-Host $summary -ForegroundColor Green
exit 0
