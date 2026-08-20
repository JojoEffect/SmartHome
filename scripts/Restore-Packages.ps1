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

    For each <package id="X" version="Y"> across every packages.config in the repo,
    this script:
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

if (-not (Test-Path $packagesDir)) {
    New-Item -ItemType Directory -Path $packagesDir | Out-Null
}

$configs = Get-ChildItem -Path $repoRoot -Filter 'packages.config' -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

Write-Host "Restoring packages referenced by $($configs.Count) packages.config file(s)..." -ForegroundColor Cyan

$copied = 0
$alreadyPresent = 0
$missing = New-Object System.Collections.Generic.List[string]

foreach ($config in $configs) {
    [xml]$xml = Get-Content $config.FullName
    foreach ($package in $xml.packages.package) {
        $id = $package.id
        $version = $package.version
        $destName = "$id.$version"
        $destPath = Join-Path $packagesDir $destName

        if (Test-Path $destPath) {
            $alreadyPresent++
            continue
        }

        $cachePath = Join-Path $cacheRoot "$($id.ToLowerInvariant())\$version"
        if (Test-Path $cachePath) {
            Copy-Item -Path $cachePath -Destination $destPath -Recurse
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
