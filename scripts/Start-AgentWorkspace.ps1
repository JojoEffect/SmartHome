<#
.SYNOPSIS
    Prepare the full local agent workspace for SmartHome development.

.DESCRIPTION
    1. Syncs the nanoFramework companion repositories beside SmartHome.
    2. Starts Mosquitto and the Homie topic subscriber.

    Use this as the default "session bootstrap" script for local agentic work.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host "Syncing supporting nanoFramework repositories..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'Sync-NanoFrameworkRepos.ps1')
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Starting local MQTT environment..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'Start-DevEnv.ps1')
exit $LASTEXITCODE
