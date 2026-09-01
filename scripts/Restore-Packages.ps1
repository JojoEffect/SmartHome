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

.EXAMPLE
    .\scripts\Restore-Packages.ps1
#>

[CmdletBinding()]
param()

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

# This checkout's configs only -- see Get-SmartHomePackagesConfig for why a worktree's
# must not be restored into the checkout the script was run from.
$configs = Get-SmartHomePackagesConfig -RepoRoot $repoRoot

Write-Host "Restoring packages referenced by $($configs.Count) packages.config file(s)..." -ForegroundColor Cyan

$copied = 0
$alreadyPresent = 0
$missing = New-Object System.Collections.Generic.List[string]

foreach ($config in $configs) {
    [xml]$xml = Get-Content -LiteralPath $config.FullName
    foreach ($package in $xml.packages.package) {
        $id = $package.id
        $version = $package.version
        $destName = "$id.$version"
        $destPath = Join-Path $packagesDir $destName

        if (Test-Path -LiteralPath $destPath) {
            $alreadyPresent++
            continue
        }

        $cachePath = Join-Path $cacheRoot "$($id.ToLowerInvariant())\$version"
        if (Test-Path -LiteralPath $cachePath) {
            Copy-Item -LiteralPath $cachePath -Destination $destPath -Recurse
            Write-Host "  restored: $destName" -ForegroundColor Green
            $copied++
        }
        elseif (-not $missing.Contains($destName)) {
            $missing.Add($destName)
        }
    }
}

Write-Host ""
Write-Host "$alreadyPresent already present, $copied restored from local NuGet cache." -ForegroundColor Cyan

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

# Explicit success code: callers (and Run-IntegrationTests.ps1 / Start-DevEnv.ps1) read
# $LASTEXITCODE after invoking this script, and without this it would carry whatever the
# last native command happened to leave behind.
exit 0
