<#
.SYNOPSIS
    Restore classic packages.config-style NuGet packages for every project in the repo.

.DESCRIPTION
    This repo's nanoFramework projects use classic packages.config restore, not
    PackageReference. `msbuild /t:Restore` is a no-op for that style ("None of the
    specified projects contain packages to restore"), and nuget.exe isn't installed
    on every dev machine -- so a plain build can fail with a missing deployment
    image even though "Build succeeded", if a referenced package version was never
    extracted into the repo-local packages\ folder (e.g. after switching branches,
    or after hand-editing a packages.config to pin/revert a version).

    For each <package id="X" version="Y"> across every packages.config in *this*
    checkout -- linked worktrees under .claude\worktrees\ are other checkouts and are
    left to restore their own -- this script:
      1. Skips it if packages\X.Y\ already exists.
      2. Otherwise copies it from the local NuGet global cache
         (%NUGET_PACKAGES% or ~\.nuget\packages\<id>\<version>\), which already has
         every version Visual Studio has ever restored on this machine.
      3. Reports anything not found in either place -- this script does not hit the
         network, by design, so it's safe to call unattended from other scripts.

    It then reports, in one line, how many folders in packages\ no packages.config in
    this checkout references -- the restore only ever added to that folder, so a version
    bump left the previous version behind indefinitely. -Prune removes them.

.PARAMETER Prune
    Remove the packages\ folders nothing in this checkout references, one line per
    removal. Supports -WhatIf, which is how to see the list without removing anything.

    Off by default: packages\ is not this script's to empty on a run someone asked for
    something else, and a stale folder costs disk rather than correctness now that
    Get-NanoFrameworkTestAdapterDir resolves by reference (issue #79).

.EXAMPLE
    .\scripts\Restore-Packages.ps1

.EXAMPLE
    .\scripts\Restore-Packages.ps1 -Prune -WhatIf
    Lists the unreferenced packages\ folders without touching any of them.
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Prune
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Common.ps1')

$repoRoot = Get-SmartHomeRepoRoot
$packagesDir = Join-Path $repoRoot 'packages'

$cacheRoot = [Environment]::GetEnvironmentVariable('NUGET_PACKAGES')
if ([string]::IsNullOrWhiteSpace($cacheRoot)) {
    $cacheRoot = Join-Path $env:USERPROFILE '.nuget\packages'
}

# -LiteralPath throughout this script for every path derived from the checkout root or
# from the NuGet cache root: -Path reads '[' and ']' as wildcard character-class syntax,
# and both are legal in a Windows directory name, so on a checkout at
# 'C:\repos\SmartHome [wip]' every one of these tests answered the opposite of the truth
# (issue #71). New-Item is the exception below -- it has no -LiteralPath in Windows
# PowerShell 5.1 and creates the literal name anyway.
if (-not (Test-Path -LiteralPath $packagesDir)) {
    New-Item -ItemType Directory -Path $packagesDir | Out-Null
}

# This checkout's references only -- see Get-SmartHomePackagesConfig for why a worktree's
# must not be restored into the checkout the script was run from, and
# Get-SmartHomeReferencedPackage for why the preflight reads the same list through the
# same parser.
$referenced = Get-SmartHomeReferencedPackage -RepoRoot $repoRoot

Write-Host "Restoring $($referenced.Count) package(s) referenced by this checkout..." -ForegroundColor Cyan

$copied = 0
$alreadyPresent = 0
$missing = New-Object System.Collections.Generic.List[string]

foreach ($package in $referenced) {
    if (Test-Path -LiteralPath $package.Path) {
        $alreadyPresent++
        continue
    }

    $cachePath = Join-Path $cacheRoot "$($package.Id.ToLowerInvariant())\$($package.Version)"
    if (Test-Path -LiteralPath $cachePath) {
        Copy-Item -LiteralPath $cachePath -Destination $package.Path -Recurse
        Write-Host "  restored: $($package.Name)" -ForegroundColor Green
        $copied++
    }
    else {
        $missing.Add($package.Name)
    }
}

Write-Host ""
Write-Host "$alreadyPresent already present, $copied restored from local NuGet cache." -ForegroundColor Cyan

# ── packages\ folders this checkout references no more ───────────────────────
# The loop above only ever adds, and nothing else prunes: measured on the main checkout
# on 2026-09-01, 61 folders against 29 references (issue #79). Reported unconditionally
# because it is one line and the folder is otherwise invisible; removed only on -Prune,
# because this script is called unattended by Initialize-Worktree.ps1, which asked for a
# checkout to be filled in and not for anything to be deleted.
$unreferenced = Get-SmartHomeUnreferencedPackageDir -PackagesDir $packagesDir -ReferencedPackage $referenced

if ($unreferenced.Count -eq 0) {
    Write-Host "packages\ carries nothing this checkout no longer references." -ForegroundColor DarkGray
}
elseif (-not $Prune) {
    Write-Host "$($unreferenced.Count) folder(s) in packages\ are referenced by nothing in this checkout; -Prune removes them (-Prune -WhatIf lists them)." -ForegroundColor Yellow
}
else {
    # The complement of an empty set is the whole folder. A checkout that references
    # nothing is a broken or partial one -- Test-Setup.ps1 reports it as such -- and
    # emptying packages\ on the strength of it would read as this script having done its
    # job. Refused rather than warned, because the removal is not reversible.
    if ($referenced.Count -eq 0) {
        Write-Error @"
Refusing to prune: this checkout references no packages at all, so all
$($unreferenced.Count) folder(s) in packages\ would be removed. That is a broken or
partial checkout rather than an empty reference set -- run .\scripts\Test-Setup.ps1.
"@
        exit 1
    }

    Write-Host ""
    $pruned = 0
    foreach ($folder in $unreferenced) {
        if ($PSCmdlet.ShouldProcess($folder.FullName, 'Remove unreferenced package folder')) {
            Remove-Item -LiteralPath $folder.FullName -Recurse -Force
            Write-Host "  pruned: $($folder.Name)" -ForegroundColor Yellow
            $pruned++
        }
    }

    Write-Host "$pruned of $($unreferenced.Count) unreferenced folder(s) removed from packages\." -ForegroundColor Cyan
}

if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Error @"
$($missing.Count) package(s) not found in packages\ or the local NuGet cache ($cacheRoot):
$($missing -join "`n")

This script deliberately doesn't hit the network. Restore them once via Visual Studio
(open SmartHome.sln, it will prompt to restore NuGet packages) or NuGet Package
Manager, which populates the cache this script reads from -- then re-run this script.
"@
    exit 1
}

Write-Host "All referenced packages are available." -ForegroundColor Green

# Explicit success code: Initialize-Worktree.ps1 reads $LASTEXITCODE after invoking this
# script, and without this it would carry whatever the last native command happened to
# leave behind. (This comment used to name Run-IntegrationTests.ps1 and Start-DevEnv.ps1
# as the callers; neither has invoked this script for some time.)
exit 0
