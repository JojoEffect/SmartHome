<#
.SYNOPSIS
    Report which git worktrees and branches, local and remote, are already merged into
    main, and optionally remove them in one batch.

.DESCRIPTION
    Answers one question per worktree and per branch: is its work already in the base
    branch? Everything else is kept.

    A branch is a DELETE candidate when its tip is an ancestor of the base branch --
    `git branch --merged`, the same test git itself uses to decide whether to allow
    `git branch -d`. That is the only test that proves nothing is lost: an ancestor
    contributes no commit the base branch does not already have.

    GitHub squash- and rebase-merges break that test, because they rewrite the
    commits and leave the branch tip outside the base branch's history. When `gh` is
    available and authenticated, a branch whose pull request GitHub reports as MERGED
    is therefore also a candidate -- reported in its own group, because deleting it
    needs `git branch -D` (git will refuse `-d`) and the evidence is GitHub's word
    rather than local history.

    Never a candidate, whatever the merge state:
      - the base branch itself
      - any branch pinned by a worktree that is staying
      - any branch with an OPEN pull request
      - anything named in -Protect

    Worktrees are always analysed and reported; -Worktrees is what allows them to be
    removed. A worktree is removed only when it is clean, unlocked, not the main
    worktree, not the one this script is running in, and its HEAD is already in the
    base branch. A worktree with uncommitted or untracked changes is ALWAYS kept and
    there is deliberately no flag to override that: uncommitted work is the one thing
    neither the reflog nor the remote can give back.

    With -Worktrees -Delete the two passes chain: worktrees are removed first, then
    the branches they were pinning are deleted in the same run. The report reflects
    that up front -- a branch freed by a pending removal is listed as a delete
    candidate, not as pinned.

    Read-only unless -Delete is passed. It touches no hardware, opens no port, and
    deliberately does not read scripts\local.env.ps1 -- so it works in a fresh
    worktree with no setup.

.PARAMETER Remote
    Remote to measure against and to delete remote branches from. Default 'origin'.

.PARAMETER BaseBranch
    Branch that merged work lands on. Default 'main'. Measured as
    <Remote>/<BaseBranch> when that exists, so the report reflects what has actually
    been pushed rather than a stale local copy.

.PARAMETER Scope
    Which side to analyse: Local, Remote, or Both (default). Worktrees are local, so
    -Scope Remote skips them.

.PARAMETER Protect
    Extra branch names to keep regardless of merge state. A worktree pinning one is
    kept too.

.PARAMETER Worktrees
    Allow merged worktrees to be removed, and their branches to be deleted in the
    same run. Without it, worktrees are still reported but nothing about them is
    touched and the branches they pin stay pinned.

.PARAMETER Delete
    Actually delete. Without it, report only, and the command that would do the work
    is printed instead.

.PARAMETER NoFetch
    Skip the `git fetch --prune` that would otherwise refresh the remote view first.
    Use offline, or when a fetch has just been done.

.PARAMETER NoGitHub
    Do not consult `gh`. Loses squash-merge detection and open-pull-request
    protection; ancestry-merged branches and worktrees are still found.

.PARAMETER Json
    Emit the full classification as JSON on stdout instead of the human report.

.EXAMPLE
    .\scripts\Clean-GitBranches.ps1
    Report only. Nothing is removed or deleted.

.EXAMPLE
    .\scripts\Clean-GitBranches.ps1 -Delete
    Delete the merged branches. Worktrees are reported but left alone, so the
    branches they pin survive.

.EXAMPLE
    .\scripts\Clean-GitBranches.ps1 -Worktrees -Delete
    Remove the merged worktrees, then delete the merged branches -- including the
    ones the removed worktrees were pinning.

.EXAMPLE
    .\scripts\Clean-GitBranches.ps1 -Worktrees -Delete -WhatIf
    Show exactly which removals and deletions would run.
#>

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$Remote = 'origin',

    [string]$BaseBranch = 'main',

    [ValidateSet('Local', 'Remote', 'Both')]
    [string]$Scope = 'Both',

    [string[]]$Protect = @(),

    [switch]$Worktrees,

    [switch]$Delete,

    [switch]$NoFetch,

    [switch]$NoGitHub,

    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Common.ps1')

# Deliberately no Import-SmartHomeLocalEnv: this script needs no COM port, no broker
# and no restored packages, so it must not inherit the one thing that would make it
# fail in a fresh worktree.

$repoRoot = Get-SmartHomeRepoRoot

function Invoke-Git {
    # Never pipe a native tool through 2>&1 in Windows PowerShell 5.1: the redirect
    # wraps ordinary stderr in a NativeCommandError and reports a healthy call as a
    # failure. Let stderr reach the console and judge by the exit code.
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [string]$WorkingDirectory = ''
    )

    $where = if ($WorkingDirectory) { $WorkingDirectory } else { $repoRoot }
    $output = & git -C $where @Arguments
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Lines    = @($output | Where-Object { $null -ne $_ -and $_ -ne '' })
    }
}

function Assert-Git {
    param([Parameter(Mandatory = $true)][string[]]$Arguments, [string]$What = '')

    $result = Invoke-Git -Arguments $Arguments
    if ($result.ExitCode -ne 0) {
        $context = if ($What) { " while $What" } else { '' }
        Write-Error "git $($Arguments -join ' ') failed with exit code $($result.ExitCode)$context."
        exit 1
    }
    return $result.Lines
}

function ConvertTo-ComparablePath {
    # git reports worktree paths with forward slashes; PowerShell hands out
    # backslashes. Normalise both before comparing, or the current worktree fails to
    # recognise itself and offers to delete the directory it is running in.
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    return ($Path -replace '/', '\').TrimEnd('\').ToLowerInvariant()
}

$insideRepo = Invoke-Git -Arguments @('rev-parse', '--is-inside-work-tree')
if ($insideRepo.ExitCode -ne 0) {
    Write-Error "Not a git repository: $repoRoot"
    exit 1
}

# ---------------------------------------------------------------- remote view ----

if (-not $NoFetch) {
    Write-Host "Fetching $Remote (pruning deleted remote branches)..." -ForegroundColor DarkGray
    $fetch = Invoke-Git -Arguments @('fetch', $Remote, '--prune')
    if ($fetch.ExitCode -ne 0) {
        Write-Warning "git fetch $Remote --prune failed (exit $($fetch.ExitCode)). Analysing the stale remote view; fix connectivity, or pass -NoFetch if that is intended."
    }
}

$baseRef = "$Remote/$BaseBranch"
$baseCheck = Invoke-Git -Arguments @('rev-parse', '--verify', '--quiet', "refs/remotes/$baseRef")
if ($baseCheck.ExitCode -ne 0) {
    $localBase = Invoke-Git -Arguments @('rev-parse', '--verify', '--quiet', "refs/heads/$BaseBranch")
    if ($localBase.ExitCode -ne 0) {
        Write-Error "Neither '$baseRef' nor local '$BaseBranch' exists. Pass -BaseBranch with the branch merged work lands on."
        exit 1
    }
    Write-Warning "'$baseRef' does not exist; measuring against the local '$BaseBranch' instead. A local base can lag what is actually on the remote."
    $baseRef = $BaseBranch
}

# @(...) around the call, not just the [0]: PowerShell unrolls a single-element array
# on `return`, so indexing the bare result would take the first *character* of the sha.
$baseSha = @(Assert-Git -Arguments @('rev-parse', '--short', $baseRef) -What 'resolving the base branch')[0]

$protectedNames = @{}
foreach ($name in @($BaseBranch) + @($Protect)) {
    if (-not [string]::IsNullOrWhiteSpace($name)) { $protectedNames[$name] = $true }
}

# ---------------------------------------------------------------- GitHub ----

# Two things only GitHub knows: which pull requests are still open (a branch to keep
# even when its work happens to be in main), and which were squash- or rebase-merged
# (work that is in main under rewritten commits, which ancestry cannot see).
$prByBranch = @{}
$githubStatus = 'skipped (-NoGitHub)'
if (-not $NoGitHub) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        $githubStatus = 'gh is not on PATH'
    }
    else {
        $remoteUrl = Invoke-Git -Arguments @('remote', 'get-url', $Remote)
        if ($remoteUrl.ExitCode -ne 0 -or @($remoteUrl.Lines).Count -eq 0) {
            $githubStatus = "remote '$Remote' has no URL"
        }
        else {
            $prJson = & gh pr list --repo $remoteUrl.Lines[0] --state all --limit 300 --json number,state,headRefName,isCrossRepository
            if ($LASTEXITCODE -ne 0) {
                $githubStatus = 'the gh call failed (not authenticated? run: gh auth login)'
            }
            else {
                $githubStatus = 'ok'
                # ForEach-Object, not just @(): ConvertFrom-Json in Windows PowerShell
                # 5.1 emits a JSON array as ONE object, so @() wraps it instead of
                # unrolling it. Without the extra pipeline the loop runs once with the
                # whole array bound to $pr, and `$pr.isCrossRepository` -- an array of
                # booleans -- is truthy, silently skipping every pull request.
                foreach ($pr in @($prJson | ConvertFrom-Json | ForEach-Object { $_ })) {
                    if ($pr.isCrossRepository) { continue }   # a fork's branch, not ours to delete
                    $existing = if ($prByBranch.ContainsKey($pr.headRefName)) { $prByBranch[$pr.headRefName] } else { $null }
                    # An OPEN pull request outranks any closed or merged one on the
                    # same branch: a reused branch name must protect the branch, not
                    # free it.
                    if ($null -eq $existing -or $pr.state -eq 'OPEN' -or ($existing.state -ne 'OPEN' -and $pr.number -gt $existing.number)) {
                        $prByBranch[$pr.headRefName] = $pr
                    }
                }
            }
        }
    }
}

function Get-PullRequest {
    param([string]$BranchName)

    if ($BranchName -and $prByBranch.ContainsKey($BranchName)) { return $prByBranch[$BranchName] }
    return $null
}

# -------------------------------------------------------------- merge sets ----

$mergedLocal = @{}
foreach ($name in (Assert-Git -Arguments @('branch', '--merged', $baseRef, '--format=%(refname:short)') -What 'listing merged local branches')) {
    $mergedLocal[$name] = $true
}

$mergedRemote = @{}
foreach ($name in (Assert-Git -Arguments @('branch', '--remotes', '--merged', $baseRef, '--format=%(refname:short)') -What 'listing merged remote branches')) {
    $mergedRemote[$name] = $true
}

function Get-AheadCount {
    param([string]$Ref)

    $result = Invoke-Git -Arguments @('rev-list', '--count', "$baseRef..$Ref")
    if ($result.ExitCode -ne 0 -or @($result.Lines).Count -eq 0) { return -1 }
    return [int]$result.Lines[0]
}

function Test-CommitMerged {
    param([string]$Commit)

    $result = Invoke-Git -Arguments @('merge-base', '--is-ancestor', $Commit, $baseRef)
    return ($result.ExitCode -eq 0)
}

function New-Entry {
    param(
        [string]$Name,
        [string]$Side,
        [string]$Action,
        [string]$Reason,
        [string]$Method = 'ancestry',
        [int]$Ahead = 0,
        [string]$Upstream = '',
        [string]$Path = '',
        [string]$Branch = ''
    )
    return [pscustomobject]@{
        Name     = $Name
        Side     = $Side
        Action   = $Action
        Reason   = $Reason
        Method   = $Method
        Ahead    = $Ahead
        Upstream = $Upstream
        Path     = $Path
        Branch   = $Branch
    }
}

# --------------------------------------------------------------- worktrees ----

function Get-WorktreeEntries {
    <#
        Every state a worktree can be in, and what happens to it:

          directory gone      -> prune  (the admin record is all that is left)
          main worktree       -> keep   (git refuses to remove it, and it is the repo)
          this worktree       -> keep   (removing it deletes the running session's cwd)
          locked              -> keep   (an explicit do-not-touch marker, with a reason)
          dirty               -> keep   (uncommitted work; no flag overrides this)
          pins a protected or open-pull-request branch -> keep
          HEAD merged into base, or its branch squash-merged -> remove
          anything else       -> keep   (carries commits the base branch does not have)
    #>

    $current = ConvertTo-ComparablePath $repoRoot

    # Parse the porcelain output into records first. A blank line terminates each
    # record, but Invoke-Git strips blanks -- so a record is closed by the next
    # 'worktree' line, and the last one by the end of the list.
    $records = @()
    $record = $null
    foreach ($line in (Assert-Git -Arguments @('worktree', 'list', '--porcelain') -What 'listing worktrees')) {
        if ($line -match '^worktree (.+)$') {
            if ($null -ne $record) { $records += $record }
            $record = [pscustomobject]@{
                Path = $Matches[1]; Head = ''; Branch = ''
                Locked = $false; LockReason = ''; Prunable = $false
                IsMain = (@($records).Count -eq 0)   # git always lists the main worktree first
            }
        }
        elseif ($null -eq $record) { continue }
        elseif ($line -match '^HEAD (.+)$') { $record.Head = $Matches[1] }
        elseif ($line -match '^branch refs/heads/(.+)$') { $record.Branch = $Matches[1] }
        elseif ($line -match '^locked\s*(.*)$') { $record.Locked = $true; $record.LockReason = $Matches[1].Trim() }
        elseif ($line -match '^prunable') { $record.Prunable = $true }
    }
    if ($null -ne $record) { $records += $record }

    $entries = @()
    foreach ($record in $records) {
        $path = $record.Path
        $head = $record.Head
        $branch = $record.Branch
        $isMain = $record.IsMain

        $exists = Test-Path -LiteralPath $path
        $comparable = ConvertTo-ComparablePath $path
        $name = if ($isMain) { '<main worktree>' } else { Split-Path $path -Leaf }
        $pr = Get-PullRequest -BranchName $branch
        $dirty = 0

        if ($exists) {
            $status = Invoke-Git -Arguments @('status', '--porcelain') -WorkingDirectory $path
            if ($status.ExitCode -eq 0) { $dirty = @($status.Lines).Count }
        }

        $entries +=
        if (-not $exists -or $record.Prunable) {
            New-Entry -Name $name -Side 'worktree' -Action 'prune' -Method 'prunable' `
                -Reason 'directory is gone; only the admin record remains' -Path $path -Branch $branch
        }
        elseif ($isMain) {
            New-Entry -Name $name -Side 'worktree' -Action 'keep' -Method 'protected' `
                -Reason 'main worktree' -Path $path -Branch $branch
        }
        elseif ($comparable -eq $current) {
            New-Entry -Name $name -Side 'worktree' -Action 'keep' -Method 'current' `
                -Reason 'this script is running in it' -Path $path -Branch $branch
        }
        elseif ($record.Locked) {
            $why = if ($record.LockReason) { "locked: $($record.LockReason)" } else { 'locked' }
            New-Entry -Name $name -Side 'worktree' -Action 'keep' -Method 'locked' `
                -Reason $why -Path $path -Branch $branch
        }
        elseif ($dirty -gt 0) {
            New-Entry -Name $name -Side 'worktree' -Action 'keep' -Method 'dirty' `
                -Reason "$dirty uncommitted change(s) - never removed, and no flag overrides this" -Path $path -Branch $branch
        }
        elseif ($branch -and $protectedNames.ContainsKey($branch)) {
            New-Entry -Name $name -Side 'worktree' -Action 'keep' -Method 'protected' `
                -Reason "pins the protected branch '$branch'" -Path $path -Branch $branch
        }
        elseif ($null -ne $pr -and $pr.state -eq 'OPEN') {
            New-Entry -Name $name -Side 'worktree' -Action 'keep' -Method 'open-pr' `
                -Reason "pins '$branch', open pull request #$($pr.number)" -Path $path -Branch $branch
        }
        elseif (Test-CommitMerged -Commit $head) {
            $what = if ($branch) { "pins '$branch', merged into $baseRef" } else { "detached at $($head.Substring(0, 7)), merged into $baseRef" }
            New-Entry -Name $name -Side 'worktree' -Action 'remove' -Method 'ancestry' `
                -Reason $what -Path $path -Branch $branch
        }
        elseif ($null -ne $pr -and $pr.state -eq 'MERGED') {
            New-Entry -Name $name -Side 'worktree' -Action 'remove' -Method 'squash' `
                -Reason "pins '$branch', pull request #$($pr.number) merged (squash/rebase rewrote the commits)" `
                -Ahead (Get-AheadCount -Ref $head) -Path $path -Branch $branch
        }
        else {
            $what = if ($branch) { "pins '$branch', not merged into $baseRef" } else { "detached at $($head.Substring(0, 7)), not merged into $baseRef" }
            New-Entry -Name $name -Side 'worktree' -Action 'keep' -Method 'unmerged' `
                -Reason $what -Ahead (Get-AheadCount -Ref $head) -Path $path -Branch $branch
        }
    }

    return $entries
}

$worktreeEntries = @()
if ($Scope -ne 'Remote') {
    $worktreeEntries = @(Get-WorktreeEntries)
}

$worktreeRemove = @($worktreeEntries | Where-Object { $_.Action -eq 'remove' })
$worktreePrune = @($worktreeEntries | Where-Object { $_.Action -eq 'prune' })
$worktreeKeep = @($worktreeEntries | Where-Object { $_.Action -eq 'keep' })

# Branches pinned by a worktree that is STAYING. When -Worktrees is passed, the
# branches of the worktrees about to be removed are not pinned any more, so they are
# classified as the candidates they will be by the time the delete pass runs.
$pinnedBranches = @{}
$freedByWorktree = @{}
foreach ($entry in $worktreeEntries) {
    if (-not $entry.Branch) { continue }
    if ($Worktrees -and $entry.Action -eq 'remove') {
        $freedByWorktree[$entry.Branch] = $entry.Name
    }
    else {
        $pinnedBranches[$entry.Branch] = $entry
    }
}

# --------------------------------------------------------- branch classify ----

$entries = @()

if ($Scope -ne 'Remote') {
    $refs = Assert-Git -Arguments @('for-each-ref', 'refs/heads/', '--format=%(refname:short)%09%(upstream:short)%09%(upstream:track)') -What 'listing local branches'
    foreach ($line in $refs) {
        $parts = $line -split "`t"
        $name = $parts[0]
        $upstream = if ($parts.Count -gt 1) { $parts[1] } else { '' }
        $track = if ($parts.Count -gt 2) { $parts[2] } else { '' }
        $gone = $track -match 'gone'
        $pr = Get-PullRequest -BranchName $name
        $freed = if ($freedByWorktree.ContainsKey($name)) { "; freed by removing worktree $($freedByWorktree[$name])" } else { '' }

        if ($protectedNames.ContainsKey($name)) {
            $entries += New-Entry -Name $name -Side 'local' -Action 'keep' -Reason 'protected' -Method 'protected' -Upstream $upstream
        }
        elseif ($pinnedBranches.ContainsKey($name)) {
            $holder = $pinnedBranches[$name]
            $where = if ($holder.Method -eq 'current') { 'checked out here' } else { "pinned by worktree $($holder.Name) ($($holder.Reason))" }
            $entries += New-Entry -Name $name -Side 'local' -Action 'keep' -Reason $where -Method 'worktree' -Upstream $upstream
        }
        elseif ($null -ne $pr -and $pr.state -eq 'OPEN') {
            $entries += New-Entry -Name $name -Side 'local' -Action 'keep' -Reason "open pull request #$($pr.number)" -Method 'open-pr' -Upstream $upstream
        }
        elseif ($mergedLocal.ContainsKey($name)) {
            $suffix = if ($gone) { '; remote branch already deleted' } else { '' }
            $entries += New-Entry -Name $name -Side 'local' -Action 'delete' -Reason "merged into $baseRef$suffix$freed" -Method 'ancestry' -Upstream $upstream
        }
        elseif ($null -ne $pr -and $pr.state -eq 'MERGED') {
            $entries += New-Entry -Name $name -Side 'local' -Action 'delete' -Reason "pull request #$($pr.number) merged (squash/rebase rewrote the commits)$freed" -Method 'squash' -Ahead (Get-AheadCount -Ref $name) -Upstream $upstream
        }
        else {
            $reason = if ($null -ne $pr) { "not merged into $baseRef (pull request #$($pr.number) is $($pr.state))" } else { "not merged into $baseRef" }
            $entries += New-Entry -Name $name -Side 'local' -Action 'keep' -Reason $reason -Method 'unmerged' -Ahead (Get-AheadCount -Ref $name) -Upstream $upstream
        }
    }
}

if ($Scope -ne 'Local') {
    $remoteHead = ''
    $symref = Invoke-Git -Arguments @('symbolic-ref', '--quiet', '--short', "refs/remotes/$Remote/HEAD")
    if ($symref.ExitCode -eq 0 -and @($symref.Lines).Count -gt 0) { $remoteHead = $symref.Lines[0] }

    # %(refname), not %(refname:short): git shortens refs/remotes/<remote>/HEAD to the
    # bare remote name, which no longer carries the prefix the branch name is cut from.
    $prefix = "refs/remotes/$Remote/"
    foreach ($refname in (Assert-Git -Arguments @('for-each-ref', $prefix, '--format=%(refname)') -What 'listing remote branches')) {
        if (-not $refname.StartsWith($prefix)) { continue }
        $short = $refname.Substring($prefix.Length)
        if ($short -eq 'HEAD') { continue }

        $full = "$Remote/$short"
        $pr = Get-PullRequest -BranchName $short

        # The remote's own HEAD target is protected even when it is not the base
        # branch: deleting it leaves every fresh clone without a default branch.
        if ($protectedNames.ContainsKey($short) -or $full -eq $baseRef -or $full -eq $remoteHead) {
            $entries += New-Entry -Name $short -Side 'remote' -Action 'keep' -Reason 'protected' -Method 'protected'
        }
        elseif ($null -ne $pr -and $pr.state -eq 'OPEN') {
            $entries += New-Entry -Name $short -Side 'remote' -Action 'keep' -Reason "open pull request #$($pr.number)" -Method 'open-pr'
        }
        elseif ($mergedRemote.ContainsKey($full)) {
            $entries += New-Entry -Name $short -Side 'remote' -Action 'delete' -Reason "merged into $baseRef" -Method 'ancestry'
        }
        else {
            $reason = if ($null -ne $pr) { "not merged into $baseRef (pull request #$($pr.number) is $($pr.state))" } else { "not merged into $baseRef" }
            $entries += New-Entry -Name $short -Side 'remote' -Action 'keep' -Reason $reason -Method 'unmerged' -Ahead (Get-AheadCount -Ref $full)
        }
    }
}

# ---------------------------------------------------------------- report ----

$localDeleteAncestry = @($entries | Where-Object { $_.Side -eq 'local' -and $_.Action -eq 'delete' -and $_.Method -eq 'ancestry' })
$localDeleteSquash = @($entries | Where-Object { $_.Side -eq 'local' -and $_.Action -eq 'delete' -and $_.Method -eq 'squash' })
$localKeep = @($entries | Where-Object { $_.Side -eq 'local' -and $_.Action -eq 'keep' })
$remoteDelete = @($entries | Where-Object { $_.Side -eq 'remote' -and $_.Action -eq 'delete' })
$remoteKeep = @($entries | Where-Object { $_.Side -eq 'remote' -and $_.Action -eq 'keep' })

if ($Json) {
    [pscustomobject]@{
        BaseRef          = $baseRef
        BaseSha          = $baseSha
        Remote           = $Remote
        Scope            = $Scope
        GitHub           = $githubStatus
        WorktreesEnabled = [bool]$Worktrees
        Deleted          = [bool]$Delete
        Entries          = @($worktreeEntries) + @($entries)
    } | ConvertTo-Json -Depth 4
}
else {
    function Write-EntryGroup {
        param([string]$Title, [object[]]$Items, [string]$Color)

        if (@($Items).Count -eq 0) { return }
        Write-Host ''
        Write-Host "  $Title ($(@($Items).Count))" -ForegroundColor $Color
        foreach ($item in $Items) {
            $ahead = if ($item.Ahead -gt 0) { " [+$($item.Ahead) commit(s) not in $baseRef]" } else { '' }
            Write-Host ("    {0,-42} {1}{2}" -f $item.Name, $item.Reason, $ahead)
        }
    }

    Write-Host ''
    Write-Host "Git cleanup - base $baseRef @ $baseSha" -ForegroundColor Cyan
    if ($githubStatus -ne 'ok') {
        Write-Warning "GitHub check unavailable ($githubStatus). Squash-merged branches will not be detected and an open pull request cannot protect a branch or its worktree. The ancestry-merged entries below are still safe to remove."
    }

    if ($Scope -ne 'Remote') {
        Write-Host ''
        Write-Host 'WORKTREES' -ForegroundColor White
        $removeTitle = if ($Worktrees) { 'REMOVE - merged' } else { 'REMOVABLE - merged (pass -Worktrees to remove)' }
        Write-EntryGroup -Title $removeTitle -Items $worktreeRemove -Color Yellow
        Write-EntryGroup -Title 'PRUNE - stale admin record' -Items $worktreePrune -Color Yellow
        Write-EntryGroup -Title 'KEEP' -Items $worktreeKeep -Color Green

        Write-Host ''
        Write-Host 'LOCAL BRANCHES' -ForegroundColor White
        Write-EntryGroup -Title "DELETE - merged into $baseRef" -Items $localDeleteAncestry -Color Yellow
        Write-EntryGroup -Title 'DELETE - squash/rebase merged (needs -D)' -Items $localDeleteSquash -Color Yellow
        Write-EntryGroup -Title 'KEEP' -Items $localKeep -Color Green
    }

    if ($Scope -ne 'Local') {
        Write-Host ''
        Write-Host "REMOTE BRANCHES ($Remote)" -ForegroundColor White
        Write-EntryGroup -Title "DELETE - merged into $baseRef" -Items $remoteDelete -Color Yellow
        Write-EntryGroup -Title 'KEEP' -Items $remoteKeep -Color Green
    }

    $worktreeAction = if ($Worktrees) { @($worktreeRemove).Count } else { 0 }
    Write-Host ''
    Write-Host ('Summary: remove {0} worktree(s), delete {1} local + {2} remote branch(es); keep {3} worktree(s), {4} local + {5} remote.' -f `
            $worktreeAction, (@($localDeleteAncestry).Count + @($localDeleteSquash).Count), @($remoteDelete).Count, `
        @($worktreeKeep).Count, @($localKeep).Count, @($remoteKeep).Count) -ForegroundColor Cyan

    if (-not $Worktrees -and @($worktreeRemove).Count -gt 0) {
        Write-Host "Pass -Worktrees to remove the $(@($worktreeRemove).Count) merged worktree(s) above and delete the branches they pin." -ForegroundColor DarkGray
    }
}

$totalDelete = @($localDeleteAncestry).Count + @($localDeleteSquash).Count + @($remoteDelete).Count
if ($Worktrees) { $totalDelete += @($worktreeRemove).Count + @($worktreePrune).Count }

if (-not $Delete) {
    if (-not $Json) {
        if ($totalDelete -eq 0) {
            Write-Host 'Nothing to remove.' -ForegroundColor DarkGray
        }
        else {
            $rerun = @("-Remote $Remote", "-BaseBranch $BaseBranch")
            if ($Scope -ne 'Both') { $rerun += "-Scope $Scope" }
            if ($NoFetch) { $rerun += '-NoFetch' }
            if ($NoGitHub) { $rerun += '-NoGitHub' }
            if ($Worktrees) { $rerun += '-Worktrees' }
            Write-Host ''
            Write-Host 'Nothing was removed. To do all of the above in one batch:' -ForegroundColor DarkGray
            Write-Host ("    .\scripts\Clean-GitBranches.ps1 {0} -Delete" -f ($rerun -join ' ')) -ForegroundColor DarkGray
        }
    }
    exit 0
}

# ---------------------------------------------------------------- delete ----

function Remove-BranchBatch {
    param([string[]]$Names, [string[]]$GitArguments, [string]$What)

    if (@($Names).Count -eq 0) { return 0 }
    if (-not $PSCmdlet.ShouldProcess(($Names -join ', '), $What)) { return 0 }

    Write-Host ''
    Write-Host "$What ($(@($Names).Count))..." -ForegroundColor Yellow
    $result = Invoke-Git -Arguments (@($GitArguments) + @($Names))
    foreach ($line in $result.Lines) { Write-Host "  $line" }
    if ($result.ExitCode -ne 0) {
        Write-Warning "$What exited with code $($result.ExitCode). Branches not named in an error above were still deleted."
        return 1
    }
    return 0
}

$failed = 0

# Worktrees first. A branch pinned by a worktree cannot be deleted, so this pass is
# what makes the branch pass below able to reach the ones the report listed as freed.
if ($Worktrees -and @($worktreeRemove).Count -gt 0) {
    Write-Host ''
    Write-Host "Removing merged worktrees ($(@($worktreeRemove).Count))..." -ForegroundColor Yellow
    foreach ($worktree in $worktreeRemove) {
        # One call each: `git worktree remove` takes a single path, unlike the branch
        # commands below. No --force -- the dirty check already excluded everything
        # that would need it, so a refusal here means the state changed under us and
        # is worth surfacing rather than overriding.
        if (-not $PSCmdlet.ShouldProcess($worktree.Path, 'Remove worktree')) { continue }
        $result = Invoke-Git -Arguments @('worktree', 'remove', $worktree.Path)
        foreach ($line in $result.Lines) { Write-Host "  $line" }
        if ($result.ExitCode -ne 0) {
            Write-Warning "Could not remove worktree $($worktree.Path) (exit $($result.ExitCode)). The branch it pins will be reported as still pinned."
            $failed = 1
        }
        else {
            Write-Host "  Removed worktree $($worktree.Path)" -ForegroundColor DarkGray
        }
    }
}

if ($Worktrees -and @($worktreePrune).Count -gt 0) {
    if ($PSCmdlet.ShouldProcess(($worktreePrune | ForEach-Object { $_.Name }) -join ', ', 'Prune stale worktree records')) {
        Write-Host ''
        Write-Host "Pruning stale worktree records ($(@($worktreePrune).Count))..." -ForegroundColor Yellow
        $result = Invoke-Git -Arguments @('worktree', 'prune', '--verbose')
        foreach ($line in $result.Lines) { Write-Host "  $line" }
        if ($result.ExitCode -ne 0) {
            Write-Warning "git worktree prune exited with code $($result.ExitCode)."
            $failed = 1
        }
    }
}

$failed += Remove-BranchBatch -Names @($localDeleteAncestry | ForEach-Object { $_.Name }) `
    -GitArguments @('branch', '-d') -What 'Deleting merged local branches'

# -D, not -d: git cannot see a squash-merge as merged, so -d would refuse every one
# of these. The evidence that nothing is lost is GitHub reporting the pull request as
# merged, which is why this is a separate batch rather than a fallback on -d failing.
$failed += Remove-BranchBatch -Names @($localDeleteSquash | ForEach-Object { $_.Name }) `
    -GitArguments @('branch', '-D') -What 'Deleting squash/rebase-merged local branches'

$failed += Remove-BranchBatch -Names @($remoteDelete | ForEach-Object { $_.Name }) `
    -GitArguments @('push', $Remote, '--delete') -What "Deleting merged branches on $Remote"

Write-Host ''
if ($failed -gt 0) {
    Write-Host 'Cleanup finished with errors.' -ForegroundColor Red
    exit 1
}

Write-Host 'Cleanup complete.' -ForegroundColor Green
exit 0
