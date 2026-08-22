---
name: smarthome-sync-nanoframework
description: Sync the sibling nanoFramework repos (nf-interpreter, nanoFramework.m2mqtt, Samples, etc.) cloned beside SmartHome. Use before debugging any nanoFramework/firmware/library behavior, or when asked to sync/update companion repos.
---

# Sync nanoFramework companion repos

```powershell
.\scripts\Sync-NanoFrameworkRepos.ps1
```

Clones/updates 13 repos one level above `SmartHome` (`nf-interpreter`, `CoreLibrary`,
`nanoFramework.m2mqtt`, `Samples`, `nanoFramework.WebServer`, `nanoFramework.IoT.Device`, `Home`,
`nanoframework.github.io`, and others), on the branch set in
`scripts\nanoFramework.local.env.ps1`. First-time setup: copy
`scripts\nanoFramework.local.env.template.ps1` if that file doesn't exist yet.

Repos pinned to a specific tag/commit for exact-version source lookup (detached HEAD — see
"When to delegate" below) are skipped on re-sync, not reset — pass `-Force` to this script to
reset everything back to tracking branches instead.

**Run this before forming theories about a socket exception, an MQTT connect failure, or "does
this API even work this way" — not after getting stuck.** A real debugging session spent hours
guessing at package versions and searching the web blind before finally syncing these repos, at
which point the actual M2Mqtt source (not just its compiled NuGet package) and the native
`nf-interpreter` socket implementation were directly available. If the sibling repos aren't
already present (`Test-Path ..\nf-interpreter`), just run this — it's fast and side-effect-free,
no need to ask first.

Lookup order once synced: `nanoFramework.WebServer` (Skills/MCP) → `Samples` (working examples)
→ `nanoFramework.IoT.Device` (sensor drivers) → `nanoframework.github.io` (docs) →
`nf-interpreter` (native firmware/runtime — this is where `Socket`/`Poll`/native behavior is
actually implemented, in `src/DeviceInterfaces/System.Net/` and `src/PAL/`) → `CoreLibrary`
(mscorlib source — a **separate repo** from `nf-interpreter`, easy to miss since it's only
referenced from there as an external test dependency) → `nanoFramework.m2mqtt` (the real MQTT
client source) → `Home` (repo/contribution index).

## When to delegate instead of doing this inline

This skill (running the sync script, then reading source directly) is enough for most lookups.
For anything heavier — matching each sibling repo to the exact tag/commit for the package
versions this repo currently references, resolving version-string ambiguity across repos,
correlating a firmware build number back to a commit — dispatch the **nanoframework-sync
subagent** (`Agent` tool, `subagent_type: nanoframework-sync`) instead of doing the `git tag`/
`git log`/WebFetch legwork in the main conversation. A real instance of exactly that task ran to
66 tool calls and ~115k tokens across 4 repos; running that inline would have been a large,
mostly-irrelevant chunk of a conversation instead of one clean delegated report. The subagent
file is committed (`.claude/agents/nanoframework-sync.md`), so it's available in every session on
this repo, not just this one — but nothing forces a session to reach for it. If a sync/lookup
task is turning into more than a couple of exploratory git/WebFetch calls, that's the signal to
delegate rather than keep going inline. `nanoFramework.Hardware.Esp32`, `nanoFramework.Logging`,
`nanoFramework.m2mqtt`, `nf-interpreter`, and `CoreLibrary` are already pinned this way as of
2026-08-20 — check `git status` in a given sibling repo before assuming it's still tracking
`main`.
