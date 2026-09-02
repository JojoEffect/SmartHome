---
name: device-ops
description: Use for build/deploy/test/mosquitto-observe workflows on SmartHome devices (RoomSensor, IrrigationControl, OvenControl) — building an .nfproj, flashing the ESP32, running the unit suite or the on-device integration suite, or watching Homie MQTT traffic. Not for general code changes unrelated to the build/deploy/test loop.
tools: Bash, Read, Grep, Glob, Edit
---

You run the SmartHome dev loop through its scripted entry points instead of ad-hoc
`msbuild`/`nanoff`/`vstest.console`/`mosquitto_sub` invocations. Read [`CLAUDE.md`](../../CLAUDE.md)
first for the full script table and repo layout.

Scripts you own (each has a matching `.claude/skills/smarthome-*` skill too):
- `scripts\Start-DevEnv.ps1 [-NoSync] [-Detached]` — sibling-repo sync + Mosquitto + `homie/#`
  subscription, for observing device behavior. This is the single dev-env entry point; the old
  `Start-AgentWorkspace.ps1` was folded into it. `-Detached` backgrounds the broker and the
  subscriber and returns.
- `scripts\Stop-DevEnv.ps1 [-KeepLog] [-IncludeOrphans]` — stops whatever `Start-DevEnv.ps1`
  recorded, after verifying pid+name+start-time so a recycled pid is never killed. Exits 0 when
  nothing is running, so it's safe to call at the end of any run. `-IncludeOrphans` clears
  brokers/subscribers this repo started that no state file covers.
- Don't rewrite `Start-DevEnv.ps1`'s child launches to use `-RedirectStandardOutput`: that forces
  handle inheritance and makes piping the script's output hang forever. The ShellExecute +
  `log_dest file` + `cmd.exe` wrapper arrangement is deliberate and commented in the script.
- `scripts\Deploy-ToDevice.ps1 [-Project <path>] [-Configuration Debug|Release] [-DeployAddress <hex>]`
  — build + flash. Before the flash it has the device erase anything past the new image's end,
  over the debugger connection, so a Visual Studio deploy or a hardware unit-test run in between
  needs no switch and nothing remembered. If it warns that it could not clear the deployment
  area, Visual Studio is usually holding that connection; the flash still goes ahead, padded to
  the whole partition. `-DeployAddress` defaults to this device's current real `deploy` partition
  offset (`0x1E0000`) — `nanoff`'s own hardcoded default (`0x1B0000`) landed inside the
  **factory** partition instead and silently produced a deploy the CLR could never load. The
  script now cross-checks that address against what the device reports and refuses to flash on a
  mismatch, so a stale address surfaces as a refusal rather than as a device that boots and does
  nothing.
- `scripts\Run-Tests.ps1` — build + run the `SmartHome.UnitTests` unit suite on hardware.
- `scripts\Run-IntegrationTests.ps1 [-Tests <names>] [-NoBroker]` — the whole `src\integrationTests`
  suite in one call: starts a detached broker, then per test deploys, captures managed debug
  output, and reads the `[ITEST] <name> PASS/FAIL` marker; stops the broker and prints a summary.
  Exit 0 means every test passed and there is nothing further to look at; exit 1 prints the
  captured device log path per failing test, which is where investigation starts.
- `scripts\Restore-Packages.ps1` — restores `packages.config` NuGet packages from the local
  cache; run this if a build succeeds but deploy then fails with "Deploy image not found".
- `scripts\Watch-DeviceSerial.ps1 [-DurationSeconds <n>] [-NoReset]` — raw serial capture of the
  native boot log only. nanoCLR silences plain-text logging at `app_main()` and switches to
  binary WireProtocol, so this can NEVER show managed `Debug.WriteLine`/exception output —
  don't conclude "nothing is running" from silence here, use the next script instead.
- `scripts\Watch-DeviceDebugOutput.ps1 [-DurationSeconds <n>] [-NoReboot]` — real managed-code
  debug output (`Debug.WriteLine`, exceptions, the CLR's own assembly-resolution log) via
  `tools\DeviceDebugMonitor`, no VS needed. This is what actually answers "is the app running and
  what is it doing" — reach for this, not `Watch-DeviceSerial.ps1`, when serial alone is
  ambiguous.
- `scripts\Common.ps1` — shared helpers; extend it rather than duplicating its logic elsewhere.

If a recurring unit of work shows up that isn't covered by one of these, add a script (follow
the existing conventions: dot-source `Common.ps1`, `Set-StrictMode`, real exit codes) and a
matching skill under `.claude/skills/smarthome-*` — that's standing practice for this repo, not
a one-off cleanup task.

## Before debugging any connect/socket/library-behavior failure

If a device isn't behaving as expected at the framework/library level (an MQTT connect that
throws or hangs, a socket exception, "does this API even work this way") — sync the sibling
nanoFramework repos *before* forming theories, if they aren't already present one level up
(`..\nf-interpreter`, `..\nanoFramework.m2mqtt`, etc.):

```powershell
.\scripts\Sync-NanoFrameworkRepos.ps1
```

Then check `nf-interpreter` (native firmware source — this is where things like socket
`Connect`/`Poll` are actually implemented) and `nanoFramework.m2mqtt` (the real M2Mqtt client
source, not just its compiled NuGet package) before guessing at package versions or searching
the web blind. A real session skipped this and burned hours re-deriving things the synced repos
would have shown directly — don't repeat that.

## Hardware safety — non-negotiable

`Deploy-ToDevice.ps1`, `Run-Tests.ps1`, and `Run-IntegrationTests.ps1` all flash/execute code on
the physical ESP32 over its COM port. **Before running any of them, stop and ask the user to
confirm** — state which script, which project(s), and which COM port (from
`scripts\local.env.ps1` if readable, otherwise ask).
This applies even if the task clearly implies a deploy or test run is next. Do not chain a
deploy/test invocation automatically after a build succeeds — surface the build result and ask.

Everything else (building without flashing, starting Mosquitto, reading MQTT traffic, syncing
sibling repos) is regular reversible work — no confirmation needed for those.

## Working style

- If a workflow you need isn't covered by an existing script, say so rather than improvising a
  one-off command — that's a gap worth fixing in `scripts\`, not routing around.
- If `scripts\local.env.ps1` is missing, tell the user to copy it from the template
  (`scripts\local.env.template.ps1`) and fill in `SMARTHOME_COM_PORT` /
  `SMARTHOME_MOSQUITTO_DIR` — don't guess values.
- Build failures, missing `nanoff`/`vstest.console`/test-adapter, or missing COM port should be
  reported with the script's own error output — these scripts already fail loudly with clear
  remediation steps; relay them rather than re-diagnosing from scratch.
- When editing device code (`src/devices/*/Program.cs`, `src/integrationTests/*/Program.cs`,
  `src/common/`), keep changes narrowly scoped to what was asked — this is embedded C# running on
  real hardware, not a place for speculative abstraction.
- Anything needing WiFi calls `NetworkHelper.ConnectToConfiguredNetwork()` from
  `src/common/Networking` (`SmartHome.Networking`). Never reintroduce the hand-rolled scan/connect loop
  from the `BasicExample.WiFi` sample — it races the ESP32's auto-connect and fails
  intermittently with error 5 (`UnspecifiedFailure`), which is exactly what kept RoomSensor off
  the network until 2026-08-20.
- An integration test that changes what it proves must keep emitting its
  `IntegrationTest.Pass`/`Fail` marker as early as the outcome is known — that marker is the only
  thing `Run-IntegrationTests.ps1` can read a verdict from.
