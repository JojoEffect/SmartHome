Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Memo slots for the expensive lookups below (vswhere, git rev-parse). They live in
# the global scope so they survive across the sub-scripts a suite run invokes in the
# same process, and they are declared here because Set-StrictMode makes *reading* an
# unset variable an error -- the memo check itself would throw on first use.
# -WhatIf:$false because a script with SupportsShouldProcess dot-sources this file and
# propagates $WhatIfPreference into it. Without the override, a -WhatIf run reports
# "WhatIf: Set variable SmartHomeMSBuildPath" as though it were an action the user
# asked about, and skips the assignment -- noise in front of the real preview.
foreach ($memo in 'SmartHomeMSBuildPath', 'SmartHomeVsTestPath', 'SmartHomeSiblingRoot', 'SmartHomeMainWorktreeRoot') {
    if (-not (Test-Path "variable:global:$memo")) {
        Set-Variable -Name $memo -Scope Global -Value $null -WhatIf:$false
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

    # -LiteralPath: this is the gate every other script enters through, and -Path reads
    # '[' and ']' as wildcard character-class syntax -- so on a checkout at
    # 'C:\repos\SmartHome [wip]' the file on disk tested as absent and every script
    # aborted claiming a config file that was right there was missing (issue #71).
    # Test-Setup.ps1 makes the same test and would otherwise report the opposite of
    # this one, which is the single thing a preflight must not do.
    if (-not (Test-Path -LiteralPath $localEnv)) {
        Write-Error @"
Missing: $localEnv
Copy the template and fill in your machine settings:
    Copy-Item -LiteralPath "$template" "$localEnv"
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

function Get-SmartHomePackagesConfig {
    # Every packages.config *this* checkout references -- the one list both
    # Restore-Packages.ps1 (what to restore into packages\) and Test-Setup.ps1 (what
    # to check is restored) work from. Shared because the two have to agree about it:
    # the exclusion below was added to the preflight and the restore kept globbing
    # everything, so a restore run from the main checkout pulled every worktree's
    # referenced packages into the main checkout's packages\ (issue #68).
    #
    # bin\ and obj\ carry build-time copies of a config, not a reference of their own.
    #
    # Linked worktrees live *inside* the main checkout (.claude\worktrees\<name>),
    # each a full copy of the source tree with its own packages.config set and its own
    # packages\ folder, so recursing into them answers for checkouts other than the one
    # asked about. Anchored at $RepoRoot rather than matched as a substring anywhere in
    # the path: these scripts usually run *from* a worktree, whose own paths all
    # contain '\.claude\worktrees\', and a bare substring test would exclude every file
    # they are supposed to see.
    #
    # Always an array, so a caller can read .Count even when nothing matched. The
    # leading comma in the return is what makes that true and is not a typo: a plain
    # `return @(...)` is unrolled on the way out, so no match reaches the caller as
    # $null (and one match as a bare FileInfo), and $null.Count throws under
    # Set-StrictMode. Wrapping the array in a one-element array survives the unroll.
    #
    # A caller that would rather skip a subtree the enumerator cannot read than abort
    # on it passes -ErrorAction SilentlyContinue, which this advanced function
    # propagates to the Get-ChildItem below.
    #
    # -LiteralPath, not -Path: -Path reads '[' and ']' as wildcard character-class
    # syntax, and both are legal in a Windows directory name, so a checkout at
    # 'C:\repos\SmartHome [wip]' matched nothing at all -- and the two callers then
    # failed differently, neither naming the cause: the restore threw on $null.Count,
    # the preflight reported "no packages.config found" on a complete checkout
    # (issue #71). $RepoRoot is always a concrete directory derived from $PSScriptRoot,
    # so wildcard semantics are never wanted here.
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    # With the trailing separator, so the anchor is the folder and not a name prefix:
    # a sibling '.claude\worktrees-something' is a different folder and stays in.
    $worktreesDir = (Join-Path $RepoRoot '.claude\worktrees') + [System.IO.Path]::DirectorySeparatorChar

    return ,@(Get-ChildItem -LiteralPath $RepoRoot -Filter 'packages.config' -Recurse -File |
        Where-Object {
            $_.FullName -notmatch '\\(bin|obj)\\' -and
            -not $_.FullName.StartsWith($worktreesDir, [StringComparison]::OrdinalIgnoreCase)
        })
}

function Get-SmartHomeReferencedPackage {
    # Every <package> this checkout references, once each, as a record the callers can
    # act on: Id, Version, the packages\ folder Name NuGet extracts it to, and the Path
    # that folder would have. Four things read this list and they have to agree about
    # what a reference *is*:
    #
    #   Restore-Packages.ps1  what to copy in from the local NuGet cache;
    #   Restore-Packages.ps1  and, by complement, which packages\ folders nothing
    #                         references any more (-Prune);
    #   Test-Setup.ps1        what to check is restored;
    #   Get-NanoFrameworkTestAdapterDir below, which needs one specific version.
    #
    # The restore and the preflight computed this separately until issue #79, differing
    # only in the XML access -- and that difference was itself a bug (issue #78): the
    # property form $xml.packages.package THROWS under Set-StrictMode -Version Latest on
    # a packages.config whose <packages> has no <package> children, because the property
    # does not exist on that object. The preflight had already been given XPath for
    # exactly that reason; the restore still aborted, without naming the file. SelectNodes
    # returns an empty list instead, so this reads every config the same way.
    #
    # GetAttribute rather than $package.id for the same class of reason one level down: a
    # <package> missing either attribute is malformed, and under Set-StrictMode reading it
    # as a property throws where GetAttribute returns ''. Warned about and skipped rather
    # than thrown on -- one hand-mangled entry should not cost the other 28 their restore
    # -- but never silently, because a skipped reference is a package that then looks
    # restored to everything downstream.
    #
    # Distinct by folder name: this repository's 14 packages.config files carry 130
    # references to 29 distinct package versions, and every caller wants the set. The
    # restore's "already present" count is a count of packages, not of mentions.
    #
    # Always an array, for the reason spelled out on Get-SmartHomePackagesConfig, and
    # -ErrorAction reaches that function's enumeration through the scope chain.
    #
    # Assign the result before piping it. The leading comma that makes .Count readable on
    # an empty result also survives into a pipeline: `Get-SmartHomeReferencedPackage ... |
    # Where-Object { $_.Id -eq 'x' }` hands the scriptblock the whole array as ONE item,
    # where $_.Id is member enumeration over every record -- truthy whenever any of them
    # matches, so the filter passes everything through and reads as if it had worked. Both
    # callers assign first; the test file has a case for the empty-result half of this.
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $packagesDir = Join-Path $RepoRoot 'packages'
    $seen = @{}
    $records = New-Object System.Collections.Generic.List[psobject]

    foreach ($config in (Get-SmartHomePackagesConfig -RepoRoot $RepoRoot)) {
        [xml]$xml = Get-Content -LiteralPath $config.FullName

        foreach ($package in $xml.SelectNodes('/packages/package')) {
            $id = $package.GetAttribute('id')
            $version = $package.GetAttribute('version')

            if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($version)) {
                Write-Warning "Skipping a <package> with no id or no version in $($config.FullName)."
                continue
            }

            $name = "$id.$version"
            if ($seen.ContainsKey($name)) { continue }
            $seen[$name] = $true

            $records.Add([pscustomobject]@{
                Id      = $id
                Version = $version
                Name    = $name
                Path    = (Join-Path $packagesDir $name)
            })
        }
    }

    return ,@($records)
}

function Get-SmartHomeUnreferencedPackageDir {
    # The folders in packages\ that no reference accounts for -- what Restore-Packages.ps1
    # reports, and removes under -Prune. The restore only ever added to that folder and
    # nothing else pruned it, so every version bump left the previous version behind:
    # measured on the main checkout on 2026-09-01, 61 folders against 29 references
    # (issue #79).
    #
    # Takes the referenced records rather than a repo root, for two reasons. The caller
    # already has them -- recomputing would walk every packages.config a second time to
    # reach the same answer -- and a function whose destructive half depends only on its
    # arguments can be proved at a desk, which the block this replaced could not.
    #
    # Directories only. NuGet leaves loose files in packages\ (a .nupkg, a lock file) and
    # they are nobody's stale package version; a prune that swept them up would be doing
    # something other than what it says.
    #
    # Name matching is case-insensitive, which is what a hashtable gives by default and
    # what NTFS gives anyway: the folder NuGet extracts and the id in packages.config
    # differ in case often enough (nanoFramework.M2Mqtt vs nanoframework.m2mqtt in the
    # cache) that a case-sensitive complement would call a restored package stale.
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagesDir,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        $ReferencedPackage
    )

    if (-not (Test-Path -LiteralPath $PackagesDir)) {
        return ,@()
    }

    $referencedNames = @{}
    foreach ($package in $ReferencedPackage) { $referencedNames[$package.Name] = $true }

    return ,@(Get-ChildItem -LiteralPath $PackagesDir -Directory |
        Where-Object { -not $referencedNames.ContainsKey($_.Name) })
}

function Get-NanoFrameworkTestAdapterDir {
    # The adapter vstest.console loads to run SmartHome.UnitTests, resolved from the
    # version this checkout *references* rather than from whatever is on disk.
    #
    # It used to take the highest-sorting nanoFramework.TestAdapter.dll anywhere under
    # packages\, which is a text sort and therefore not a version order: with 3.0.9 and
    # 3.0.80 both restored, 3.0.9 won. Nothing prunes packages\, so a version bump leaves
    # the old folder behind and the run silently uses an adapter this checkout does not
    # reference (issue #79). CLAUDE.md records what that class of mismatch costs -- the
    # NFUnitTest rename produced a green vstest run that executed nothing and went
    # unnoticed for three commits -- so the failure mode to avoid is "picked something",
    # not "picked nothing".
    #
    # $null on every path it cannot resolve, because both callers already treat that as
    # "cannot run the unit tests" and say so. The warning is what distinguishes the three
    # reasons; Test-Setup.ps1 silences it and builds its own row from the same list.
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    # Assigned before it is filtered, never piped straight in -- see the note on
    # Get-SmartHomeReferencedPackage for what piping it does to a Where-Object.
    $referenced = Get-SmartHomeReferencedPackage -RepoRoot $RepoRoot
    $testFramework = @($referenced | Where-Object { $_.Id -eq 'nanoFramework.TestFramework' })

    if ($testFramework.Count -eq 0) {
        Write-Warning "No packages.config in $RepoRoot references nanoFramework.TestFramework, so there is no adapter version to resolve."
        return $null
    }

    if ($testFramework.Count -gt 1) {
        Write-Warning ("This checkout references {0} nanoFramework.TestFramework versions ({1}). Pin one before running the unit tests -- picking between them is what issue #79 was about." -f `
            $testFramework.Count, (@($testFramework | ForEach-Object { $_.Version }) -join ', '))
        return $null
    }

    # -LiteralPath for the same reason as Get-SmartHomePackagesConfig above: a checkout
    # path containing '[' or ']' finds nothing under -Path, and this one reports that as
    # "adapter not restored". SilentlyContinue covers the folder simply not being there,
    # which is the ordinary unrestored case and not an error worth two messages.
    #
    # Sorted before the pick so a package that ships the adapter under more than one
    # target framework resolves the same way twice. Both are the referenced version, so
    # unlike the sort this replaced, which of them wins is not a correctness question.
    $adapterDll = Get-ChildItem -LiteralPath $testFramework[0].Path -Recurse -Filter 'nanoFramework.TestAdapter.dll' -ErrorAction SilentlyContinue |
        Sort-Object FullName | Select-Object -First 1

    if (-not $adapterDll) {
        Write-Warning ("nanoFramework.TestFramework {0} is referenced but its adapter is not in {1}. Run .\scripts\Restore-Packages.ps1." -f `
            $testFramework[0].Version, $testFramework[0].Path)
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

function Get-SmartHomeMainWorktreeRoot {
    # The root of the MAIN working tree, whether this checkout is that tree or a
    # linked worktree under it. $null means only "git could not answer" -- no git on
    # PATH, not a repository, an exported source tree -- never "this is the main
    # one"; use Test-SmartHomeLinkedWorktree for that question.
    #
    # `git rev-parse --git-common-dir` reports the main repository's .git, which every
    # linked worktree shares; its parent is the main working tree. That holds wherever
    # the worktree sits, which a relative hop like ..\..\.. does not -- that one is
    # correct only for a worktree exactly three levels down.
    #
    # Memoised in the global scope for the same reason as the tool lookups above: a
    # suite run invokes sub-scripts in this one process, and each would otherwise pay
    # for its own `git rev-parse`. A $null is deliberately not memoised -- it costs a
    # re-probe per call in the one case where the answer is unusable anyway.
    if ($global:SmartHomeMainWorktreeRoot) {
        return $global:SmartHomeMainWorktreeRoot
    }

    $repoRoot = Get-SmartHomeRepoRoot

    $commonDir = $null
    $gitExit = 1
    try {
        # --path-format=absolute would save the rooting dance below, but that flag is
        # git 2.31+. Plain --git-common-dir answers on every version, relatively.
        $commonDir = git -C $repoRoot rev-parse --git-common-dir 2>$null

        # Read inside the try, not after it. Two different ways this line can throw,
        # and both have to land in the catch for the function to keep its "$null when
        # git can't answer" contract:
        #   - git is not on PATH at all -- $ErrorActionPreference = 'Stop' makes the
        #     CommandNotFoundException terminating;
        #   - `git` resolves to a PowerShell function or alias rather than the
        #     executable (profile wrappers, corporate shims), so no native command
        #     ran and $LASTEXITCODE was never set -- which Set-StrictMode -Version
        #     Latest turns into a terminating "variable cannot be retrieved" error.
        # Read outside the try, that second one escapes the function and takes down
        # Test-Setup.ps1, whose whole point is to report every gap rather than abort
        # on the first.
        $gitExit = $LASTEXITCODE
    }
    catch {
        return $null
    }

    if ($gitExit -ne 0 -or [string]::IsNullOrWhiteSpace($commonDir)) {
        return $null
    }

    if (-not [System.IO.Path]::IsPathRooted($commonDir)) {
        $commonDir = Join-Path $repoRoot $commonDir
    }

    # -LiteralPath: git hands back this checkout's own path, brackets and all, and
    # -Path would read them as wildcards and resolve nothing -- which this function
    # reports as "git cannot answer for this checkout", so a linked worktree at a
    # bracketed path would look like the main one.
    $resolved = Resolve-Path -LiteralPath $commonDir -ErrorAction SilentlyContinue
    if (-not $resolved) {
        return $null
    }

    # <main repo>\.git  ->  <main repo>
    $global:SmartHomeMainWorktreeRoot = Split-Path $resolved.Path -Parent
    return $global:SmartHomeMainWorktreeRoot
}

function Test-SmartHomeLinkedWorktree {
    # True only when this checkout is provably a linked worktree. The main working
    # tree is false, and so is a checkout git cannot answer for -- a caller that
    # seeds a worktree from its main checkout must not act on a guess.
    $mainRoot = Get-SmartHomeMainWorktreeRoot
    if (-not $mainRoot) {
        return $false
    }

    return ($mainRoot.TrimEnd('\', '/') -ne (Get-SmartHomeRepoRoot).TrimEnd('\', '/'))
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
    # the siblings the main checkout already has. Fall back to the plain parent only
    # when git can't answer at all.
    $mainRoot = Get-SmartHomeMainWorktreeRoot
    if (-not $mainRoot) {
        $mainRoot = Get-SmartHomeRepoRoot
    }

    $global:SmartHomeSiblingRoot = Split-Path $mainRoot -Parent
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

    # -LiteralPath on every probe of $targetPath below: the sibling root is derived from
    # the checkout, and -Path reads '[' and ']' as wildcard syntax. Under -Path a sibling
    # repo that is cloned and current tests as absent, so the pin guard is skipped and
    # `git clone` fails on a non-empty destination -- twice, because the cleanup probe is
    # wrong the same way -- and the sync aborts on the first repository while
    # Test-Setup.ps1 reports the same siblings present (issue #71).
    if ((Test-Path -LiteralPath $targetPath) -and -not $Force) {
        $currentRef = git -C $targetPath rev-parse --abbrev-ref HEAD 2>$null
        if ($currentRef -eq 'HEAD') {
            $pinnedCommit = git -C $targetPath rev-parse --short HEAD 2>$null
            Write-Host "Skipping $Repository -- pinned to a specific commit/tag ($pinnedCommit, detached HEAD)." -ForegroundColor Yellow
            Write-Host "  Pass -Force to this script to reset it back to '$Branch'." -ForegroundColor DarkGray
            return
        }
    }

    if (-not (Test-Path -LiteralPath $targetPath)) {
        Write-Host "Cloning $Repository..." -ForegroundColor Cyan
        git clone --branch $Branch --single-branch $repoUrl $targetPath
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Branch '$Branch' not found for $Repository. Falling back to default branch."
            if (Test-Path -LiteralPath $targetPath) {
                Remove-Item -LiteralPath $targetPath -Recurse -Force
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

    # Both comparisons inside the try, and that is not tidiness. Get-Process hands back an
    # object whose name and start time are fetched lazily, so a process that exits in the
    # gap between the lookup above and either read throws "Process has exited, so the
    # requested information is not available" -- terminating under the callers'
    # $ErrorActionPreference = 'Stop'. That gap is not hypothetical for a caller polling
    # this in a loop precisely while the process is expected to die (Start-HomieCapture's
    # connect wait), and a throw there escapes before that caller can hand back what it
    # started, orphaning it. A process that vanished mid-check is exactly the "not running"
    # this function reports; it must not be an error.
    try {
        if ($Record['Name'] -and $process.ProcessName -ne $Record['Name']) {
            return $null
        }

        if ($Record['StartTime']) {
            $recorded = [datetime]::Parse(
                $Record['StartTime'],
                [cultureinfo]::InvariantCulture,
                [System.Globalization.DateTimeStyles]::RoundtripKind)

            if ([math]::Abs(($process.StartTime - $recorded).TotalSeconds) -gt 2) {
                return $null
            }
        }
    }
    catch {
        return $null
    }

    return $process
}

function Stop-SmartHomeProcessTree {
    # taskkill /T takes the children too. Stop-Process would kill only the
    # launcher and leave the real process running with no way back to it.
    #
    # The redirect is cmd's, not PowerShell's, and that is the whole point. For a pid that
    # is already gone taskkill writes "FEHLER: Der Prozess ... wurde nicht gefunden" to
    # stderr, and in Windows PowerShell 5.1 redirecting a native command's stderr is what
    # wraps it in a NativeCommandError -- so the `*> $null` that used to be here did not
    # suppress a failure, it manufactured one, terminating under the callers'
    # $ErrorActionPreference = 'Stop'. Stop-SmartHomeRecordedProcess checks liveness first,
    # but that is check-then-act and a process can exit in the gap. Letting cmd swallow
    # both streams keeps this silent AND non-throwing.
    param(
        [Parameter(Mandatory = $true)]
        [int]$ProcessId
    )

    & cmd.exe /c "taskkill /PID $ProcessId /T /F >nul 2>&1"
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
