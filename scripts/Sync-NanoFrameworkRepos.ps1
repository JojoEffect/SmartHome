<#
.SYNOPSIS
    Clone or update the nanoFramework repositories that support SmartHome development.

.DESCRIPTION
    Keeps a local sibling checkout set beside the SmartHome repository so an agent or
    developer always has current docs, samples, tools, and source references available.
#>

[CmdletBinding()]
param(
    # Reset repos pinned to a specific tag/commit (detached HEAD) back to the tracked
    # branch too. Without this, pinned repos are left alone -- see Common.ps1's
    # Invoke-GitCloneOrUpdate.
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Common.ps1')
Import-SmartHomeLocalEnv -FileName 'nanoFramework.local.env.ps1'

$branch = Get-OptionalEnvValue -Name 'SMARTHOME_NANOFW_BRANCH' -DefaultValue 'main'

$repositories = @(
    'nanoframework/Home',
    'nanoframework/nanoframework.github.io',
    'nanoframework/Samples',
    'nanoframework/nf-interpreter',
    'nanoframework/CoreLibrary',
    'nanoframework/nanoFramework.WebServer',
    'nanoframework/nanoFramework.IoT.Device',
    'nanoframework/nanoFramework.Hardware.Esp32',
    'nanoframework/nanoFramework.Logging',
    'nanoframework/nanoFramework.m2mqtt',
    'nanoframework/nanoFirmwareFlasher',
    'nanoframework/nf-Visual-Studio-extension',
    'nanoframework/nf-tools'
)

Write-Host "Syncing nanoFramework companion repositories to branch '$branch'..." -ForegroundColor Cyan
Write-Host "Target root: $(Get-SiblingRoot)" -ForegroundColor DarkGray

foreach ($repository in $repositories) {
    Invoke-GitCloneOrUpdate -Repository $repository -Branch $branch -Force:$Force
}

Write-Host ""
Write-Host "nanoFramework companion repositories are ready." -ForegroundColor Green
