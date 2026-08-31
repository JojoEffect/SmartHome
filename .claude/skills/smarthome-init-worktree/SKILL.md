---
name: smarthome-init-worktree
description: Seed a fresh git worktree with the machine-local setup a clone never carries — both local.env files and a restored packages\ folder. Use as the first thing in any session running under .claude\worktrees\, or when a script in a worktree aborts with "Missing: ...\scripts\local.env.ps1" or a build fails with CS0518 everywhere.
---

# Seed a fresh SmartHome worktree

```powershell
.\scripts\Initialize-Worktree.ps1
```

Copies `scripts\local.env.ps1` and `scripts\nanoFramework.local.env.ps1` across from the main
working tree, then restores `packages\`. Touches no hardware, opens no port, needs no
confirmation.

Idempotent and safe to call unconditionally — including in the main checkout, where it reports
that and exits 0 without changing anything. Run it as the first command of a session in a
worktree rather than waiting for something to fail.

## Why a worktree needs seeding at all

`git worktree add` brings tracked files and nothing else. All three of these are git-ignored, so
none of them arrive:

- `scripts\local.env.ps1`
- `scripts\nanoFramework.local.env.ps1`
- `packages\`

The main checkout sitting right beside it is fully configured, which is what makes this
confusing rather than obvious. `Get-SmartHomeScriptsDir` returns the *calling script's own*
`$PSScriptRoot` — deliberately, so a worktree can carry different settings — so a worktree reads
its own missing config, never the main checkout's.

Both failures read like broken code:

| What is missing | What you actually see |
|---|---|
| Either `local.env` file | Any script aborting at its first line with `Missing: ...\scripts\local.env.ps1` |
| `packages\` | A wall of `error CS0518` — predefined type `System.Object` is not defined — in every project |

MSBuild on this machine emits **German** diagnostics, so match on the error *code*, not the
message text.

## Switches

| Switch | Effect |
|---|---|
| *(none)* | Copy both config files, then restore `packages\`. Measured at ~1.6s on a fresh worktree, ~0.5s when everything is already there |
| `-NoRestore` | Config files only, ~0.03s. For an unattended cheap path, or when `packages\` is known good |
| `-MainWorktree <path>` | Copy the config files from some other checkout instead of the main working tree |

## What it will not do

- **Never overwrites an existing config file.** A worktree may deliberately carry an edited copy
  — a second device's COM port, a different broker port — and this script keeps it, reporting
  `kept:` rather than `copied:`.
- **Never invents config.** If the main checkout has no `local.env.ps1` either, this is
  first-time setup on the machine, not a worktree that missed out; the script says so and prints
  the `Copy-Item` for the *template* with both paths filled in. It does not seed from a template
  itself — that would trade "file missing" for "file present, values empty", which fails later
  and reads worse.
- **Never hits the network.** The restore is `Restore-Packages.ps1`, which copies out of the
  local NuGet cache only.

## Finding the main checkout

Via `git rev-parse --git-common-dir` — the main repository's `.git`, shared by every linked
worktree; its parent is the main working tree. Shared as `Get-SmartHomeMainWorktreeRoot` in
`Common.ps1`, with `Test-SmartHomeLinkedWorktree` beside it for the yes/no question.

Deliberately not a relative hop like `..\..\..`: that is only correct for a worktree exactly
three levels down, which `.claude\worktrees\<name>` happens to be and nothing guarantees.

## Related

`smarthome-check-setup` (`Test-Setup.ps1`) reports whether a worktree is seeded, along with
every other prerequisite, and names this script as the fix. Run it after this one to confirm.
`smarthome-restore-packages` is the `packages\` half on its own, for when the config files are
already in place.
