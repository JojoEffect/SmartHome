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

function Get-SmartHomeVsTool {
    # One locate-and-memoise for every Visual Studio-resident tool. MSBuild and
    # vstest.console had a copy each -- same five steps, differing only in the three
    # strings below -- and the memo dance was spelled out twice more on top of the
    # pre-declaration loop at the top of this file.
    #
    # Memoised in the global scope on purpose: vswhere costs ~440ms per call, and a
    # suite run invokes Deploy-ToDevice.ps1 once per test -- those are separate script
    # scopes but the same process, so a script-scoped cache would miss.
    param(
        [Parameter(Mandatory = $true)]
        [string]$MemoName,

        [Parameter(Mandatory = $true)]
        [string]$Requires,

        [Parameter(Mandatory = $true)]
        [string]$Find,

        # Used when vswhere is absent or finds nothing: bare name, resolved off PATH.
        [Parameter(Mandatory = $true)]
        [string]$Fallback
    )

    $memoPath = "variable:global:$MemoName"
    if ((Test-Path $memoPath) -and (Get-Variable -Name $MemoName -Scope Global -ValueOnly)) {
        return (Get-Variable -Name $MemoName -Scope Global -ValueOnly)
    }

    $resolved = $Fallback
    $vswhere = Get-VsWherePath
    if ($vswhere) {
        $found = & $vswhere -latest -requires $Requires -find $Find 2>$null | Select-Object -First 1
        if ($found) {
            $resolved = $found
        }
    }

    Set-Variable -Name $MemoName -Scope Global -Value $resolved
    return $resolved
}

function Get-MSBuildPath {
    return (Get-SmartHomeVsTool -MemoName 'SmartHomeMSBuildPath' `
                                -Requires 'Microsoft.Component.MSBuild' `
                                -Find 'MSBuild\**\Bin\MSBuild.exe' `
                                -Fallback 'msbuild')
}

function Get-VsTestPath {
    return (Get-SmartHomeVsTool -MemoName 'SmartHomeVsTestPath' `
                                -Requires 'Microsoft.VisualStudio.Component.ManagedDesktop.Core' `
                                -Find 'Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe' `
                                -Fallback 'vstest.console')
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

# Shared by every temp file this repo leaves behind, dev-environment or not (the
# deploy-state section at the bottom of this file uses it too), so one glob finds
# the lot.
$SmartHomeTempFilePrefix = 'smarthome'
$SmartHomeHomieTopic = 'homie/#'

# The address host-side mosquitto clients dial. 127.0.0.1, not 'localhost'. Measured, not
# guessed: 'localhost' resolves to ::1 first, and Start-DevEnv.ps1 binds the broker to
# 0.0.0.0 -- IPv4 only, deliberately, so a real device on the LAN can reach it. Every
# client therefore attempts IPv6, waits out the connect timeout, and only then falls back.
#
#   mosquitto_sub -h localhost   first message after 2.03s, 2.03s, 2.06s
#   mosquitto_sub -h 127.0.0.1   first message after 0.02s, 0.02s, 0.12s
#
# Two seconds on every subscriber and every publish, and worse than slow: a 2s snapshot
# window used to be spent almost entirely connecting, so it captured nearly nothing and
# the polling loops only hid that by retrying.
#
# Deliberately NOT the SMARTHOME_MQTT_BROKER setting, and not configurable. That variable
# is the address the *device* dials to reach this machine (192.168.1.238 here); pointing
# host-side clients at it would send them out over the LAN to reach a broker on the same
# box, and break the moment the DHCP lease changes. The port is genuinely shared -- both
# sides must agree on it -- the host is not.
$SmartHomeLocalBrokerHost = '127.0.0.1'

function Get-SmartHomeDevEnvPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Port,

        [Parameter(Mandatory = $true)]
        [ValidateSet('State', 'Config', 'BrokerLog', 'SubscriberLog', 'SubscriberErrorLog', 'Snapshot')]
        [string]$Kind
    )

    $suffix = switch ($Kind) {
        'State'              { "devenv-$Port.json" }
        'Config'             { "mosquitto-$Port.conf" }
        'BrokerLog'          { "mosquitto-$Port.log" }
        'SubscriberLog'      { "homie-$Port.log" }
        'SubscriberErrorLog' { "homie-$Port.err.log" }
        # Retained-store reads by Run-IntegrationTests.ps1's conformance check. Short
        # lived, and not part of a dev environment -- but it belongs in this vocabulary
        # rather than being minted at the call site.
        'Snapshot'           { "homie-snapshot-$Port.log" }
    }

    return (Join-Path ([System.IO.Path]::GetTempPath()) "$SmartHomeTempFilePrefix-$suffix")
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
    #
    # Host from $SmartHomeLocalBrokerHost, which carries the reasoning for why it is not
    # 'localhost'.
    return @('-h', $SmartHomeLocalBrokerHost, '-p', $Port, '-t', $SmartHomeHomieTopic, '-F', '%t %r %p')
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

    # -Port matters: without it Get-SmartHomeOrphanProcess falls back to matching '\d+',
    # so a teardown for one port classified another port's healthy dev-env as an orphan
    # -- warning about it, and killing it outright under -IncludeOrphans. That directly
    # contradicts the per-port state design documented at the top of this section.
    $orphans = @(Get-SmartHomeOrphanProcess -Port $Port)
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

# ── Deploy state ──────────────────────────────────────────────────────────────
# Deploy-ToDevice.ps1 pads every image with 0xFF before flashing it, because nanoff
# erases and writes only the image file's own byte length -- so a smaller app
# deployed after a larger one leaves the larger one's tail sitting unerased past
# the new image's end. That tail is not ignored: the CLR walks the WHOLE deployment
# partition looking for assembly headers, and on a header that doesn't check out it
# `continue`s to the next candidate rather than stopping (ContiguousBlockAssemblies
# in nf-interpreter's src\CLR\Startup\CLRStartup.cpp, whose stream Length is the
# full block range). So no amount of trailing blank flash terminates the scan --
# the only thing that keeps stale assemblies out is overwriting them.
#
# The invariant is therefore exactly "cover the footprint of the previous image",
# and the previous image's size is the one fact needed to pad to less than a flat
# worst case. It is recorded here, keyed by COM port.
#
# Deliberately NOT another -Kind on Get-SmartHomeDevEnvPath: that function's -Port
# is an MQTT port, and one parameter meaning two different kinds of port depending
# on -Kind is exactly the ambiguity its callers should not have to hold. This
# shares the temp directory and $SmartHomeTempFilePrefix, not the signature.
#
# StaleBytes is the contract: "the number of bytes from the deploy address that the
# next flash must overwrite for no leftover assembly data to survive it". It is
# written pessimistically -- the full padded length -- BEFORE a flash, so an
# interrupted or failed write still leaves a record covering everything nanoff may
# have put down, and tightened to the image's own length only once nanoff reports
# success, at which point everything between the image's end and the padded length
# is known to be 0xFF.
#
# Anything that flashes the device WITHOUT going through Deploy-ToDevice.ps1 --
# Visual Studio's F5 deploy, the nanoFramework test adapter -- invalidates the
# record, because it writes assemblies this script never saw. Such a path must call
# Clear-SmartHomeDeployState; the next deploy then falls back to the flat
# worst-case pad, which is what the script did unconditionally before any of this.
$SmartHomeDeployStateVersion = 1

function Get-SmartHomeDeployStatePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ComPort
    )

    # 'COM3' needs no escaping, but the port comes from local.env.ps1 and something
    # like \\.\COM10 would otherwise build a path pointing somewhere else entirely.
    # '*' survives the scrub on purpose, so Clear-SmartHomeDeployState can build the
    # all-ports glob from this one naming rule instead of restating it.
    $safePort = ($ComPort -replace '[^A-Za-z0-9._*-]', '_')
    return (Join-Path ([System.IO.Path]::GetTempPath()) "$SmartHomeTempFilePrefix-deploy-$safePort.json")
}

function Get-SmartHomeDeployState {
    # Returns the record only when every field is present and sane; $null otherwise.
    # Every rejection here costs one full-size deploy -- the behaviour this script
    # had before the record existed -- so the failure direction is the safe one, and
    # nothing about a bad record should ever reach the flashing decision.
    param(
        [Parameter(Mandatory = $true)]
        [string]$ComPort
    )

    $stateFile = Get-SmartHomeDeployStatePath -ComPort $ComPort
    if (-not (Test-Path $stateFile)) {
        return $null
    }

    try {
        $state = ConvertTo-SmartHomeHashtable -InputObject (Get-Content $stateFile -Raw | ConvertFrom-Json)
    }
    catch {
        Write-Warning "Ignoring unreadable deploy-state file: $stateFile"
        return $null
    }

    if ($null -eq $state -or -not ($state -is [System.Collections.IDictionary])) {
        return $null
    }

    if ($state['Version'] -ne $SmartHomeDeployStateVersion) {
        return $null
    }

    if ($state['ComPort'] -ne $ComPort) {
        return $null
    }

    if ([string]::IsNullOrWhiteSpace([string]$state['DeployAddress'])) {
        return $null
    }

    # Parsed rather than type-checked: ConvertFrom-Json hands back Int32, Int64 or
    # Double depending on the literal, and the caller does arithmetic with this.
    $staleBytes = 0
    if (-not [int]::TryParse([string]$state['StaleBytes'], [ref]$staleBytes) -or $staleBytes -lt 0) {
        return $null
    }
    $state['StaleBytes'] = $staleBytes

    return $state
}

function Save-SmartHomeDeployState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ComPort,

        [Parameter(Mandatory = $true)]
        [string]$DeployAddress,

        [Parameter(Mandatory = $true)]
        [int]$StaleBytes,

        # Purely for humans reading the file while working out what is on a device.
        [string]$Image = ''
    )

    # Deliberately returns nothing, same as Save-SmartHomeDevEnvState: callers only
    # care that the record landed, and a returned path would leak into their output.
    @{
        Version       = $SmartHomeDeployStateVersion
        ComPort       = $ComPort
        DeployAddress = $DeployAddress
        StaleBytes    = $StaleBytes
        Image         = $Image
        UpdatedUtc    = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json -Depth 3 |
        Set-Content -Path (Get-SmartHomeDeployStatePath -ComPort $ComPort) -Encoding utf8
}

function Clear-SmartHomeDeployState {
    # Without -ComPort, clears every port's record. That is the right default for the
    # callers that need it: the nanoFramework test adapter picks its own device when
    # nano.runsettings leaves RealHardwarePort empty, so which port it flashed is not
    # knowable here. Clearing one record too many costs one full-size deploy.
    param(
        [string]$ComPort
    )

    # A clear that quietly did not happen is the one failure this whole mechanism has
    # to avoid: it leaves a record SMALLER than what is actually on the device, and the
    # next deploy pads to it. So a missing file is fine (nothing to clear) but a removal
    # that fails is not swallowed -- callers run under $ErrorActionPreference = 'Stop'
    # and should stop rather than flash against a record they were told was gone.
    if ($ComPort) {
        $stateFile = Get-SmartHomeDeployStatePath -ComPort $ComPort
        if (Test-Path -LiteralPath $stateFile) {
            Remove-Item -LiteralPath $stateFile -Force
        }
        return
    }

    # -LiteralPath on the directory plus -Filter, not a wildcard -Path: Get-ChildItem
    # reads [ and ] in a -Path as wildcard metacharacters, so a temp directory whose
    # name contains either would match nothing at all and clear silently.
    $pattern = Split-Path (Get-SmartHomeDeployStatePath -ComPort '*') -Leaf
    Get-ChildItem -LiteralPath ([System.IO.Path]::GetTempPath()) -Filter $pattern -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}
