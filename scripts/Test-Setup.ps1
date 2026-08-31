<#
.SYNOPSIS
    Check everything the other scripts assume already exists on this machine.

.DESCRIPTION
    The rest of scripts\ depends on things that live outside the repository: two
    git-ignored local.env files, a set of installed tools, an authenticated gh, a
    restored packages\ folder. None of that is version-controlled and none of it
    can be inferred from a clone, so every one of them is an assumption -- and
    each fails in a way that reads like broken code rather than a missing
    prerequisite. An unrestored packages\ folder, for instance, emits a wall of
    CS0518 "predefined type 'System.Object' is not defined" across every project.

    This script reports all of them at once, rather than letting a workflow
    discover one, stop, and hide the next. It is read-only: nothing is installed,
    nothing is written, no COM port is opened and no device is touched.

    Deliberately does NOT use Import-SmartHomeLocalEnv -- that helper exits on the
    first missing file, which is the opposite of what a preflight is for.

    Exit code is 0 when nothing FAILed (WARNings still pass), 1 otherwise.

.EXAMPLE
    .\scripts\Test-Setup.ps1
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Common.ps1')

$repoRoot = Get-SmartHomeRepoRoot
$scriptsDir = Get-SmartHomeScriptsDir

$results = New-Object System.Collections.Generic.List[psobject]

function Add-Result {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][ValidateSet('OK', 'WARN', 'FAIL')][string]$Status,
        [Parameter(Mandatory = $true)][string]$Detail,
        [string]$Fix = ''
    )

    $results.Add([pscustomobject]@{
        Name   = $Name
        Status = $Status
        Detail = $Detail
        Fix    = $Fix
    })
}

function Test-OnPath {
    param([Parameter(Mandatory = $true)][string]$Name)

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    return $null
}

# The main working tree, when this is running inside a linked worktree; $null in the
# main checkout and whenever git cannot answer. Used only to steer the remediation:
# both config files and packages\ are git-ignored, so a fresh worktree has none of
# them even though the main checkout is fully configured -- and Initialize-Worktree.ps1
# fixes all three in one call, which is a better answer than three separate ones.
$mainCheckout = $null
if (Test-SmartHomeLinkedWorktree) {
    $mainCheckout = Get-SmartHomeMainWorktreeRoot
}

# ---------------------------------------------------------------- config files --

$configFiles = @(
    @{ Name = 'local.env.ps1'; Values = @('SMARTHOME_COM_PORT', 'SMARTHOME_MOSQUITTO_DIR') },
    @{ Name = 'nanoFramework.local.env.ps1'; Values = @('SMARTHOME_NANOFW_BRANCH') }
)

# What the config files actually provided, as opposed to what happens to be in the
# process environment. The checks below read this and never $env: directly: these
# scripts dot-source local.env.ps1 into the caller's own process, so after any other
# SmartHome script has run in the same shell the variables are still set even once the
# file is gone -- and a preflight would then pass a mosquitto/COM-port check on behalf
# of a config file it just reported as missing.
$configValues = @{}

foreach ($config in $configFiles) {
    $path = Join-Path $scriptsDir $config.Name
    $template = Join-Path $scriptsDir ($config.Name -replace '\.ps1$', '.template.ps1')

    if (-not (Test-Path $path)) {
        $fix = "Copy-Item `"$template`" `"$path`"  (then fill it in)"
        if ($mainCheckout) {
            $fix = ".\scripts\Initialize-Worktree.ps1  -- this is a worktree; the file is git-ignored, " +
                   "so it did not come along. That copies it from $mainCheckout and restores packages\ too"
        }

        Add-Result -Name "scripts\$($config.Name)" -Status 'FAIL' -Detail 'missing' -Fix $fix
        continue
    }

    Add-Result -Name "scripts\$($config.Name)" -Status 'OK' -Detail 'present'

    # Dot-source into this scope so the values below are readable. Safe: both files
    # are assignments to $env: only.
    . $path

    foreach ($name in $config.Values) {
        $value = [Environment]::GetEnvironmentVariable($name)
        $configValues[$name] = $value

        if ([string]::IsNullOrWhiteSpace($value)) {
            Add-Result -Name $name -Status 'FAIL' -Detail 'not set' `
                       -Fix "Set it in $path -- compare against $template"
        }
        else {
            Add-Result -Name $name -Status 'OK' -Detail $value
        }
    }
}

# ------------------------------------------------------------------- mosquitto --

$mosquittoDir = $configValues['SMARTHOME_MOSQUITTO_DIR']
if (-not [string]::IsNullOrWhiteSpace($mosquittoDir)) {
    $mosquitto = Join-Path $mosquittoDir 'mosquitto.exe'
    if (Test-Path $mosquitto) {
        Add-Result -Name 'mosquitto.exe' -Status 'OK' -Detail $mosquitto
    }
    else {
        Add-Result -Name 'mosquitto.exe' -Status 'FAIL' -Detail "not at $mosquittoDir" `
                   -Fix 'Install Mosquitto (https://mosquitto.org/download/), or point SMARTHOME_MOSQUITTO_DIR at the directory holding mosquitto.exe'
    }
}
else {
    # Say so rather than dropping the row: a check that silently disappears reads as
    # a check that passed.
    Add-Result -Name 'mosquitto.exe' -Status 'WARN' -Detail 'not checked -- SMARTHOME_MOSQUITTO_DIR unset'
}

# -------------------------------------------------------------------- COM port --

$comPort = $configValues['SMARTHOME_COM_PORT']
if (-not [string]::IsNullOrWhiteSpace($comPort)) {
    # Enumerates names only -- the port is never opened, so this cannot disturb a
    # device or collide with a session that has it open.
    $ports = @([System.IO.Ports.SerialPort]::GetPortNames())
    if ($ports -contains $comPort) {
        Add-Result -Name "COM port $comPort" -Status 'OK' -Detail 'present'
    }
    else {
        $seen = if ($ports.Count -gt 0) { $ports -join ', ' } else { 'none' }
        Add-Result -Name "COM port $comPort" -Status 'WARN' -Detail "not present (this machine has: $seen)" `
                   -Fix 'Plug the ESP32 in, or correct SMARTHOME_COM_PORT. Only the hardware-touching scripts need it'
    }
}
else {
    Add-Result -Name 'COM port' -Status 'WARN' -Detail 'not checked -- SMARTHOME_COM_PORT unset'
}

# ----------------------------------------------------------------------- tools --

foreach ($tool in @('git', 'nanoff', 'gh')) {
    $source = Test-OnPath $tool
    if ($source) {
        Add-Result -Name $tool -Status 'OK' -Detail $source
    }
    else {
        $fix = switch ($tool) {
            'git'    { 'Install Git and put it on PATH' }
            'nanoff' { 'dotnet tool install -g nanoff' }
            'gh'     { 'Install the GitHub CLI (https://cli.github.com/)' }
        }
        Add-Result -Name $tool -Status 'FAIL' -Detail 'not on PATH' -Fix $fix
    }
}

if (Test-OnPath 'gh') {
    # Not piped through 2>&1 on purpose: in Windows PowerShell 5.1 that wraps a native
    # command's ordinary stderr in a NativeCommandError and flips $?, so a healthy gh
    # would be reported as broken.
    $authOk = $false
    try {
        & gh auth status 2>$null | Out-Null
        $authOk = ($LASTEXITCODE -eq 0)
    }
    catch {
        $authOk = $false
    }

    if ($authOk) {
        Add-Result -Name 'gh auth' -Status 'OK' -Detail 'authenticated'
    }
    else {
        Add-Result -Name 'gh auth' -Status 'FAIL' -Detail 'not authenticated' `
                   -Fix 'gh auth login -- the backlog is GitHub issues, so it is unreadable until then'
    }
}

# ----------------------------------------------------- Visual Studio-side tools --

# Both resolvers fall back to a bare name off PATH when vswhere finds nothing, so a
# returned value is not by itself proof the tool exists.
foreach ($vsTool in @(
    @{ Name = 'MSBuild'; Path = (Get-MSBuildPath) },
    @{ Name = 'vstest.console'; Path = (Get-VsTestPath) }
)) {
    $path = $vsTool.Path
    if ((Test-Path $path -ErrorAction SilentlyContinue) -or (Test-OnPath $path)) {
        Add-Result -Name $vsTool.Name -Status 'OK' -Detail $path
    }
    else {
        Add-Result -Name $vsTool.Name -Status 'FAIL' -Detail "not found (resolved to '$path')" `
                   -Fix 'Install Visual Studio with the MSBuild and Managed Desktop components'
    }
}

# ------------------------------------------------------------------- packages\ --

$packagesDir = Join-Path $repoRoot 'packages'
$missingPackages = New-Object System.Collections.Generic.List[string]

# Worktrees live *inside* the main checkout (.claude\worktrees\<name>), each a full
# copy with its own packages.config set and its own packages\ folder. Recursing into
# them would report on other checkouts than the one being checked: from the main
# checkout that is 155 packages.config instead of 14, and a worktree that has pinned a
# different package version to chase a regression would produce a "not restored"
# failure here for a package this checkout does not need.
#
# Anchored to $repoRoot rather than matched as a substring anywhere in the path: this
# script usually runs *from* a worktree, whose own paths all contain
# '\.claude\worktrees\', and a bare substring test would exclude every file it is
# supposed to check.
$worktreesDir = Join-Path $repoRoot '.claude\worktrees'
$configs = @(Get-ChildItem -Path $repoRoot -Filter 'packages.config' -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object {
        $_.FullName -notmatch '\\(bin|obj)\\' -and
        -not $_.FullName.StartsWith($worktreesDir, [StringComparison]::OrdinalIgnoreCase)
    })

foreach ($configFile in $configs) {
    [xml]$xml = Get-Content $configFile.FullName

    # XPath rather than $xml.packages.package: under Set-StrictMode -Version Latest the
    # property access throws on a packages.config with no <package> children, which
    # would abort the whole preflight. SelectNodes just returns an empty list.
    foreach ($package in $xml.SelectNodes('/packages/package')) {
        $name = "$($package.id).$($package.version)"
        if (-not (Test-Path (Join-Path $packagesDir $name)) -and -not $missingPackages.Contains($name)) {
            $missingPackages.Add($name)
        }
    }
}

if ($configs.Count -eq 0) {
    Add-Result -Name 'packages\' -Status 'WARN' -Detail 'no packages.config found' `
               -Fix 'Unexpected -- check this is a full checkout'
}
elseif ($missingPackages.Count -eq 0) {
    Add-Result -Name 'packages\' -Status 'OK' -Detail "all packages restored ($($configs.Count) packages.config)"
}
else {
    # In a worktree, name the one script that fixes this row and the config rows
    # together -- a fresh worktree is normally missing all of them at once.
    $packagesFix = '.\scripts\Restore-Packages.ps1 -- otherwise every project fails to compile with CS0518, which looks like broken source'
    if ($mainCheckout) {
        $packagesFix = '.\scripts\Initialize-Worktree.ps1 -- otherwise every project fails to compile with CS0518, which looks like broken source'
    }

    Add-Result -Name 'packages\' -Status 'FAIL' -Detail "$($missingPackages.Count) package(s) not restored, e.g. $($missingPackages[0])" `
               -Fix $packagesFix
}

# The unit-test adapter lives inside packages\, so this only means anything once the
# restore above is clean -- but naming it separately saves guessing when Run-Tests.ps1
# is the thing that failed.
$adapterDir = Get-NanoFrameworkTestAdapterDir -RepoRoot $repoRoot
if ($adapterDir) {
    Add-Result -Name 'nanoFramework test adapter' -Status 'OK' -Detail $adapterDir
}
else {
    $adapterFix = '.\scripts\Restore-Packages.ps1 -- Run-Tests.ps1 cannot run without it'
    if ($mainCheckout) {
        $adapterFix = '.\scripts\Initialize-Worktree.ps1 -- Run-Tests.ps1 cannot run without it'
    }

    Add-Result -Name 'nanoFramework test adapter' -Status 'FAIL' -Detail 'nanoFramework.TestAdapter.dll not in packages\' `
               -Fix $adapterFix
}

# -------------------------------------------------------------- sibling repos --

$siblingRoot = Get-SiblingRoot
$siblingProbe = @('nf-interpreter', 'nanoFramework.m2mqtt', 'Samples')
$missingSiblings = @($siblingProbe | Where-Object { -not (Test-Path (Join-Path $siblingRoot $_)) })

if ($missingSiblings.Count -eq 0) {
    Add-Result -Name 'companion nanoFramework repos' -Status 'OK' -Detail $siblingRoot
}
else {
    Add-Result -Name 'companion nanoFramework repos' -Status 'WARN' -Detail "missing under $siblingRoot`: $($missingSiblings -join ', ')" `
               -Fix '.\scripts\Sync-NanoFrameworkRepos.ps1 -- needed before any firmware/library debugging, not for building'
}

# ---------------------------------------------------------------------- report --

Write-Host ""
Write-Host "SmartHome setup check" -ForegroundColor Cyan
Write-Host "  repo:    $repoRoot"
if ($mainCheckout) {
    Write-Host "  worktree of: $mainCheckout" -ForegroundColor DarkGray
}
Write-Host ""

$width = ($results | ForEach-Object { $_.Name.Length } | Measure-Object -Maximum).Maximum

foreach ($result in $results) {
    $colour = switch ($result.Status) {
        'OK'   { 'Green' }
        'WARN' { 'Yellow' }
        'FAIL' { 'Red' }
    }

    Write-Host ("  [{0,-4}] {1}  {2}" -f $result.Status, $result.Name.PadRight($width), $result.Detail) -ForegroundColor $colour
}

$failed = @($results | Where-Object { $_.Status -eq 'FAIL' })
$warned = @($results | Where-Object { $_.Status -eq 'WARN' })

$needsAction = @($results | Where-Object { $_.Status -ne 'OK' -and $_.Fix })
if ($needsAction.Count -gt 0) {
    Write-Host ""
    Write-Host "How to fix:" -ForegroundColor Cyan
    foreach ($result in $needsAction) {
        Write-Host "  $($result.Name)" -ForegroundColor Yellow
        Write-Host "    $($result.Fix)"
    }
}

Write-Host ""
Write-Host "$($results.Count) checked, $($failed.Count) failed, $($warned.Count) warned." -ForegroundColor Cyan

if ($failed.Count -gt 0) {
    Write-Host ""

    # Non-terminating on purpose. $ErrorActionPreference is 'Stop' for the body of this
    # script, which would make Write-Error throw -- the `exit 1` below would never run,
    # and the exception would propagate into whatever called this script, so a caller
    # could never reach its own next line to read $LASTEXITCODE. The contract of a
    # preflight is its exit code, so the message goes to the error stream without
    # terminating.
    $ErrorActionPreference = 'Continue'
    Write-Error "Setup is incomplete: $($failed.Count) check(s) failed. See 'How to fix' above."
    exit 1
}

Write-Host "Setup looks complete." -ForegroundColor Green

# Explicit success code, same reason as Restore-Packages.ps1: callers read
# $LASTEXITCODE, which would otherwise carry whatever the last native command left.
exit 0
