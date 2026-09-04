# SmartHome — Claude Code guidance

**nanoFramework** solution for **ESP32** home-automation devices, speaking the **Homie v4** MQTT
convention (topic prefix `homie/`). Primary device today: **RoomSensor**.

This file is the single source of truth for how to work in this repo — layout, scripts,
machine prerequisites, companion repos, version policy. (It replaced `.github/copilot-instructions.md` and the root
`AGENTS.md`, which were near-duplicates of it; if you find a stale reference to either, fix the
reference rather than recreating the file.)

The local workflow is built around **Mosquitto** (local MQTT observation), **nanoff** (CLI
deploy/flash), **Visual Studio** (deploy/debug when needed), and the **sibling nanoFramework
repositories** checked out beside `SmartHome`.

## Hardware safety — read this first

Three scripts talk to the physical ESP32 over its COM port: `Deploy-ToDevice.ps1` (flashes
firmware), `Run-Tests.ps1` (`nano.runsettings` has `IsRealHardware=True`, so it deploys and
executes test code on the device too), and `Run-IntegrationTests.ps1` (deploys and runs each
integration test project in turn). **Always confirm with the user before running any of them**,
even mid-session, even if a task seems to obviously call for it. Everything else in this repo
(build, mosquitto, repo sync) is regular reversible work.

## Start here for any framework/library/device debugging

Before investigating anything nanoFramework-, firmware-, or library-behavior related (a socket
exception, an MQTT connect failure, "does this API exist," version-alignment questions), **sync
the sibling repos first** if they aren't already present one level up from this repo
(`..\nf-interpreter`, `..\nanoFramework.m2mqtt`, etc. — check with a quick `Test-Path`, or just
run the idempotent sync):

```powershell
.\scripts\Sync-NanoFrameworkRepos.ps1
```

This is not optional/best-effort — a real debugging session on 2026-08-19 spent hours
diagnosing a socket connect failure (guessing at package versions, capturing raw serial output,
searching GitHub blind) before finally syncing these repos, at which point their source, samples,
and docs became directly available for exactly that failure. Don't repeat that: sync first, then
investigate using `nf-interpreter` source (native firmware behavior),
`nanoFramework.m2mqtt` source (the actual M2Mqtt client code, not just its NuGet package),
and `Samples` (working reference usage) before falling back to web search or guesswork.

## The reliable entry points

The goal for this repo is a dev loop that works equally well by hand or from an agent: one
script per workflow, sourced from `scripts\local.env.ps1`, non-interactive, real exit codes.
Prefer these over ad-hoc `msbuild`/`nanoff`/`mosquitto_sub` invocations — if a workflow doesn't
have a script yet, that's a gap worth closing rather than working around.

| Script | Does | Touches hardware? | Skill |
|---|---|---|---|
| `scripts\Start-DevEnv.ps1 [-NoSync] [-Detached]` | Syncs the sibling repos (unless `-NoSync`), then starts local Mosquitto (explicit `0.0.0.0` listener — a bare `-p` binds localhost-only on Mosquitto 2.x and silently can't be reached from a real device) and subscribes to `homie/#`. `-Detached` backgrounds both and returns | No | `smarthome-dev-env` |
| `scripts\Stop-DevEnv.ps1 [-KeepLog] [-IncludeOrphans]` | Stops whatever `Start-DevEnv.ps1` recorded for the configured port, verifying pid+name+start-time first so a recycled pid is never killed. No-op + exit 0 if nothing is running, so it's safe to call unconditionally. `-IncludeOrphans` also clears brokers/subscribers this repo started that no state file covers | No | `smarthome-dev-env` |
| `scripts\Deploy-ToDevice.ps1 [-Project <path>] [-Configuration Debug\|Release]` | Always `/t:Rebuild`s (a plain incremental build silently drops the deployment `.bin`), has the device erase whatever sits past the new image's end, then flashes via `nanoff` (see Clearing the deployment area below) | **Yes** | `smarthome-deploy` |
| `scripts\Run-Tests.ps1` | Builds `SmartHome.UnitTests` and runs it via `vstest.console` + the nanoFramework test adapter | **Yes** | `smarthome-test` |
| `scripts\Run-ScriptTests.ps1 [-File <names>] [-Name <wildcard>] [-Detailed]` | The host-side script tests: `scripts\tests\*.Tests.ps1`, run by this repo's own `TestRunner.ps1`. ~200 cases in about 20s, needing nothing installed — no device, no broker, no `local.env`, no `packages\`, no Pester. A run that executed zero tests fails | No | `smarthome-script-tests` |
| `scripts\Run-IntegrationTests.ps1 [-Tests <names>] [-NoBroker]` | The whole `src\integrationTests` suite in one call: broker up, deploy + capture + verdict per test, broker down, summary + exit code | **Yes** | `smarthome-integration-tests` |
| `scripts\Sync-NanoFrameworkRepos.ps1 [-Force]` | Clones/updates the sibling nanoFramework repos beside `SmartHome` | No | `smarthome-sync-nanoframework` |
| `scripts\Restore-Packages.ps1 [-Prune]` | Restores classic `packages.config` NuGet packages from the local NuGet cache — `msbuild /t:Restore` is a no-op for this repo's project style. Only *this* checkout's configs: worktrees live inside the main checkout, and `Get-SmartHomePackagesConfig` in `Common.ps1` is the one glob that says so, shared with `Test-Setup.ps1`. Also reports how many `packages\` folders nothing references any more; `-Prune` removes them, `-Prune -WhatIf` lists them | No | `smarthome-restore-packages` |
| `scripts\Initialize-Worktree.ps1 [-NoRestore] [-MainWorktree <path>]` | Seeds a fresh linked worktree with the three git-ignored things `git worktree add` never brings: both `local.env` files, copied from the main working tree, and a restored `packages\`. Idempotent — never overwrites a config file the worktree already has, and no-ops with exit 0 in the main checkout, so it is safe to call unconditionally | No | `smarthome-init-worktree` |
| `scripts\Clean-GitBranches.ps1 [-Worktrees] [-Delete] [-Scope Local\|Remote\|Both] [-Protect <names>]` | Classifies every worktree and every local and remote branch against `origin/main` and, with `-Delete`, removes the merged ones in one batch. Report-only without it. Keeps the base branch, anything with an open pull request, anything unmerged, and — unless `-Worktrees` frees them — the branches worktrees pin. A dirty worktree is never removed, and there is deliberately no flag that overrides that | No | `smarthome-clean-branches` |
| `scripts\Get-BacklogPriorities.ps1 [-Hardware ...] [-TimeBudget ...] [-Theme ...] [-Overrides <path>] [-Handoff <n>] [-RankingOnly] [-Top <n>] [-Json]` | Classifies every open issue on eight axes the labels don't cover — verification trust, evidence debt, where the edit lands, what verifying it needs, capability vs velocity, risk, effort, what it unblocks — then clusters and ranks them. Run plain first; the interview in the skill turns the answers into the weighting flags for a second run. Classification is a keyword heuristic that reports its own confidence and blind spots, and `-Overrides` is how a read of the actual bodies corrects it | No | `smarthome-prioritize` |
| `scripts\Test-Setup.ps1` | Reports everything the other scripts assume exists on this machine — both `local.env` files and their values, restored `packages\`, the test adapter, `gh` auth, MSBuild/vstest, Mosquitto, the COM port, the companion repos — all at once, rather than one abort at a time. Read-only; opens no port and touches no device | No | `smarthome-check-setup` |
| `scripts\Watch-DeviceSerial.ps1 [-DurationSeconds <n>] [-NoReset]` | Raw serial capture of the device's native boot log only — nanoCLR silences this at `app_main()` and switches to binary WireProtocol, so this can't see managed output | Resets only | `smarthome-watch-serial` |
| `scripts\Watch-DeviceDebugOutput.ps1 [-DurationSeconds <n>] [-NoReboot] [-NoBuild] [-BuildOnly] [-Until <regex>] [-DumpConfig]` | Real managed-code debug output (`Debug.WriteLine`, exceptions) via `tools\DeviceDebugMonitor` — no VS needed, same library VS's debugger extension uses | Resets only | `smarthome-watch-debug-output` |
| `scripts\Set-AssemblyVersion.ps1 -Version <v> [-Check]` | Stamps a version into every `AssemblyInfo.cs` under `src` — a plain recursive glob, not a fixed list and not restricted to `Properties\`, so adding a device needs no edit here and a stray one anywhere under `src` will also be picked up (and fails the run if it carries no version attribute). `.nfproj` has no generated assembly info, so this is the only thing that makes a release build carry its version. Normally invoked by the release workflow, not by hand | No | `smarthome-release` |
| `scripts\Common.ps1` | Shared helpers (env loading, MSBuild/vstest/adapter discovery, repo-sync, dev-env state) — dot-source, don't duplicate its logic | No | — |

`Start-DevEnv.ps1` is the session bootstrap: it absorbed the old `Start-AgentWorkspace.ps1`,
which was only `Sync-NanoFrameworkRepos.ps1` followed by `Start-DevEnv.ps1`. Pass `-NoSync` when
the siblings are known current or you're offline.

Two things about `Start-DevEnv.ps1` worth knowing before changing it. Its background children
are started through ShellExecute (`-WindowStyle`, never `-RedirectStandardOutput`) and log via
Mosquitto's own `log_dest file` and a `cmd.exe` redirect wrapper. That is not stylistic: the
`-Redirect*` switches force `UseShellExecute=false`, and .NET then creates the child with
`bInheritHandles=TRUE`, handing it *every* inheritable handle including this script's stdout --
so `.\scripts\Start-DevEnv.ps1 -Detached | tail` would hang forever waiting for an EOF the
surviving child still holds open. And a stale state file (Ctrl+C, killed shell, reboot) is
cleared automatically; it refuses only when the recorded processes are genuinely alive.

Every script here has a matching project skill under `.claude/skills/smarthome-*` — prefer
invoking the skill (or just running the script directly) over re-deriving the workflow ad hoc.
If a new recurring unit of work shows up that isn't covered by an existing script, add one
(follow the conventions above: `Common.ps1` helpers, `Set-StrictMode`, real exit codes, clear
`Write-Error` remediation) and give it a matching skill — that's the standing expectation for
this repo, not a one-time cleanup.

### Clearing the deployment area

`nanoff` erases and writes only the image file's own byte length, so a smaller app flashed
after a larger one leaves the larger one's tail unerased past the new image's end — and the
CLR finds it. It walks the *whole* deployment partition looking for assembly headers and, on
a header that doesn't check out, moves to the next candidate rather than stopping
(`ContiguousBlockAssemblies` in `nf-interpreter`'s `src/CLR/Startup/CLRStartup.cpp`). No
amount of trailing blank flash terminates that scan; only erasing the stale bytes does. So
every deploy has to hold exactly one invariant: **nothing but erased flash past the end of
the image being written**.

`Deploy-ToDevice.ps1` asks the device to hold it, immediately before the flash. Nothing on
this machine has to know what is already on the device, so nothing can be wrong about it.
Three attempts at that question, of which only the third works, all checked against the
pinned sibling checkouts rather than assumed:

- `Monitor_DeploymentMap`, the command whose name promises the used extent, is a **stub** in
  the firmware — it replies with an empty payload (`Debugger.cpp`), so `nf-debugger`'s
  `GetDeploymentMap()` always hands back an empty list.
- `Debugging_Deployment_Status` reads as the direct question and **does not work here**: on
  this ESP32 it comes back with no usable geometry. Nothing in `nf-debugger`'s own ESP32 path
  depends on it either — `DeploymentExecuteFull` is its only caller, and the ESP32 reports
  `IncrementalDeployment`, so `DeploymentExecuteIncremental` is what actually runs. Measured on
  the device on 2026-09-02, not reasoned about.
- The **flash sector map** (`Monitor_FlashSectorMap`, a `Monitor_` command rather than a
  `Debugging_` one) does answer, and its `c_MEMORY_USAGE_DEPLOYMENT` entry gives the partition's
  start and size — its geometry, not how much of it is in use. `0x1E0000` / `1835008`, which
  is exactly what `-DeployAddress` and `-DeployPartitionSize` had been carrying from a boot log.
- `AccessMemory_Read` over the deploy partition is **refused**: `CheckPermission` lists no
  `BLOCKTYPE_DEPLOYMENT` case for reads. (`Watch-DeviceDebugOutput.ps1 -DumpConfig` reads the
  *config* partition, which is permitted — that is why that one works.)
- `AccessMemory_Erase` **does** list it, and the firmware skips the erase when the region from
  the given address to the end of the partition is already blank
  (`Esp32FlashDriver_IsBlockErased`). Same command Visual Studio's own deploy issues before it
  writes (`DeploymentExecute` in `nf-debugger`'s `WireProtocol/Engine.cs`).

So the device is told to erase rather than asked what is there, through
`DeviceDebugMonitor --erase-deployment <keep-bytes>` and `Clear-SmartHomeDeviceDeployment` in
`Common.ps1`. On ESP32 an erase that *is* needed takes the whole partition with it
(`Esp32FlashDriver_EraseBlock` ignores the address), so the device has no app between that
call and the flash — which is why it runs immediately before, not as a tidy-up step.

That call also returns the partition's real geometry, and the deploy **refuses to flash** when
the device's start address disagrees with `-DeployAddress`. That mismatch is the failure that
cost a whole debugging session: `nanoff`'s own default address landed in the *factory*
partition on this device's layout, every deploy silently never ran, and `nanoff` reported
success either way. A reported partition *length* that disagrees with `-DeployPartitionSize`
is a warning, and the device's value wins.

Until 2026-09-02 the script guessed instead, padding each image with 0xFF as far as a
per-COM-port record of what *it* last flashed (issue #46). The record could only ever describe
this script's own flashes — Visual Studio's F5 deploy and the nanoFramework test adapter write
the same partition over the debugger connection and erase only their own footprint too — so
`Run-Tests.ps1` had to invalidate it, Visual Studio could not, and a `-FullPad` switch existed
for the case only a human could notice. Record, switch and `Run-Tests.ps1`'s call are all gone.
The 0xFF pad survives as the fallback for one case: the device could not be reached at all (VS
holding the debugger connection is the usual reason), where the image is padded to the whole
partition and the flash goes ahead with a warning.

## First-time setup

Everything in this section lives **outside the repository** — on the machine the session is
running on. None of it is version-controlled, none of it can be inferred from the source tree,
and a session cannot install any of it for itself. So it is an assumption to check rather than
a given, and checking is cheap: each item goes missing as something that reads like broken code
or a broken build, which is what the failure table below exists to translate.

### Machine-local config

```powershell
Copy-Item scripts\local.env.template.ps1 scripts\local.env.ps1
Copy-Item scripts\nanoFramework.local.env.template.ps1 scripts\nanoFramework.local.env.ps1
```

Then fill in:

- `scripts\local.env.ps1` — `SMARTHOME_COM_PORT`, `SMARTHOME_MOSQUITTO_DIR`, optionally
  `SMARTHOME_MQTT_BROKER` / `SMARTHOME_MQTT_PORT`
- `scripts\nanoFramework.local.env.ps1` — `SMARTHOME_NANOFW_BRANCH`

Both `local.env.ps1` files are git-ignored — never commit them. Every script dot-sources them
through `Import-SmartHomeLocalEnv` before doing anything else, so a missing or half-filled file
stops the run at its first line, ahead of any build, flash or broker start.

### Required local tools

- [nanoFramework VS extension](https://marketplace.visualstudio.com/items?itemName=nanoframework.nanoFramework-VS2022-Extension)
- [.NET SDK](https://dotnet.microsoft.com/download) — not just the runtime.
  `tools\DeviceDebugMonitor` is a net8.0 project, and it is built by every debug capture
  *and* by every deploy (which now has the device clear its deployment area first). A
  machine that installed `nanoff` as a global tool has only the runtime, so this does not
  come for free with the line below it
- `nanoff` CLI: `dotnet tool install -g nanoff`
- [Mosquitto](https://mosquitto.org/download/)
- Git on PATH
- [GitHub CLI](https://cli.github.com/) (`gh`), **authenticated** — `gh auth login`, then
  confirm with `gh auth status`. Not optional and not only for opening pull requests: the
  backlog *is* GitHub issues (see Open work below), so `gh issue list` is how you find out
  what there is to do. A human can fall back to the issues page in a browser; an agent
  session has no such fallback, and every workflow in this file that reaches GitHub goes
  through `gh`.

### Check it before relying on it

```powershell
.\scripts\Test-Setup.ps1
```

Checks every item in this section in one pass and reports them together — read-only, opens no
COM port, touches no device, exit 0 unless something FAILs. Worth running before the first
script call of a session rather than discovering the gap midway through a workflow, and always
in a fresh worktree. Skill: `smarthome-check-setup`.

It reports rather than aborts, on purpose: `Import-SmartHomeLocalEnv` exits on the first missing
file, so a workflow that relies on it discovers one gap, stops, and hides the next.

One trap if you check something by hand instead: in Windows PowerShell 5.1, do **not** pipe a
native tool through `2>&1`. The redirect wraps the exe's ordinary stderr in a
`NativeCommandError` and sets `$?` to false, so a perfectly healthy `nanoff --version` reports
as a failure.

### What a missing prerequisite looks like

None of these are code problems, and each has been read as one. `Test-Setup.ps1` reports all of
them by name; the table is for when you meet one without having run it:

| Symptom | Cause | Fix |
|---|---|---|
| A script exits at once with `Missing: ...\scripts\local.env.ps1` | config file absent | in a worktree, `scripts\Initialize-Worktree.ps1`; otherwise copy the templates above, or the filled-in files from another checkout |
| `Missing environment variable: SMARTHOME_...` | config file present but incomplete | fill the value in, comparing against the template |
| A wall of `error CS0518` — predefined type `System.Object`/`System.Void` not defined — in every project | NuGet packages not restored: `packages\` is git-ignored (`.gitignore:200`) and comes with no clone | `scripts\Restore-Packages.ps1` (or `scripts\Initialize-Worktree.ps1`, which also seeds the config files). A plain `msbuild /t:Restore` is a no-op for this project style |
| `gh` fails with an authentication error | `gh auth login` never run, or the token expired | `gh auth login`. The backlog is unreadable until then |

MSBuild on this machine emits **German** diagnostics, so match on the error *code* (`CS0518`)
rather than on English message text.

### A fresh worktree starts with none of it

`Get-SmartHomeScriptsDir` returns the calling script's own `$PSScriptRoot`, so a worktree under
`.claude\worktrees\<name>` reads *its own* `scripts\local.env.ps1`, never the main checkout's.
Both config files and `packages\` are git-ignored, so a new worktree has neither, and the first
script call fails there even though the main checkout is fully set up. Seed it once, from the
worktree root:

```powershell
.\scripts\Initialize-Worktree.ps1
```

That copies both config files from the main working tree and restores `packages\` — about 1.6s
on a fresh worktree. **Run it as the first command of any session in a worktree**, rather than
waiting for a script to abort or a build to emit CS0518 everywhere. It is idempotent, never
overwrites a config file the worktree already has, and no-ops with exit 0 in the main checkout,
so calling it unconditionally is fine. `-NoRestore` does the config half only (~0.03s).

It finds the main checkout via `git rev-parse --git-common-dir` — the main repository's `.git`,
shared by every linked worktree, whose parent is the main working tree. Not by a relative hop:
`..\..\..` is only correct for a worktree exactly three levels down. `Common.ps1` exposes that
as `Get-SmartHomeMainWorktreeRoot`, with `Test-SmartHomeLinkedWorktree` for the yes/no question;
`Get-SiblingRoot` and `Test-Setup.ps1` both read it rather than re-deriving it.

`Test-Setup.ps1` detects the worktree and names this script as the fix for every row it would
repair, so running it first is quicker than reading this.

**A committed `SessionStart` hook already runs it**, so in practice a worktree seeds itself before
the first prompt. [`.claude/settings.json`](.claude/settings.json) holds it — matcher `startup`,
exec form (`powershell.exe` plus an argument list, no shell, so nothing depends on whether Git
Bash is on PATH), script path via `${CLAUDE_PROJECT_DIR}`. It is the only thing in that file. The
hook is a convenience, not the mechanism: it is the same script with no switches, so running it by
hand stays correct, and a session that starts before the hook lands (or with hooks disabled) is
one command away from the same state. Its stdout is injected into the session's context, which is
the reason the script's normal-path output is a handful of lines rather than a log.

## Repository layout

Naming rule: **assembly name = root namespace = `SmartHome.<Area>.<Name>`**, while the folder
and the `.nfproj` file keep the short `<Name>`. So
`src/devices/RoomSensor/RoomSensor.nfproj` builds `SmartHome.Devices.RoomSensor.exe`.

**One exception, and it is not stylistic:** the unit test assembly must stay named
`NFUnitTest`. nanoFramework.TestFramework 3.0.80's device-side `UnitTestLauncher` resolves the
test assembly by that name; renaming it made `Assembly.Load` throw on the device and every test
was reported *skipped* while `vstest` still exited 0 — a green run that executed nothing, which
went unnoticed for three commits. `Run-Tests.ps1` now decides from the TRX counters and fails
when nothing ran, but don't re-break the name.

```text
src/
  common/                 Shared libraries, used by device apps and tests alike
    Homie/                SmartHome.Homie      — Homie v4 client (SmartHome.Homie.V4 inside)
    Mqtt/                 SmartHome.Mqtt       — ReconnectingMqttClient: auto-reconnect and
                            subscription replay over nanoFramework.M2Mqtt. Protocol-agnostic;
                            knows nothing about Homie
    Networking/           SmartHome.Networking — NetworkHelper, the only WiFi connect path
    Text/                 SmartHome.Text       — StringUtils
  devices/                Real device apps — the things that actually get shipped
    RoomSensor/           SmartHome.Devices.RoomSensor — temperature/humidity/pressure, BMP280
    IrrigationControl/    SmartHome.Devices.IrrigationControl
    OvenControl/          SmartHome.Devices.OvenControl
  integrationTests/       On-device end-to-end checks, one dependency each (see below)
    TestSupport/          SmartHome.IntegrationTests.TestSupport — the PASS/FAIL markers
    WifiCheck/            Connects to the configured WiFi network, nothing else
    MqttCheck/            WifiCheck's setup + round-trip pub/sub through Mosquitto (plain
                            nanoFramework.M2Mqtt, no reconnect wrapper)
    Bmp280Check/          BMP280 (Bme280 driver) sensor reads over I2C, no network
    MqttReconnectCheck/   Publishes a heartbeat through ReconnectingMqttClient while the runner
                            kills and recreates the broker under it
    HomieClientCheck/     One property of every datatype, settable and not, retained and not:
                            the conformance test's device, deliberately not a real one
  tests/
    Unit/                 SmartHome.UnitTests — nanoFramework.TestFramework, runs on hardware
tools/
  DeviceDebugMonitor/     Host-side .NET console app (NOT nanoFramework) -- CLI device debugger,
                          see scripts\Watch-DeviceDebugOutput.ps1
scripts/                  Dev-environment and agent helper scripts (see table above)
  tests/                  Host-side tests for those scripts. TestRunner.ps1 is the
                          Describe/It/Assert-* vocabulary (this repo's own, not Pester);
                          <Subject>.Tests.ps1 is one file per subject
SmartHome.sln
```

Two naming traps this layout exists to avoid, both hit for real:

- A namespace whose last segment matches a type in scope makes that type unusable. A plain
  `Device` namespace collided with the `SmartHome.Homie.V4.Device` class, and
  `SmartHome.Tests.Unit` collided with the `Unit` enum — which is why the unit tests are
  `SmartHome.UnitTests`, not `SmartHome.Tests.Unit`. Check for a same-named type before adding
  a namespace segment.
- `AssemblyName` no longer equals the project file name, so anything hunting build output must
  read `<AssemblyName>` from the project. `Get-NfProjectAssemblyName` in `Common.ps1` does
  that; `Deploy-ToDevice.ps1` and `Run-Tests.ps1` use it. Don't reintroduce
  `GetFileNameWithoutExtension($projectPath)` for that purpose.

`RoomSensor/Program.cs` is the main device logic; `HomieClient` (in `SmartHome.Homie`) is the
shared Homie client, layered on `ReconnectingMqttClient` (in `SmartHome.Mqtt`).

`ReconnectingMqttClient`'s auto-reconnect handling was long marked WIP, "blocked on an ESP32
nanoFramework target bug" — as of 2026-08-20 that is out of date: `MqttReconnectCheck` proves on
real hardware that a client keeps its heartbeat going across broker outages of 3s and 20s,
reconnecting rather than restarting. That test exercises `SmartHome.Mqtt` alone -- it does not
reference `SmartHome.Homie` at all, so it settles the transport and nothing above it. The Homie
layer is the next paragraph.

The Homie layer on top now re-announces too: `HandleConnectionOpen` takes the device back through
`init` -> `ready`, republishing every attribute, because Homie state lives in the broker's
retained store and a restarted broker has an empty one. Only reconnects reach that handler --
on first connect the connection-change handlers are registered after `ConnectInternal`, so the
initial CONNACK has already passed. Verified on hardware by destroying the broker under a running
RoomSensor: the fresh broker sees the full announcement again, not just bare sensor values.

Device apps talk to `IHomieClient` (in `SmartHome.Homie`), not to the MQTT client: `Connect()`,
`Disconnect()`, `Alert()`, `Sleep()`, `Ready()`, plus `DeviceId`/`State`/`IsConnected` and an
`OnCommand` event. It is deliberately **not** derived from `IReconnectingMqttClient` — a Homie
device owns a connection rather than being one, and exposing `Publish`/`Subscribe` there would
let an app publish an attribute non-retained or `$state` out of order. The `Device` model (built
with `HomieDeviceBuilder`) says what the device *is*; `IHomieClient` is what you *do* with it.

Five things to know when writing an actuator (Irrigation, Oven):

- Don't re-check the payload against `$datatype` or `$format` -- the property already did.
  Since 2026-08-29 `PropertyBase.Set` validates a `/set` before anything is applied: an enum
  payload must be one of the `$format` values, an integer or float inside a declared
  `min:max` range, a boolean exactly `true` or `false`, a colour a real `<r>,<g>,<b>` triple.
  A rejected payload is logged and dropped -- the value does not move, nothing is published,
  nothing lands in the retained store -- and `HomieClient` does **not** raise `OnCommand` for
  it, so a handler only ever sees payloads its property can hold. That is narrower than it
  sounds and does not replace the rule below: the library refuses what the *declaration*
  forbids, and only the app can refuse what its *state* forbids (a legal enum value that is
  an illegal transition, a valid setpoint a relay then fails to reach). Issue #39.
- Act on `IHomieClient.OnCommand`, not on `property.OnUpdate`. The property event fires both
  when a controller sets a value and when the device updates its own, and cannot tell them
  apart; `OnCommand` fires only for a controller's `/set`.
- Settable properties are subscribed on `homie/[device]/[node]/[property]/set`, as the spec
  requires. Until 2026-08-21 the code subscribed to the property *value* topic instead, so
  commands were never received and the device re-consumed its own retained publishes.
- Reflect the outcome, not the command. `HomieClient` publishes a `/set` payload onto the
  property before `OnCommand` runs, which is right for an ordinary property and wrong for one
  whose value is a *request* the device can turn down — an illegal `$state` transition, an out
  of range setpoint, a relay that failed to close. Publish the real value over it once the
  command has been applied or refused, or the retained store advertises a state the device is
  not in, to every controller that connects afterwards. **After** the reflection, never instead
  of it: the library's publish is already out by the time the handler runs, so a device that
  publishes its real value first only has it overwritten. `HomieClientCheck` does this for its
  `lifecycle` property, the conformance suite asserts the order on the wire, and
  `HomieClient_Reflects_The_Command_Before_The_Handler_Can_Correct_It` pins the same pattern in
  the unit suite, where CI can run it. Issue #33 asked whether the library should take this over
  by having `OnCommand` return accept/reject, and was closed as working-as-intended — the
  reasoning is in that issue, and worth reading before proposing it again.
- Don't block `OnCommand`. It runs on M2Mqtt's event-dispatch thread, and that one thread
  delivers every incoming PUBLISH plus SUBACK, PUBACK, UNSUBACK and the connection-closed
  signal (`MqttClient.DispatchEventThread` in the sibling `nanoFramework.m2mqtt` checkout). A
  handler that waits on hardware stalls the client's whole event stream, this device's next
  `/set` included. An outcome that takes time — a relay confirming, a setpoint validated
  against a sensor read — belongs on the device's own thread: hand the work off, return, and
  publish the real value onto the property when it lands. That is the same "reflect the
  outcome" publish, just later.

`HomieClient` owns its MQTT session and must: Homie v4 requires the connection to carry a last
will setting `homie/[device-id]/$state` to `lost`, and a will can only be declared in CONNECT.
An app that connects the transport first — as RoomSensor did until 2026-08-21 — produces a
session with no will at all, and `HomieClient` used to accept it ("MQTT client is already
connected. Continue..."), silently discarding the will, the keepalive and the credentials. So:
build the client, then call `HomieClient.Connect()`, which returns `bool` for retry loops. The
MQTT client id defaults to the device's topic id, not a random Guid, so a reconnect takes over
the dead session instead of leaving its `lost` will to fire after the new session already
announced `ready`.

Anything that needs a broker connection that survives the broker going away uses
`ReconnectingMqttClient` from `src/common/Mqtt`. It was called `HomieMqttClient` and lived in the
Homie library until 2026-08-21, but it never contained anything Homie-specific — it caches the
connect parameters, retries on `ConnectionClosed`, and replays cached subscriptions, all in terms
of plain MQTT. The Homie-specific publishing lives in `HomiePublishExtensions` (in
`SmartHome.Homie`), which extends M2Mqtt's own `IMqttClient` and never referenced the wrapper.
The dependency runs one way only: `SmartHome.Homie` -> `SmartHome.Mqtt`. Don't add a reference
back, and don't put topic or `$state` knowledge into the client.

Anything that needs WiFi calls `NetworkHelper.ConnectToConfiguredNetwork()` from
`src/common/Networking`. Don't reintroduce the hand-rolled scan/connect loop from the official
`BasicExample.WiFi` sample: it races the ESP32's own auto-connect and fails intermittently with
`WifiConnectionStatus.UnspecifiedFailure` (error 5). RoomSensor carried that loop until
2026-08-20 and would not join the network on a clean boot; `WifiNetworkHelper.Reconnect()` waits
for the interface instead of racing it.

### Four kinds of test, deliberately kept apart

- **`src/tests`** — unit tests (`SmartHome.UnitTests`) driven by `vstest.console` and the nanoFramework
  test adapter. They run *on* hardware but test logic, not the physical environment.
- **`src/integrationTests`** — one app per external dependency (WiFi, broker, sensor). Each is a
  full device app that boots, exercises exactly one concern, and reports a verdict. Run them via
  `scripts\Run-IntegrationTests.ps1`, not by deploying them by hand.
- **`scripts/tests`** — the host-side half: the PowerShell that decides what the integration
  suite reports. No device, no broker, no network, nothing installed. Run them via
  `scripts\Run-ScriptTests.ps1`, which CI runs too — the only automated coverage this repository
  has of the integration tooling itself, since the nanoFramework unit tests cannot reach a
  PowerShell function. **Any change under `scripts\` should come with a case here** unless it
  genuinely needs a device to exercise; before this existed, proving one meant a throwaway
  harness that was deleted afterwards, four times in a week (issue #74).
- **`src/devices`** — real applications. Nothing here should exist only to prove a dependency
  works; that's what `integrationTests` is for.

`Run-IntegrationTests.ps1` is dot-sourceable for that reason: a guard near the bottom means a
dot-source defines `$testCatalog` and the functions and runs nothing, so a test file reaches the
shipped source directly. Keep everything above that guard a declaration — a `Test-Path` or an
`Import-SmartHomeLocalEnv` drifting back to the top would run on every dot-source, and
`RunIntegrationTests.Tests.ps1` asserts against exactly that.

Those tests differ in which dependency they exercise, as above, and separately in *who decides
the verdict*. The latter is declared per entry in `$testCatalog` in `Run-IntegrationTests.ps1`,
by two capability fields rather than by a kind name: `Verdict` names the function that returns
the outcome, and `OwnsBroker` says that function stops and starts the broker itself — which is
what `-NoBroker` refuses. An entry that names no `Verdict` is device-decided. The run loop reads
both generically (one invoke by name, one shared epilogue), so a fourth kind of check is a new
function plus its catalog entry, with no branch or guard elsewhere to keep in step. The three
that exist are still worth naming (a different axis from the locations above):

- **`DeviceMarker`** — the device decides. The runner captures managed debug output and reads
  the test's own `[ITEST]` marker. WifiCheck, MqttCheck and Bmp280Check work this way, and
  their entries name no `Verdict`.
- **`HomieConformance`** — the host measures a purpose-built device against the Homie v4
  convention: mandatory attributes and their retained flags, one property of every datatype with
  its `$format`/`$unit`/`$settable`/`$retained`, a `/set` command applied and reflected back, a
  payload each datatype's own `$format` forbids that must be refused rather than applied, the
  `alert` and `sleeping` states driven through a control property, a refused transition that
  must not be advertised as if it had happened, and a full re-announce after the broker is
  replaced. `HomieClientCheck` is this kind (`Verdict = 'Invoke-HomieConformanceCheck'`).
  Retained-ness is read from a *fresh* subscriber (`mosquitto_sub -F '%t %r %p'`), because MQTT
  only sets the retain flag when replaying from the store — a live retained publish arrives with
  the flag clear.

  The refused transition is asserted **on the wire, about the device**, not on where the
  retained store settled — issue #36 closed on that distinction. A settled per-topic read
  can be flipped by a QoS-1 retransmission the broker re-processes ([MQTT 3.1.1
  §3.3.1.1](https://docs.oasis-open.org/mqtt/mqtt/v3.1.1/os/mqtt-v3.1.1-os.html#_Toc398718038):
  a receiver "cannot assume that it has seen an earlier copy" of a DUP packet), which is a
  true statement about the store and a false one about a device that reflected and corrected
  exactly as required — and the runner cannot tell it apart from a device that genuinely
  republished the refused value. So the verdict comes from the ordered payloads (the
  reflection, then the correction over it, and `$state` never carrying the forbidden value),
  and a store that disagrees with them is reported as a warning carrying the observed
  sequence. Same call as `2af2b12`: don't name a defect the window cannot distinguish.
- **`BrokerOutage`** — the host decides. The device publishes a heartbeat and subscribes to an
  echo topic; the runner takes the broker away, brings a fresh one up, and asserts heartbeats
  reappear on `homie/#` with a *higher* counter than before the outage. A lower counter means the
  app restarted rather than reconnected, which is reported as `RESTARTED`, not `PASS`. It then
  publishes a nonce to the echo command topic and requires it back — because heartbeats alone
  only prove the *connection* returned. Publishing resumes the moment the socket is up, so a
  reconnect that replayed no subscriptions (a live client subscribed to nothing, which is what a
  throw out of `ResubscribeCachedTopics` used to leave behind) is indistinguishable from a
  healthy one until something is sent *to* the device. `MqttReconnectCheck` is this kind
  (`Verdict = 'Invoke-BrokerOutageCheck'`), and it deliberately emits no `[ITEST]` marker — a
  device claiming it reconnected would be a second, weaker verdict competing with the evidence at
  the broker. See the `smarthome-mqtt-reconnect` skill.

DeviceMarker tests report by writing a marker line to managed debug output:

```text
[ITEST] <AssemblyName> PASS: <detail>
[ITEST] <AssemblyName> FAIL: <reason>
```

`IntegrationTest.Pass/Fail` (in `src\integrationTests\TestSupport\IntegrationTest.cs`) emits
these, and `Run-IntegrationTests.ps1` parses them. A device app never exits with a status code —
these markers *are* the exit code. Emit one as soon as the outcome is known, before any idle
loop.

**The name in the marker is nobody's to spell.** Both ends derive it from the project's
`<AssemblyName>`: the device reads its own running assembly (`typeof(Program)` handed to
`IntegrationTest.Pass/Fail`, which is why they take a `Type` and not a string), and the runner
reads the same element off the `.nfproj` it just flashed, in `Get-IntegrationTestMarkerName`,
during the pre-flight. Until 2026-09-02 the runner compared the catalog key against a
per-project `TestName` const — a third independent spelling of one name that nothing in the
build reconciled, and whose drift surfaced as a `WRONG-TEST` verdict 90 seconds into a hardware
run, in the same shape as a stale deploy (issue #20). `WRONG-TEST` still exists and now means
only that: the app answering is not the one just flashed — either because the flash failed, or
because it succeeded but left the old assembly where the CLR still finds it (Clearing the
deployment area above, which is what #46 closed). The type has to come from the caller
rather than `Assembly.GetExecutingAssembly()` because `IntegrationTest` ships both ways — as a
`TestSupport` reference and as a linked source file — and would otherwise name `TestSupport`
for some tests and the test itself for others.

Adding a new integration test means: new project under `src\integrationTests`, emit the
marker, add it to `$testCatalog` in `Run-IntegrationTests.ps1`. A host-decided test emits no
marker and instead adds a verdict function, names it in its entry's `Verdict`, and lists that
function's required settings under the same name in `$requiredCatalogKeys` — which doubles as the
list of names an entry may point at. That the name resolves to a function that actually exists is
a separate `Get-Command` check in the pre-flight, which is where it has to be: the functions are
defined further down the script than the catalog validation runs.

`Bmp280Check` links `IntegrationTest.cs` as a shared source file instead of referencing
`TestSupport` as a project. That kept the WiFi/networking assemblies out of a deliberately
network-free test; `TestSupport` is mscorlib-only now that `NetworkHelper` moved to
`src/common/Networking`, so the link is no longer strictly required — but it still avoids an
assembly on the device for one static class.

The suite flashes each test in turn, so it **leaves the last one on the device** — redeploy
RoomSensor (`Deploy-ToDevice.ps1`) when the device should go back to doing its real job.

## Companion nanoFramework repos

Cloned beside `SmartHome` by `Sync-NanoFrameworkRepos.ps1` (see the debugging section above —
sync these *before* debugging, not after getting stuck), kept on the branch configured in
`scripts\nanoFramework.local.env.ps1`. Check here before assuming something isn't possible in
nanoFramework.

| Repository | Why it matters here |
|---|---|
| `nanoframework/Home` | Canonical repo index, contribution guidance, org map |
| `nanoframework/nanoframework.github.io` | Source for official docs site and tutorials |
| `nanoframework/Samples` | Working examples for APIs and device scenarios |
| `nanoframework/nf-interpreter` | Firmware, native/runtime internals, target definitions |
| `nanoframework/CoreLibrary` | Source for `nanoFramework.CoreLibrary` (mscorlib) — **not** part of nf-interpreter, a separate repo, only referenced from it as an external test dependency |
| `nanoframework/nf-debugger` | Source for `nanoFramework.Tools.Debugger.Net` — the library VS's debugger extension (and this repo's own `tools\DeviceDebugMonitor`) is built on |
| `nanoframework/nanoFramework.WebServer` | AI-agent Skills Discovery and MCP examples/docs |
| `nanoframework/nanoFramework.IoT.Device` | Sensor/device bindings and sample patterns |
| `nanoframework/nanoFramework.Hardware.Esp32` | ESP32-specific managed APIs used by this repo |
| `nanoframework/nanoFramework.Logging` | Logging implementation used by this repo |
| `nanoframework/nanoFramework.m2mqtt` | MQTT stack used by this repo |
| `nanoframework/nanoFirmwareFlasher` | Firmware flashing/update tooling |
| `nanoframework/nf-Visual-Studio-extension` | VS integration behavior and build/deploy tooling |
| `nanoframework/nf-tools` | Supporting nanoFramework CLI/build utilities |

**Lookup order** when answering a nanoFramework question: sibling `nanoFramework.WebServer`
(Skills/MCP) → `Samples` → `nanoFramework.IoT.Device` (sensor drivers) →
`nanoframework.github.io` (docs) → `nf-interpreter` (firmware/runtime) → `CoreLibrary`
(mscorlib source — a separate repo from `nf-interpreter`, easy to miss) → `Home` (repo index).
Fall back to web search only after those.

### Version-alignment policy

This repo references package baselines such as `nanoFramework.CoreLibrary` `1.17.11`,
`nanoFramework.Hardware.Esp32` `1.6.42`, `nanoFramework.Logging` `1.1.161`,
`nanoFramework.M2Mqtt` `5.1.221`.

Companion repos default to the branch configured in `scripts\nanoFramework.local.env.ps1`
(`main`) — except `CoreLibrary`, `nanoFramework.Hardware.Esp32`, `nanoFramework.Logging`,
`nanoFramework.m2mqtt`, and `nf-interpreter`, which are pinned to the git tag/commit matching
the baselines above (detached HEAD — read-only reference checkouts, not branches to commit
against). `Sync-NanoFrameworkRepos.ps1` skips detached-HEAD repos rather than resetting them;
`-Force` overrides that, and then the pins need re-applying (the `nanoframework-sync` subagent
knows how).

These baselines drift from what's actually in `packages.config` and the flashed firmware over
time — treat the list above as "as of the last doc update", not live truth. Check
`packages.config` directly, and `nanoff --devicedetails` for the firmware actually on a given
device.

## On-device Skills/MCP (forward-looking)

`nanoFramework.WebServer.Skills`/`.Mcp` let a device expose agent-discoverable diagnostics and
actuator control directly. Not built yet anywhere in this repo. If asked to add it, follow those
patterns rather than inventing a custom protocol:

- **Skills Discovery** — `[Skill(...)]` + `[SkillAction(...)]`, registered with
  `SkillRegistry.DiscoverSkills(...)`, hosted by `SkillDiscoveryController`. Discover via
  `GET /.well-known/agent-card.json`, invoke via `POST /skills/invoke`.
- **MCP** — `[McpServerTool(...)]` and optionally `[McpServerPrompt(...)]`, registered with
  `McpToolRegistry.DiscoverTools(...)` / `McpPromptRegistry.DiscoverPrompts(...)`, hosted by
  `McpServerController`. Discover/invoke via `POST /mcp`.

Likely first candidates here: RoomSensor current readings, Irrigation/Oven commands and state.

## Current RoomSensor facts

| Fact | Value |
|---|---|
| Device Homie ID | `room-sensor-office` |
| MQTT broker in code | `192.168.1.238` in `Program.cs` (verified against the source, 2026-08-20) |
| Sensor node | `sensor` |
| Sensor type | `BMP280` |
| Properties | `temperature`, `humidity`, `pressure` |
| Update interval | `5000 ms` |

Note that `MqttCheck` hardcodes its own broker (`192.168.1.238`) separately — these two constants
drift apart easily, and a stale one is the usual reason a healthy device "can't reach the
broker". `Run-IntegrationTests.ps1` warns when `MqttCheck`'s constant isn't an address of the
host machine.

## Open work

**GitHub issues are the backlog.** There is no to-do file in the repository — `NEXT-STEPS.md`
was one, and keeping it alongside issues meant two places to look and neither trusted. Check
the issue list before starting anything:

```bash
gh issue list --state open
```

Issues are labelled `type:` (bug/feature/task/spike), `area:` (homie/infra/sensor) and
`status:` (in-progress/blocked/review). Anything `status: blocked` names in its body exactly
what it is waiting for, so it can be picked up cold.

Those labels classify but do not rank — they cannot choose between eleven `type: task` issues.
When the question is *what to work on next* rather than *what exists*, use
`scripts\Get-BacklogPriorities.ps1` (skill: `smarthome-prioritize`), which scores the backlog
on the axes the labels leave out and then re-weights for whether the device is reachable, how
much time there is, and which cluster to work in. `-Handoff <n>` closes the loop by printing
the top few as spin-off pointers — blocked and closed rows skipped, the ones needing hardware
to verify marked so the confirm-before-device-scripts rule reaches the session that picks them
up.

Two of its axes look alike and are not. `Where` says where the *edit* lands; `VerifyNeeds` says
what *proving* it takes, and the device-availability weighting — the largest multiplier in the
script — keys off the second. They disagree for every host-side tool that only a real suite run
can exercise: `Run-IntegrationTests.ps1` and the capture helpers are edited at a desk and
verified on hardware. `VerifyNeeds` is read from the issue title first and from the body only
as a fallback, because every body in this repo mentions the device; a body-derived call is
reported at confidence `Low`, marked `?` in the ranking table, and is the one to check before
trusting a rank. An `-Overrides` file that sets `Where` alone now warns, because before the
split that key was how a row got moved and it no longer is.

### File what you find

**Anything found and verified that falls outside the change in hand gets a GitHub issue,
before the session ends.** Not a note in a pull request body, not a line in a summary the
user has to act on, not a TODO in the code — an issue, because the issue list is the backlog
and everything else is a place findings go to be forgotten. This is standing behaviour for
every session, not something to be asked for.

Three things make it work, and skipping any one of them makes it worse than not doing it:

- **Verified is the bar.** Reproduce it, or read the source that proves it, before filing.
  A hunch filed as an issue costs the next session a full investigation to disprove and
  teaches everyone to distrust the list. If investigation refutes the finding, that is a
  result too — say so plainly and file nothing.
- **Retract what you already said.** A finding reported somewhere before it was checked — a
  pull request body, a review comment, a message to the user — has to be corrected in that
  same place once it turns out to be wrong. A wrong claim left standing in a merged PR is
  indistinguishable from a real one later.
- **File it, don't fix it.** Widening the current change to cover what you tripped over is how
  a reviewable diff becomes an unreviewable one. Write the issue so it can be picked up cold:
  what is wrong, the evidence, the files, and what closing it would look like.

Check `gh issue list --state all` first — a duplicate is worse than nothing, and a closed issue
counts as much as an open one: #33 was closed as working-as-intended and #16 as not-planned, so
searching only the open list re-files decisions this repo has already made. Label to match the
rest of the list, and link the pull request or issue that turned the finding up. The label names
contain the space after the colon — `type: bug`, not `type:bug`, which `gh` rejects with
`'type:bug' not found` and then creates nothing:

```bash
gh issue create --title "..." --body "..." --label "type: bug" --label "area: infra"
```

Then say which issues you filed. What must not live in a summary is the *finding* — the issue
numbers themselves belong in the session's closing message, so the user can see what was opened
in a public tracker without going to look for it.

Worth filing: a defect confirmed by reproduction or by reading the source; a constant or
assumption the code asserts without evidence; a residual gap in something just shipped, where
the mitigation is "remember to do X"; work deliberately cut from a change to keep it
reviewable.

Not worth filing: style preferences; refactors with no stated payoff; anything already covered
by an open issue, settled by a closed one, or already documented in this file as intentional;
and anything you have not actually checked.

[`CONTRIBUTING.md`](CONTRIBUTING.md) carries the same rule in short form — it is the source of
truth for process, so change both or neither.

## Development process

[`CONTRIBUTING.md`](CONTRIBUTING.md) is the source of truth for how work moves: trunk-based
branching off a protected `main`, Conventional Commits enforced on PR titles only, and what CI
can and cannot verify.

The part worth knowing before you touch anything: **CI runs the host-side script tests, builds
every project and runs the unit tests on the nanoclr virtual device, but it cannot run the
integration suite** — that needs a real ESP32, a real network and a real broker. Hardware
verification is a manual step, and the pull request should say what was run on hardware, or say
plainly that nothing was.

A green CI is therefore not the same claim for every change. For a change under `scripts\`,
`Run-ScriptTests.ps1` covers the desk-provable half and nothing else: the capture, the broker
outage and the conformance verdicts are still only proved by a run on the device.

## Project skills

`.claude/skills/smarthome-*` each wrap one script from the table above with the context/safety
notes specific to that workflow — see the table for which skill goes with which script.

## Response style

The `caveman` skill gives ultra-compressed, technically-precise responses. It auto-triggers on
cues like "less tokens" / "be brief" / `/caveman`, or invoke it explicitly. Code, commits, and PR
text stay in normal style regardless. It is installed **locally on this machine** (the caveman
Claude Code plugin), deliberately not vendored into this repo — the repo previously carried
copies under `.claude/skills/`, `.agents/`, `agent/`, `.clinerules/`, `.cursor/`, `.windsurf/`,
and `.opencode/`, all of which were removed as duplicate installs. Don't re-vendor them. The
same plugin also supplies the engineering-discipline skills (`investigate-first`, `lean-build`,
`safe-refactor`, `surgical-patch`, `verify-and-stop`, `migration`, `cavecrew`) — reach for
whichever matches the shape of the task.

## GitHub integration

[`.github/workflows/claude.yml`](.github/workflows/claude.yml) is on-demand only: comment
`@claude implement this` on an issue and it opens a PR; comment `@claude review this` on a PR
and it reviews. No auto-trigger on PR open or push — deliberate for this single-developer repo,
not an always-on reviewer.

The workflow file is in the repo but **inert until you complete the one-time setup**, which
needs your own GitHub admin action (I can't do this for you):
1. Install the Claude GitHub App: https://github.com/apps/claude
2. Run `claude setup-token` locally to generate a subscription OAuth token
3. Add it as repo secret `CLAUDE_CODE_OAUTH_TOKEN`

Locally, `/code-review` works today with no setup — reviews your branch's diff in-session,
`--fix` applies findings, `--comment` posts them to a PR (that last one goes through `gh`,
which First-time setup requires anyway).

## Subagents

Defined in `.claude/agents/` and committed — available in every session on this repo, not tied
to any one conversation. Nothing forces a session to dispatch to them, though; picking one over
working inline is a per-session judgment call, steered by their `description` and this file.

- **device-ops** — build/deploy/test/mosquitto-observe workflows through the scripts above.
  Always confirms before anything hardware-touching.
- **nanoframework-sync** — sibling-repo sync and package-version alignment against the
  companion nanoFramework repos. Reach for it specifically when a lookup turns into more than a
  couple of exploratory `git tag`/`git log`/WebFetch calls (e.g. matching several repos to exact
  release tags) — that kind of investigation is worth keeping out of the main conversation. For
  a plain sync, the `smarthome-sync-nanoframework` skill (just runs the script) is enough.
