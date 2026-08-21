Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Memo slots for the expensive lookups below (vswhere, git rev-parse). They live in
# the global scope so they survive across the sub-scripts a suite run invokes in the
# same process, and they are declared here because Set-StrictMode makes *reading* an
# unset variable an error -- the memo check itself would throw on first use.
foreach ($memo in 'SmartHomeMSBuildPath', 'SmartHomeVsTestPath', 'SmartHomeSiblingRoot') {
    if (-not (Test-Path "variable:global:$memo")) {
        Set-Variable -Name $memo -Scope Global -Value $null
    }
}

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
    # Memoised in the global scope on purpose: vswhere costs ~440ms per call, and
    # a suite run invokes Deploy-ToDevice.ps1 once per test -- those are separate
    # script scopes but the same process, so a script-scoped cache would miss.
    if ($global:SmartHomeMSBuildPath) {
        return $global:SmartHomeMSBuildPath
    }

    $resolved = 'msbuild'
    $vswhere = Get-VsWherePath
    if ($vswhere) {
        $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null | Select-Object -First 1
        if ($msbuild) {
            $resolved = $msbuild
        }
    }

    $global:SmartHomeMSBuildPath = $resolved
    return $resolved
}

function Get-VsTestPath {
    if ($global:SmartHomeVsTestPath) {
        return $global:SmartHomeVsTestPath
    }

    $resolved = 'vstest.console'
    $vswhere = Get-VsWherePath
    if ($vswhere) {
        $vstest = & $vswhere -latest -requires Microsoft.VisualStudio.Component.ManagedDesktop.Core -find 'Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe' 2>$null | Select-Object -First 1
        if ($vstest) {
            $resolved = $vstest
        }
    }

    $global:SmartHomeVsTestPath = $resolved
    return $resolved
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

    # Memoised: Invoke-GitCloneOrUpdate re-derives this per repository, so one sync
    # would otherwise spawn `git rev-parse` 15 times for a value that cannot change
    # while the process lives.
    if ($global:SmartHomeSiblingRoot) {
        return $global:SmartHomeSiblingRoot
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
            $global:SmartHomeSiblingRoot = Split-Path $mainRepoRoot -Parent
            return $global:SmartHomeSiblingRoot
        }
    }

    $global:SmartHomeSiblingRoot = Split-Path $repoRoot -Parent
    return $global:SmartHomeSiblingRoot
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

# ── Dev environment: file vocabulary ──────────────────────────────────────────
# Every file the dev environment creates is named here and nowhere else. The
# orphan scan below recognises leftover processes by these same names, so a
# rename that only touched Start-DevEnv.ps1 would silently turn the scan into a
# permanent "nothing found" -- which reads as success.

$SmartHomeDevEnvPrefix = 'smarthome'
$SmartHomeHomieTopic = 'homie/#'

function Get-SmartHomeDevEnvPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Port,

        [Parameter(Mandatory = $true)]
        [ValidateSet('State', 'Config', 'BrokerLog', 'SubscriberLog', 'SubscriberErrorLog')]
        [string]$Kind
    )

    $suffix = switch ($Kind) {
        'State'              { "devenv-$Port.json" }
        'Config'             { "mosquitto-$Port.conf" }
        'BrokerLog'          { "mosquitto-$Port.log" }
        'SubscriberLog'      { "homie-$Port.log" }
        'SubscriberErrorLog' { "homie-$Port.err.log" }
    }

    return (Join-Path ([System.IO.Path]::GetTempPath()) "$SmartHomeDevEnvPrefix-$suffix")
}

function Get-SmartHomeSubscriberArguments {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Port
    )

    # -F '%t %r %p' rather than -v: the retain flag is part of the Homie convention
    # ("All messages MUST be sent as retained, UNLESS stated otherwise") and only the
    # broker can confirm it, but -v prints topic and payload only. Lines become
    # "<topic> <0|1> <payload>", which readers still match by topic prefix.
    return @('-h', 'localhost', '-p', $Port, '-t', $SmartHomeHomieTopic, '-F', '%t %r %p')
}

# ── Dev environment: state ────────────────────────────────────────────────────
# Start-DevEnv.ps1 records what it spawned so Stop-DevEnv.ps1 (or the
# integration-test runner) can shut it down from a different shell. Keyed by
# MQTT port so two ports can be up at once without clobbering each other.
#
# The state holds an ordered list of process records, each with its own label,
# rather than named slots -- adding something else to the environment (a second
# subscriber, a bridge) is then a change in Start-DevEnv.ps1 alone. Every record
# carries the process NAME and START TIME next to the pid, because Windows
# recycles pids: a state file left behind by a crash can easily name a pid that
# now belongs to something else, and stopping that would be worse than leaking.

function New-SmartHomeProcessRecord {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Label,

        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,

        # Set when the recorded process is a launcher (e.g. a cmd.exe wrapper doing
        # a log redirect) and the real work happens in its children: stopping it
        # then has to take the whole tree, or the grandchild is orphaned.
        [switch]$Tree
    )

    return @{
        Label     = $Label
        Id        = $Process.Id
        Name      = $Process.ProcessName
        StartTime = $Process.StartTime.ToString('o')
        Tree      = [bool]$Tree
    }
}

function ConvertTo-SmartHomeHashtable {
    # ConvertFrom-Json hands back PSCustomObjects, where a missing property throws
    # under Set-StrictMode. Normalising to hashtables once, here at the
    # deserialization boundary, means every reader can use $x['Field'] and get
    # $null for anything absent.
    param($InputObject)

    if ($null -eq $InputObject) {
        return $null
    }

    if ($InputObject -is [System.Collections.IDictionary]) {
        return $InputObject
    }

    if ($InputObject -is [object[]]) {
        return @(foreach ($item in $InputObject) { ConvertTo-SmartHomeHashtable -InputObject $item })
    }

    if ($InputObject.GetType().Name -eq 'PSCustomObject') {
        $result = @{}
        foreach ($property in $InputObject.PSObject.Properties) {
            $result[$property.Name] = ConvertTo-SmartHomeHashtable -InputObject $property.Value
        }
        return $result
    }

    return $InputObject
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
    $State | ConvertTo-Json -Depth 5 |
        Set-Content -Path (Get-SmartHomeDevEnvPath -Port $Port -Kind State) -Encoding utf8
}

function Get-SmartHomeDevEnvState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Port
    )

    $stateFile = Get-SmartHomeDevEnvPath -Port $Port -Kind State
    if (-not (Test-Path $stateFile)) {
        return $null
    }

    try {
        return (ConvertTo-SmartHomeHashtable -InputObject (Get-Content $stateFile -Raw | ConvertFrom-Json))
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

    Remove-Item -Path (Get-SmartHomeDevEnvPath -Port $Port -Kind State) -Force -ErrorAction SilentlyContinue
}

# ── Dev environment: processes ────────────────────────────────────────────────

function Get-SmartHomeRecordedProcess {
    # Returns the live process ONLY when pid, name and start time all still match.
    # Anything else (exited, or a recycled pid now owned by another program) is
    # reported as "not running" rather than acted on.
    param(
        [hashtable]$Record
    )

    if ($null -eq $Record -or $null -eq $Record['Id']) {
        return $null
    }

    $process = Get-Process -Id $Record['Id'] -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        return $null
    }

    if ($Record['Name'] -and $process.ProcessName -ne $Record['Name']) {
        return $null
    }

    if ($Record['StartTime']) {
        try {
            $recorded = [datetime]::Parse(
                $Record['StartTime'],
                [cultureinfo]::InvariantCulture,
                [System.Globalization.DateTimeStyles]::RoundtripKind)

            if ([math]::Abs(($process.StartTime - $recorded).TotalSeconds) -gt 2) {
                return $null
            }
        }
        catch {
            return $null
        }
    }

    return $process
}

function Stop-SmartHomeProcessTree {
    # taskkill /T takes the children too. Stop-Process would kill only the
    # launcher and leave the real process running with no way back to it.
    param(
        [Parameter(Mandatory = $true)]
        [int]$ProcessId
    )

    & taskkill.exe /PID $ProcessId /T /F *> $null
}

function Stop-SmartHomeRecordedProcess {
    param(
        [hashtable]$Record
    )

    if ($null -eq $Record -or $null -eq $Record['Id']) {
        return $false
    }

    $processId = $Record['Id']
    $label = if ($Record['Label']) { $Record['Label'] } else { "PID $processId" }

    if ($null -eq (Get-SmartHomeRecordedProcess -Record $Record)) {
        $occupant = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($occupant) {
            Write-Warning ("Not stopping PID {0} ({1}): that pid now belongs to '{2}'. Leaving it alone." -f $processId, $label, $occupant.ProcessName)
        }
        else {
            Write-Host ("  {0} (PID {1}) already gone." -f $label, $processId) -ForegroundColor DarkGray
        }
        return $false
    }

    Write-Host ("  Stopping {0} (PID {1})..." -f $label, $processId) -ForegroundColor Yellow

    if ($Record['Tree']) {
        Stop-SmartHomeProcessTree -ProcessId $processId
    }
    else {
        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
    }

    return $true
}

function Test-SmartHomeDevEnvRunning {
    # True when at least one recorded process is still genuinely alive.
    param(
        [hashtable]$State
    )

    foreach ($record in @($State['Processes'])) {
        if (Get-SmartHomeRecordedProcess -Record $record) {
            return $true
        }
    }

    return $false
}

function Get-SmartHomeOrphanProcess {
    # Broker/subscriber processes this repo started that no state file covers: a
    # run from before the state file existed, or one whose shell was killed hard.
    # Matched against the file vocabulary at the top of this section, so a broker
    # someone else started is never touched. One WMI query covers both names --
    # enumerating Win32_Process is the most expensive call in this whole path.
    param(
        [string]$Port
    )

    $portPattern = if ($Port) { [regex]::Escape($Port) } else { '\d+' }
    $configLeaf = Split-Path (Get-SmartHomeDevEnvPath -Port '__PORT__' -Kind Config) -Leaf
    $configPattern = [regex]::Escape($configLeaf).Replace('__PORT__', $portPattern)
    $topicPattern = [regex]::Escape($SmartHomeHomieTopic)
    # Topic and port are matched independently -- they appear in whichever order
    # Get-SmartHomeSubscriberArguments emits them, and that order is not this
    # function's business.
    $subscriberPortPattern = '-p\s+{0}\b' -f $portPattern

    Get-CimInstance Win32_Process -Filter "Name = 'mosquitto.exe' OR Name = 'mosquitto_sub.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.CommandLine -and (
                ($_.Name -eq 'mosquitto.exe' -and $_.CommandLine -match $configPattern) -or
                ($_.Name -eq 'mosquitto_sub.exe' -and
                    $_.CommandLine -match $topicPattern -and
                    $_.CommandLine -match $subscriberPortPattern))
        }
}

function Stop-SmartHomeDevEnv {
    # The one teardown implementation: Stop-DevEnv.ps1 is a thin wrapper around
    # it, and Start-DevEnv.ps1's own failure and Ctrl+C paths call it too, so
    # there is never more than one idea of what "stop the dev environment" means.
    # Returns $true when it actually stopped something.
    param(
        [Parameter(Mandatory = $true)]
        [string]$Port,

        [switch]$KeepLog,

        [switch]$IncludeOrphans
    )

    $stoppedSomething = $false
    $state = Get-SmartHomeDevEnvState -Port $Port

    if ($state) {
        Write-Host "Stopping the local dev environment (port $Port)..." -ForegroundColor Cyan

        # Reverse order: stopping the broker first would make the subscriber log a
        # connection error on its way out, for no reason.
        $records = @($state['Processes'])
        for ($i = $records.Count - 1; $i -ge 0; $i--) {
            if (Stop-SmartHomeRecordedProcess -Record $records[$i]) {
                $stoppedSomething = $true
            }
        }

        if ($state['ConfigFile']) {
            Remove-Item -Path $state['ConfigFile'] -Force -ErrorAction SilentlyContinue
        }

        foreach ($logFile in @($state['LogFiles'] | Where-Object { $_ })) {
            if ($KeepLog) {
                if (Test-Path $logFile) {
                    Write-Host ("  Kept log: {0}" -f $logFile) -ForegroundColor DarkGray
                }
            }
            else {
                Remove-Item -Path $logFile -Force -ErrorAction SilentlyContinue
            }
        }

        Clear-SmartHomeDevEnvState -Port $Port
    }

    $orphans = @(Get-SmartHomeOrphanProcess)
    if ($IncludeOrphans) {
        foreach ($orphan in $orphans) {
            Write-Host ("  Stopping orphaned {0} (PID {1})..." -f $orphan.Name, $orphan.ProcessId) -ForegroundColor Yellow
            Stop-SmartHomeProcessTree -ProcessId $orphan.ProcessId
            $stoppedSomething = $true
        }
        if ($orphans.Count -eq 0) {
            Write-Host "No orphaned SmartHome broker or subscriber processes found." -ForegroundColor DarkGray
        }
    }
    elseif ($orphans.Count -gt 0) {
        # Don't leave someone staring at "nothing to stop" while a broker this repo
        # started is demonstrably still holding the port.
        Write-Warning ("Found {0} process(es) started by this repo that no state file covers (PID {1}). Re-run with -IncludeOrphans to stop them." -f $orphans.Count, (($orphans | ForEach-Object { $_.ProcessId }) -join ', '))
    }

    return $stoppedSomething
}
