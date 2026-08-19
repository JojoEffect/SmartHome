Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SmartHomeRepoRoot {
    return (Split-Path $PSScriptRoot -Parent)
}

function Get-SmartHomeScriptsDir {
    return $PSScriptRoot
}

function Import-SmartHomeLocalEnv {
    param(
        [string]$FileName = 'local.env.ps1'
    )

    $scriptsDir = Get-SmartHomeScriptsDir
    $localEnv = Join-Path $scriptsDir $FileName
    $template = Join-Path $scriptsDir ($FileName -replace '\.ps1$', '.template.ps1')

    if (-not (Test-Path $localEnv)) {
        Write-Error @"
Missing: $localEnv
Copy the template and fill in your machine settings:
    Copy-Item "$template" "$localEnv"
"@
        exit 1
    }

    . $localEnv
}

function Get-RequiredEnvValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        Write-Error "Missing environment variable: $Name"
        exit 1
    }

    return $value
}

function Get-OptionalEnvValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$DefaultValue
    )

    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $DefaultValue
    }

    return $value
}

function Get-VsWherePath {
    $programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
    if ([string]::IsNullOrWhiteSpace($programFilesX86)) {
        return $null
    }

    $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        return $vswhere
    }

    return $null
}

function Get-MSBuildPath {
    $vswhere = Get-VsWherePath
    if ($vswhere) {
        $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null | Select-Object -First 1
        if ($msbuild) {
            return $msbuild
        }
    }

    return 'msbuild'
}

function Get-VsTestPath {
    $vswhere = Get-VsWherePath
    if ($vswhere) {
        $vstest = & $vswhere -latest -requires Microsoft.VisualStudio.Component.ManagedDesktop.Core -find 'Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe' 2>$null | Select-Object -First 1
        if ($vstest) {
            return $vstest
        }
    }

    return 'vstest.console'
}

function Get-NanoFrameworkTestAdapterDir {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $packagesDir = Join-Path $RepoRoot 'packages'
    $adapterDll = Get-ChildItem -Path $packagesDir -Recurse -Filter 'nanoFramework.TestAdapter.dll' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1

    if (-not $adapterDll) {
        return $null
    }

    return $adapterDll.DirectoryName
}

function Get-SiblingRoot {
    $override = [Environment]::GetEnvironmentVariable('SMARTHOME_NANOFW_ROOT')
    if (-not [string]::IsNullOrWhiteSpace($override)) {
        return $override
    }

    return (Split-Path (Get-SmartHomeRepoRoot) -Parent)
}

function Invoke-GitCloneOrUpdate {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Repository,

        [string]$Branch = 'main'
    )

    $targetRoot = Get-SiblingRoot
    $name = ($Repository -split '/')[1]
    $targetPath = Join-Path $targetRoot $name
    $repoUrl = "https://github.com/$Repository.git"

    if (-not (Test-Path $targetPath)) {
        Write-Host "Cloning $Repository..." -ForegroundColor Cyan
        git clone --branch $Branch --single-branch $repoUrl $targetPath
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Branch '$Branch' not found for $Repository. Falling back to default branch."
            if (Test-Path $targetPath) {
                Remove-Item $targetPath -Recurse -Force
            }
            git clone $repoUrl $targetPath
        }
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to clone $Repository"
            exit $LASTEXITCODE
        }
        return
    }

    Write-Host "Updating $Repository..." -ForegroundColor Cyan
    git -C $targetPath fetch --prune origin
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to fetch $Repository"
        exit $LASTEXITCODE
    }

    git -C $targetPath checkout $Branch
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Branch '$Branch' not found in $Repository. Keeping repository on its current/default branch."
        git -C $targetPath pull --ff-only
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to pull $Repository"
            exit $LASTEXITCODE
        }
        return
    }

    git -C $targetPath pull --ff-only origin $Branch
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to pull $Repository"
        exit $LASTEXITCODE
    }
}
