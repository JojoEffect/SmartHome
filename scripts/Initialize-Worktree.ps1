<#
.SYNOPSIS
    Seed a fresh linked git worktree with the machine-local setup a clone never carries.

.DESCRIPTION
    `git worktree add` copies tracked files and nothing else, so a new worktree under
    .claude\worktrees\<name> starts without scripts\local.env.ps1, without
    scripts\nanoFramework.local.env.ps1 and without packages\ -- all three are
    git-ignored. The main checkout beside it is fully configured, which makes the
    failure confusing rather than obvious: the first script call aborts at its own
    first line with "Missing: ...\scripts\local.env.ps1", and the first build emits a
    wall of CS0518 across every project, which reads like broken source.

    This script copies both config files across from the main working tree and
    restores packages\, so a worktree is usable immediately after `git worktree add`.

    Idempotent and safe to call unconditionally:
      - In the main working tree it reports that and exits 0, changing nothing.
      - A config file that already exists is left alone, never overwritten -- a
        worktree may deliberately carry an edited copy (a different COM port for a
        second device, a different broker port).
      - Restore-Packages.ps1 skips every package already present.

    It deliberately does NOT use Import-SmartHomeLocalEnv: that helper exits when the
    config file is missing, which is the very state this script exists to repair.

.PARAMETER NoRestore
    Skip the packages\ restore and copy the config files only. The restore is the
    larger half of the work, but not by as much as it sounds: a fresh worktree seeds
    in about 1.6s all in, an already-seeded one re-runs in 0.5s, and the config copy
    alone is 0.03s. So this switch is for when packages\ is known good, not a default
    to reach for -- skipping the restore leaves the CS0518 wall in place for the first
    build, which is the failure this script exists to prevent.

.PARAMETER MainWorktree
    Root of the working tree to copy the config files from. Defaults to the main
    working tree, resolved via `git rev-parse --git-common-dir`. Override it when the
    machine-local config lives in some other checkout.

.EXAMPLE
    .\scripts\Initialize-Worktree.ps1
    Seed this worktree: copy both config files, then restore packages\.

.EXAMPLE
    .\scripts\Initialize-Worktree.ps1 -NoRestore
    Copy the config files only, leaving packages\ alone.
#>

[CmdletBinding()]
param(
    [switch]$NoRestore,

    [string]$MainWorktree
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Common.ps1')

$repoRoot = Get-SmartHomeRepoRoot
$scriptsDir = Get-SmartHomeScriptsDir

# ------------------------------------------------------------- which checkout? --

if ($MainWorktree) {
    # -LiteralPath throughout this script, never -Path. -Path interprets [ and ] as
    # wildcards, and both are legal in a Windows directory name: with the repository
    # at "C:\repos\SmartHome [wip]", Test-Path -Path reports False for a directory
    # that exists, so a perfectly good -MainWorktree is rejected as missing and an
    # already-present config file is copied over instead of kept.
    if (-not (Test-Path -LiteralPath $MainWorktree -PathType Container)) {
        Write-Error "-MainWorktree is not an existing directory: $MainWorktree"
        exit 1
    }

    $sourceRoot = (Resolve-Path -LiteralPath $MainWorktree).Path

    if ($sourceRoot.TrimEnd('\', '/') -eq $repoRoot.TrimEnd('\', '/')) {
        Write-Error @"
-MainWorktree points at this checkout ($repoRoot), so there is nothing to copy from.
Point it at the checkout that already has scripts\local.env.ps1.
"@
        exit 1
    }
}
else {
    # Not a linked worktree -- nothing to seed. Exit 0 rather than erroring: this
    # script is meant to be called unconditionally (from a hook, from a skill, from
    # the top of another script), and "you are in the main checkout" is a normal
    # outcome, not a failure.
    if (-not (Test-SmartHomeLinkedWorktree)) {
        $mainRoot = Get-SmartHomeMainWorktreeRoot
        if ($mainRoot) {
            Write-Host "Not a linked worktree -- this is the main working tree ($repoRoot). Nothing to seed." -ForegroundColor DarkGray
        }
        else {
            # Test-SmartHomeLinkedWorktree is false for "git could not answer" too,
            # and that is a different situation worth naming: an exported source tree
            # or a machine with no git still needs its config files, this script just
            # cannot find where to copy them from.
            Write-Host "Could not ask git which working tree this is -- assuming the main checkout. Nothing to seed." -ForegroundColor Yellow
            Write-Host "  If this IS a worktree, pass -MainWorktree <path to the main checkout>." -ForegroundColor DarkGray
        }

        exit 0
    }

    $sourceRoot = Get-SmartHomeMainWorktreeRoot
}

$sourceScripts = Join-Path $sourceRoot 'scripts'

Write-Host "Seeding worktree: $repoRoot" -ForegroundColor Cyan
Write-Host "  from: $sourceRoot" -ForegroundColor DarkGray
Write-Host ""

# ------------------------------------------------------------------ config files --

# Both are git-ignored, so neither ever arrives with a worktree. Named here rather
# than derived, because the templates beside them are not the thing to copy: a
# template has the machine's values blanked out, and seeding a worktree from one
# would trade "missing file" for "file present, values empty" -- a later failure that
# reads worse.
$configFiles = @('local.env.ps1', 'nanoFramework.local.env.ps1')

$copied = 0
$kept = 0
$absent = New-Object System.Collections.Generic.List[string]

foreach ($name in $configFiles) {
    $destination = Join-Path $scriptsDir $name
    $source = Join-Path $sourceScripts $name

    if (Test-Path -LiteralPath $destination) {
        Write-Host "  kept:    scripts\$name (already present)" -ForegroundColor DarkGray
        $kept++
        continue
    }

    if (-not (Test-Path -LiteralPath $source)) {
        Write-Host "  MISSING: scripts\$name -- not in $sourceScripts either" -ForegroundColor Red
        $absent.Add($name)
        continue
    }

    Copy-Item -LiteralPath $source -Destination $destination
    Write-Host "  copied:  scripts\$name" -ForegroundColor Green
    $copied++
}

if ($absent.Count -gt 0) {
    Write-Host ""
    $templateHints = ($absent | ForEach-Object {
        $template = $_ -replace '\.ps1$', '.template.ps1'
        # -LiteralPath in the printed command for the same reason as the copies above:
        # on a bracketed checkout Copy-Item -Path matches nothing and exits 0 having
        # copied nothing at all, so a user who runs exactly what this prints sees
        # success and then this identical error again, with nothing naming the cause.
        "    Copy-Item -LiteralPath `"$(Join-Path $scriptsDir $template)`" `"$(Join-Path $scriptsDir $_)`""
    }) -join "`n"

    Write-Error @"
$($absent.Count) config file(s) could not be seeded: $($absent -join ', ')
The source checkout does not have them either, so this is first-time setup on this
machine rather than a worktree that missed out. Copy the templates and fill them in:

$templateHints

Then re-run this script, or see the "First-time setup" section of CLAUDE.md.
"@
    exit 1
}

# --------------------------------------------------------------------- packages\ --

if ($NoRestore) {
    Write-Host ""
    Write-Host "Skipping the packages\ restore (-NoRestore). Run .\scripts\Restore-Packages.ps1 before building." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Worktree seeded: $copied copied, $kept already present." -ForegroundColor Green
    exit 0
}

Write-Host ""

# Reused rather than reimplemented: Restore-Packages.ps1 owns the cache layout, the
# already-present check and the "not in the local cache either" message. Invoked as
# this worktree's own copy, so it restores into this worktree's packages\.
#
# try/catch as well as the exit-code check: the restore can also fail by *throwing*
# rather than exiting -- a Copy-Item that hits Windows' 260-character path limit does,
# which a deep worktree path makes reachable. Without the catch that exception would
# propagate as a raw stack trace, hiding the one thing worth saying here (the config
# half is already seeded, so a re-run only has to redo the restore).
$restoreExit = 0
try {
    & (Join-Path $scriptsDir 'Restore-Packages.ps1')
    $restoreExit = $LASTEXITCODE
}
catch {
    Write-Host ""
    Write-Host $_.Exception.Message -ForegroundColor Red
    $restoreExit = 1
}

if ($restoreExit -ne 0) {
    # Restore-Packages.ps1 has already said which packages and why. Don't restate it;
    # do say that the config half of the seed did land, so a re-run is not needed for
    # that part.
    Write-Error @"
Config files are seeded, but the packages\ restore failed (exit $restoreExit). See the
message above. Re-run .\scripts\Restore-Packages.ps1 once it is addressed -- the config
files are already in place and will not be copied again.
"@
    exit $restoreExit
}

Write-Host ""
Write-Host "Worktree seeded: $copied config file(s) copied, $kept already present, packages\ restored." -ForegroundColor Green
Write-Host "Check it with: .\scripts\Test-Setup.ps1" -ForegroundColor DarkGray

# Explicit success code, same reason as Restore-Packages.ps1: callers read
# $LASTEXITCODE, and without this it would carry whatever the last native command
# happened to leave behind.
exit 0
