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

function Get-NfProjectAssemblyName {
    # The built output is named after <AssemblyName>, which since the SmartHome.*
    # rename is NOT the project file's own name (src\devices\RoomSensor\RoomSensor.nfproj
    # produces SmartHome.Devices.RoomSensor.exe/.bin). Anything looking for build
    # output has to read the project rather than assume the two match.
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    $content = Get-Content -Path $ProjectPath -Raw
    $match = [regex]::Match($content, '<AssemblyName>\s*([^<]+?)\s*</AssemblyName>')
    if ($match.Success) {
        return $match.Groups[1].Value
    }

    return [System.IO.Path]::GetFileNameWithoutExtension($ProjectPath)
}

function Get-SiblingRoot {
    $override = [Environment]::GetEnvironmentVariable('SMARTHOME_NANOFW_ROOT')
    if (-not [string]::IsNullOrWhiteSpace($override)) {
        return $override
    }

    # Companion repos belong beside the *main* SmartHome checkout, not beside
    # whatever directory this script happens to run from. Inside a linked git
    # worktree (.claude\worktrees\<name>) the plain parent directory would be the
    # worktrees folder, which would clone 14 nanoFramework repos into it and hide
    # the siblings the main checkout already has. Resolve the main working tree via
    # git's common dir instead; fall back to the plain parent when git can't answer
    # (no git on PATH, not a repo, an exported source tree).
    $repoRoot = Get-SmartHomeRepoRoot

    $commonDir = git -C $repoRoot rev-parse --git-common-dir 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($commonDir)) {
        if (-not [System.IO.Path]::IsPathRooted($commonDir)) {
            $commonDir = Join-Path $repoRoot $commonDir
        }

        $resolved = Resolve-Path -Path $commonDir -ErrorAction SilentlyContinue
        if ($resolved) {
            # <main repo>\.git  ->  <main repo>  ->  the directory holding it
            $mainRepoRoot = Split-Path $resolved.Path -Parent
            return (Split-Path $mainRepoRoot -Parent)
        }
    }

    return (Split-Path $repoRoot -Parent)
}

function Invoke-GitCloneOrUpdate {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Repository,

        [string]$Branch = 'main',

        # Repos deliberately pinned to a tag/commit (detached HEAD -- e.g. matched to an
        # exact package version for source lookup) are skipped by default. Re-running the
        # sync must never silently reset an intentional pin back to $Branch.
        [switch]$Force
    )

    $targetRoot = Get-SiblingRoot
    $name = ($Repository -split '/')[1]
    $targetPath = Join-Path $targetRoot $name
    $repoUrl = "https://github.com/$Repository.git"

    if ((Test-Path $targetPath) -and -not $Force) {
        $currentRef = git -C $targetPath rev-parse --abbrev-ref HEAD 2>$null
        if ($currentRef -eq 'HEAD') {
            $pinnedCommit = git -C $targetPath rev-parse --short HEAD 2>$null
            Write-Host "Skipping $Repository -- pinned to a specific commit/tag ($pinnedCommit, detached HEAD)." -ForegroundColor Yellow
            Write-Host "  Pass -Force to this script to reset it back to '$Branch'." -ForegroundColor DarkGray
            return
        }
    }

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

# ── Dev-environment state ─────────────────────────────────────────────────────
# Start-DevEnv.ps1 records what it spawned so Stop-DevEnv.ps1 (or the
# integration-test runner) can shut it down from a different shell. Keyed by
# MQTT port so two ports can be up at once without clobbering each other.
#
# Each entry stores the process NAME and START TIME next to the PID on purpose:
# Windows recycles PIDs, so a state file left behind by a crash could otherwise
# name a PID that now belongs to something else entirely -- and Stop-DevEnv.ps1
# would kill an unrelated process. Every lookup re-verifies all three.

function Get-SmartHomeDevEnvStateFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Port
    )

    return (Join-Path ([System.IO.Path]::GetTempPath()) "smarthome-devenv-$Port.json")
}

function Get-SmartHomeRecordValue {
    # Reads one field from a state record that may be a hashtable (freshly built)
    # or a PSCustomObject (round-tripped through JSON), without tripping
    # Set-StrictMode on a field an older state file didn't have.
    param(
        $Record,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($null -eq $Record) {
        return $null
    }

    if ($Record -is [hashtable]) {
        if ($Record.ContainsKey($Name)) {
            return $Record[$Name]
        }
        return $null
    }

    $property = $Record.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function New-SmartHomeProcessRecord {
    param(
        [System.Diagnostics.Process]$Process,

        # Set when the recorded process is a launcher (e.g. a cmd.exe wrapper doing
        # the log redirect) and the real work happens in its children: stopping it
        # then has to take the whole tree, or the grandchild is orphaned.
        [switch]$Tree
    )

    if ($null -eq $Process) {
        return $null
    }

    return @{
        Id        = $Process.Id
        Name      = $Process.ProcessName
        StartTime = $Process.StartTime.ToString('o')
        Tree      = [bool]$Tree
    }
}

function Get-SmartHomeRecordedProcess {
    # Returns the live process ONLY when pid, name and start time all still match.
    # Anything else (exited, or a recycled pid now owned by another program) is
    # reported as "not running" rather than acted on.
    param(
        $Record
    )

    $processId = Get-SmartHomeRecordValue -Record $Record -Name 'Id'
    if ($null -eq $processId) {
        return $null
    }

    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        return $null
    }

    $recordedName = Get-SmartHomeRecordValue -Record $Record -Name 'Name'
    if ($recordedName -and $process.ProcessName -ne $recordedName) {
        return $null
    }

    $recordedStart = Get-SmartHomeRecordValue -Record $Record -Name 'StartTime'
    if ($recordedStart) {
        try {
            $parsed = [datetime]::Parse(
                $recordedStart,
                [cultureinfo]::InvariantCulture,
                [System.Globalization.DateTimeStyles]::RoundtripKind)

            if ([math]::Abs(($process.StartTime - $parsed).TotalSeconds) -gt 2) {
                return $null
            }
        }
        catch {
            return $null
        }
    }

    return $process
}

function Stop-SmartHomeRecordedProcess {
    param(
        $Record,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $processId = Get-SmartHomeRecordValue -Record $Record -Name 'Id'
    if ($null -eq $processId) {
        return
    }

    $process = Get-SmartHomeRecordedProcess -Record $Record
    if ($null -eq $process) {
        $occupant = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($occupant) {
            Write-Warning ("Not stopping PID {0} ({1}): that pid now belongs to '{2}'. Leaving it alone." -f $processId, $Label, $occupant.ProcessName)
        }
        else {
            Write-Host ("  {0} (PID {1}) already gone." -f $Label, $processId) -ForegroundColor DarkGray
        }
        return
    }

    Write-Host ("  Stopping {0} (PID {1})..." -f $Label, $processId) -ForegroundColor Yellow

    if (Get-SmartHomeRecordValue -Record $Record -Name 'Tree') {
        # taskkill /T takes the children too. Stop-Process would kill only the
        # launcher and leave the real process running with no way back to it.
        & taskkill.exe /PID $processId /T /F *> $null
        return
    }

    Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
}

function Test-SmartHomeDevEnvRunning {
    # True when at least one recorded process is still genuinely alive.
    param(
        $State
    )

    foreach ($name in @('Broker', 'Subscriber')) {
        $record = Get-SmartHomeRecordValue -Record $State -Name $name
        if (Get-SmartHomeRecordedProcess -Record $record) {
            return $true
        }
    }

    return $false
}

function Get-SmartHomeOrphanBroker {
    # Mosquitto instances this repo started that no state file covers any more:
    # a run from before the state file existed, or one whose shell was killed
    # hard. Matched on OUR generated config file name, so a broker someone else
    # started (a service, another project) is never touched.
    param(
        [string]$Port
    )

    $pattern = if ($Port) { "smarthome-mosquitto-$Port\.conf" } else { 'smarthome-mosquitto-\d+\.conf' }

    Get-CimInstance Win32_Process -Filter "Name = 'mosquitto.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -match $pattern }
}

function Get-SmartHomeOrphanSubscriber {
    # The homie/# subscriber half of the same problem. Its own command line has no
    # log path on it (the cmd.exe wrapper owns the redirect), so it is matched on
    # the subscription this repo makes -- `-t homie/#` on the configured port.
    param(
        [string]$Port
    )

    Get-CimInstance Win32_Process -Filter "Name = 'mosquitto_sub.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.CommandLine -and
            $_.CommandLine -match 'homie/#' -and
            (-not $Port -or $_.CommandLine -match "-p\s+$Port\b")
        }
}

function Save-SmartHomeDevEnvState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Port,

        [Parameter(Mandatory = $true)]
        [hashtable]$State
    )

    # Deliberately returns nothing: callers only care that the state landed, and a
    # returned path would leak into their own output stream.
    $stateFile = Get-SmartHomeDevEnvStateFile -Port $Port
    $State | ConvertTo-Json -Depth 4 | Set-Content -Path $stateFile -Encoding utf8
}

function Get-SmartHomeDevEnvState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Port
    )

    $stateFile = Get-SmartHomeDevEnvStateFile -Port $Port
    if (-not (Test-Path $stateFile)) {
        return $null
    }

    try {
        return (Get-Content $stateFile -Raw | ConvertFrom-Json)
    }
    catch {
        # A truncated/corrupt state file must not wedge Stop-DevEnv.ps1 -- drop it
        # and report "nothing running" rather than throwing.
        Write-Warning "Ignoring unreadable dev-env state file: $stateFile"
        Remove-Item -Path $stateFile -Force -ErrorAction SilentlyContinue
        return $null
    }
}

function Clear-SmartHomeDevEnvState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Port
    )

    Remove-Item -Path (Get-SmartHomeDevEnvStateFile -Port $Port) -Force -ErrorAction SilentlyContinue
}
