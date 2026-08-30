---
name: smarthome-check-setup
description: Check that everything the SmartHome scripts assume exists on this machine — the two local.env files, restored packages, an authenticated gh, MSBuild/vstest, Mosquitto, the COM port. Use at the start of a session, in a fresh worktree, or when a script fails in a way that might be a missing prerequisite rather than a code problem.
---

# Check the SmartHome machine setup

```powershell
.\scripts\Test-Setup.ps1
```

Read-only. Nothing is installed, nothing is written, no COM port is opened and no device is
touched — so unlike the hardware scripts this needs no confirmation and is safe to run at any
point, including while someone else has the device.

Exit code is 0 when nothing FAILed (WARNings still pass), 1 otherwise.

## Why this exists

Everything the scripts in `scripts\` depend on beyond the source tree lives **outside the
repository**: two git-ignored `local.env` files, a restored `packages\` folder, an authenticated
`gh`, Visual Studio's MSBuild and vstest, Mosquitto, the ESP32's COM port. None of it is
version-controlled, none of it can be inferred from a clone, and a session cannot install any
of it for itself.

The problem is not that these go missing — it is *how* they fail. Each surfaces as something
that reads like broken code:

| What is missing | What you actually see |
|---|---|
| `packages\` not restored | A wall of `error CS0518` — predefined type `System.Object` is not defined — in every project, which looks like the source tree is broken |
| `scripts\local.env.ps1` | Any script aborting at its first line with `Missing: ...\scripts\local.env.ps1` |
| A value inside it | `Missing environment variable: SMARTHOME_...` |
| `gh` not authenticated | The backlog is simply unreadable; `gh issue list` fails |

This script reports all of them at once instead of letting a workflow discover one, stop, and
hide the next. It deliberately does not use `Import-SmartHomeLocalEnv`, which exits on the first
missing file — the opposite of what a preflight is for.

## When to run it

- **At the start of a session**, before the first script call, rather than discovering a gap
  midway through a workflow.
- **In a fresh worktree**, always. `Get-SmartHomeScriptsDir` returns the calling script's own
  `$PSScriptRoot`, so a worktree under `.claude\worktrees\<name>` reads *its own*
  `scripts\local.env.ps1`, never the main checkout's. Both `local.env` files and `packages\`
  are git-ignored, so a new worktree has neither and the first script call fails even though
  the main checkout is fully configured. The script detects the worktree and prints the exact
  `Copy-Item` from the main checkout.
- **When a script fails and it might not be your code** — especially a compile that broke
  everywhere at once, or a `gh` call that failed for no obvious reason.

## Reading the output

Each row is `OK`, `WARN` or `FAIL`, followed by a `How to fix` block naming the remediation for
anything that is not `OK`.

`WARN` is for things that do not block building: a COM port that is not currently present (the
device is unplugged — only the hardware-touching scripts care), companion nanoFramework repos
that have not been synced (needed before firmware/library debugging, not before a build), or a
check that could not run because the config it depends on is missing. A check that could not run
still gets a row, on purpose — a check that silently disappears reads as a check that passed.

Related: `smarthome-restore-packages` for the `packages\` fix, `smarthome-sync-nanoframework`
for the companion repos. The full prerequisite list, with the same failure table, is the
"First-time setup" section of `CLAUDE.md`.
