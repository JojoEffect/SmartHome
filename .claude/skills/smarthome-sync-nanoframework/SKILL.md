---
name: smarthome-sync-nanoframework
description: Sync the sibling nanoFramework repos (nf-interpreter, nanoFramework.m2mqtt, Samples, etc.) cloned beside SmartHome. Use before debugging any nanoFramework/firmware/library behavior, or when asked to sync/update companion repos.
---

# Sync nanoFramework companion repos

```powershell
.\scripts\Sync-NanoFrameworkRepos.ps1
```

Clones/updates 12 repos one level above `SmartHome` (`nf-interpreter`, `nanoFramework.m2mqtt`,
`Samples`, `nanoFramework.WebServer`, `nanoFramework.IoT.Device`, `Home`,
`nanoframework.github.io`, and others), on the branch set in
`scripts\nanoFramework.local.env.ps1`. First-time setup: copy
`scripts\nanoFramework.local.env.template.ps1` if that file doesn't exist yet.

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
actually implemented, in `src/DeviceInterfaces/System.Net/` and `src/PAL/`) →
`nanoFramework.m2mqtt` (the real MQTT client source) → `Home` (repo/contribution index).
