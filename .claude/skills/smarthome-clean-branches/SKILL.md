---
name: smarthome-clean-branches
description: Report which git worktrees and branches, local and remote, are already merged into main, then remove them in one batch. Use when asked to clean up branches or worktrees, tidy the repository, remove merged or stale checkouts, or find out what is still unmerged.
---

# Clean up merged worktrees and branches

Analyse first — this is read-only and removes nothing:

```powershell
.\scripts\Clean-GitBranches.ps1
```

Show the user the groups, get their approval, then do the whole thing in one batch:

```powershell
.\scripts\Clean-GitBranches.ps1 -Worktrees -Delete
```

Both calls do the entire analysis in the script. Do not re-derive it with `git branch
--merged`, `git worktree list`, `gh pr list` and a pile of `git rev-list` calls in the
conversation — that is what this script exists to replace, and it costs a fraction of the
tokens.

Read-only unless `-Delete` is passed. It touches no hardware, opens no port, and deliberately
does not read `scripts\local.env.ps1`, so it works in a fresh worktree with no setup.

## Branches

A branch is a candidate only when its work is provably already in the base branch:

- **Ancestry** — the branch tip is an ancestor of `origin/main` (`git branch --merged`, the
  same test git uses to allow `git branch -d`). An ancestor contributes no commit the base
  branch does not already have, so nothing can be lost. Deleted with `-d`.
- **Squash/rebase merge** — the tip is *not* an ancestor, but `gh` reports its pull request as
  MERGED. GitHub's squash and rebase merges rewrite the commits, so ancestry cannot see them.
  Reported in its own group and deleted with `-D`, because git will refuse `-d` here and the
  evidence is GitHub's word rather than local history.

Everything else is kept, and the report says why per branch:

| Kept | Why |
|---|---|
| `main` (and anything in `-Protect`) | the base branch |
| pinned by a worktree that is staying | git would refuse anyway; classified up front so the batch cannot fail halfway |
| open pull request #N | still in review, even if its work happens to be in main |
| not merged | carries commits `origin/main` does not have — the count is shown |

An **open pull request outranks a merged one on the same branch name**, so reusing a branch
name protects it rather than freeing it.

## Worktrees

Worktrees are always analysed and reported. `-Worktrees` is what allows them to be *removed* —
a plain `-Delete` leaves them alone, and the branches they pin stay pinned. Every state, and
what happens to it:

| State | Handling |
|---|---|
| directory gone, admin record left | **prune** — `git worktree prune` clears the record, and its branch is freed in the same run |
| main worktree | keep — git refuses to remove it, and it is the repository |
| the one the script is running in | keep — removing it deletes the running session's own directory |
| locked (`git worktree lock`) | keep — an explicit do-not-touch marker; the reason is shown |
| `git status` there failed | keep — cleanliness is unknown, and unknown is not clean |
| uncommitted or untracked changes | **keep, always** — see below |
| pins a protected branch, or one with an open pull request | keep |
| clean, HEAD already in `origin/main` (detached or on a branch) | **remove** |
| clean, but carries commits `origin/main` does not have | keep — the count is shown |

**A dirty worktree is never removed and there is no flag to override that.** Uncommitted work
is the one thing neither the reflog nor the remote can give back; every other mistake this
script could make is recoverable. Deal with those by hand.

The check fails closed. A `git status` that could not run — a corrupt index, a hook or filter
error, `safe.directory` refusing the path — is reported as *unknown*, not as clean, and the
worktree is kept. "No changes found" and "could not look" must not reach the removal pass as
the same answer.

`git worktree remove` is called without `--force` for the same reason: the dirty check already
excluded everything that would need it, so a refusal means the state changed underneath and is
worth surfacing rather than overriding.

### The two passes chain

With `-Worktrees -Delete`, worktrees are removed first, then the branches they were pinning are
deleted in the same run. The report reflects that up front — a branch freed by a pending
removal is listed as a delete candidate with `freed by removing worktree <name>`, not as
pinned. Without `-Worktrees` the same branch is listed as kept and pinned, which is exactly
what would happen.

### `git worktree remove` is not atomic

It unregisters the worktree and *then* deletes the directory. On Windows the second half
fails whenever another process holds something in there — a file open in an editor, or just a
shell whose current directory is inside it (a process's cwd locks the directory even when it
is empty). The record is gone by then, so the branch is no longer pinned.

The script asks git which of the two happened rather than assuming, and says so:

- **Still registered** — the branch stays pinned, and it is dropped from the delete batch so
  one failure produces one warning instead of two.
- **Unregistered, directory left behind** — an orphan git no longer knows about. Nothing points
  at it, and the branch delete proceeds correctly. The warning prints the exact
  `Remove-Item -Recurse -Force '<path>'` to finish the job once the holder lets go.

Either way the run exits 1. An orphan is invisible to a re-run — `git worktree list` no longer
mentions it — so it has to be deleted from the warning, not by running the script again.

### A clean worktree can still be in use

A worktree with nothing uncommitted may still belong to a live agent session that simply has no
edits yet. The script cannot tell. Two defences:

- Confirm the removal list with the user before running `-Worktrees -Delete`, the same way you
  would for any other batch that deletes directories.
- Mark a worktree that must survive: `git worktree lock <path> --reason "session in progress"`.
  The script honours the lock and prints the reason.

### The main checkout can pin a branch forever

If the main worktree has a feature branch checked out, that branch can never be freed by this
script — the main worktree is never removed. Switch it (`git switch main` there) and re-run.

## Options

| Flag | Effect |
|---|---|
| `-Worktrees` | Allow merged worktrees to be removed and their branches freed |
| `-Delete` | Actually remove. Without it, report only, exit 0 |
| `-WhatIf` | With `-Delete`, print the exact removals without running them |
| `-Scope Local\|Remote\|Both` | Default `Both`. Worktrees are local, so `-Scope Remote` skips them |
| `-Protect a,b` | Keep these branch names, and any worktree pinning one |
| `-BaseBranch` / `-Remote` | Default `main` / `origin` |
| `-NoFetch` | Skip the `git fetch --prune` that refreshes the remote view first |
| `-NoGitHub` | Skip `gh` (see below) |
| `-Json` | Full classification, worktrees included, as JSON instead of the human report |

## When `gh` is unavailable

Without an authenticated `gh` the script warns and continues, but loses both things only
GitHub knows: squash-merged branches are not detected, and an open pull request cannot protect
a branch or the worktree pinning it. Ancestry-merged branches and worktrees are still found and
are still safe to remove. Fix it with `gh auth login` (`smarthome-check-setup` reports this
too).

## Exit codes

`0` — analysis completed, or everything succeeded. `1` — a removal or deletion failed (the
warning names which), or the base branch could not be resolved.
