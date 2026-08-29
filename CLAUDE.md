# SmartHome — Claude Code guidance

**nanoFramework** solution for **ESP32** home-automation devices, speaking the **Homie v4** MQTT
convention (topic prefix `homie/`). Primary device today: **RoomSensor**.

This file is the single source of truth for how to work in this repo — layout, scripts,
companion repos, version policy. (It replaced `.github/copilot-instructions.md` and the root
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
| `scripts\Deploy-ToDevice.ps1 [-Project <path>] [-Configuration Debug\|Release]` | Always `/t:Rebuild`s (a plain incremental build silently drops the deployment `.bin`) then flashes via `nanoff` | **Yes** | `smarthome-deploy` |
| `scripts\Run-Tests.ps1` | Builds `SmartHome.UnitTests` and runs it via `vstest.console` + the nanoFramework test adapter | **Yes** | `smarthome-test` |
| `scripts\Run-IntegrationTests.ps1 [-Tests <names>] [-NoBroker]` | The whole `src\integrationTests` suite in one call: broker up, deploy + capture + verdict per test, broker down, summary + exit code | **Yes** | `smarthome-integration-tests` |
| `scripts\Sync-NanoFrameworkRepos.ps1 [-Force]` | Clones/updates the sibling nanoFramework repos beside `SmartHome` | No | `smarthome-sync-nanoframework` |
| `scripts\Restore-Packages.ps1` | Restores classic `packages.config` NuGet packages from the local NuGet cache — `msbuild /t:Restore` is a no-op for this repo's project style | No | `smarthome-restore-packages` |
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

## First-time setup

```powershell
Copy-Item scripts\local.env.template.ps1 scripts\local.env.ps1
Copy-Item scripts\nanoFramework.local.env.template.ps1 scripts\nanoFramework.local.env.ps1
```

Then fill in:

- `scripts\local.env.ps1` — `SMARTHOME_COM_PORT`, `SMARTHOME_MOSQUITTO_DIR`, optionally
  `SMARTHOME_MQTT_BROKER` / `SMARTHOME_MQTT_PORT`
- `scripts\nanoFramework.local.env.ps1` — `SMARTHOME_NANOFW_BRANCH`

Both `local.env.ps1` files are git-ignored — never commit them.

Required local tools:

- [nanoFramework VS extension](https://marketplace.visualstudio.com/items?itemName=nanoframework.nanoFramework-VS2022-Extension)
- `nanoff` CLI: `dotnet tool install -g nanoff`
- [Mosquitto](https://mosquitto.org/download/)
- Git on PATH
- [GitHub CLI](https://cli.github.com/) (`gh`), **authenticated** — `gh auth login`, then
  confirm with `gh auth status`. Not optional and not only for opening pull requests: the
  backlog *is* GitHub issues (see Open work below), so `gh issue list` is how you find out
  what there is to do. A human can fall back to the issues page in a browser; an agent
  session has no such fallback, and every workflow in this file that reaches GitHub goes
  through `gh`.

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

Four things to know when writing an actuator (Irrigation, Oven):

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

### Three kinds of test, deliberately kept apart

- **`src/tests`** — unit tests (`SmartHome.UnitTests`) driven by `vstest.console` and the nanoFramework
  test adapter. They run *on* hardware but test logic, not the physical environment.
- **`src/integrationTests`** — one app per external dependency (WiFi, broker, sensor). Each is a
  full device app that boots, exercises exactly one concern, and reports a verdict. Run them via
  `scripts\Run-IntegrationTests.ps1`, not by deploying them by hand.
- **`src/devices`** — real applications. Nothing here should exist only to prove a dependency
  works; that's what `integrationTests` is for.

Those tests differ in which dependency they exercise, as above, and separately in *who decides
the verdict*. The latter is declared per entry by the `Kind` field in `$testCatalog` in
`Run-IntegrationTests.ps1`, and there are three (not the same three as the locations above):

- **`DeviceMarker`** — the device decides. The runner captures managed debug output and reads
  the test's own `[ITEST]` marker. WifiCheck, MqttCheck and Bmp280Check work this way.
- **`HomieConformance`** — the host measures a purpose-built device against the Homie v4
  convention: mandatory attributes and their retained flags, one property of every datatype with
  its `$format`/`$unit`/`$settable`/`$retained`, a `/set` command applied and reflected back, the
  `alert` and `sleeping` states driven through a control property, a refused transition that
  must not be advertised as if it had happened, and a full re-announce after the broker is
  replaced. `HomieClientCheck` is this kind. Retained-ness is read from a *fresh*
  subscriber (`mosquitto_sub -F '%t %r %p'`), because MQTT only sets the retain flag when
  replaying from the store — a live retained publish arrives with the flag clear.
- **`BrokerOutage`** — the host decides. The device publishes a heartbeat and subscribes to an
  echo topic; the runner takes the broker away, brings a fresh one up, and asserts heartbeats
  reappear on `homie/#` with a *higher* counter than before the outage. A lower counter means the
  app restarted rather than reconnected, which is reported as `RESTARTED`, not `PASS`. It then
  publishes a nonce to the echo command topic and requires it back — because heartbeats alone
  only prove the *connection* returned. Publishing resumes the moment the socket is up, so a
  reconnect that replayed no subscriptions (a live client subscribed to nothing, which is what a
  throw out of `ResubscribeCachedTopics` used to leave behind) is indistinguishable from a
  healthy one until something is sent *to* the device. `MqttReconnectCheck` is this kind, and it
  deliberately emits no `[ITEST]` marker — a device claiming it reconnected would be a second,
  weaker verdict competing with the evidence at the broker. See the `smarthome-mqtt-reconnect`
  skill.

DeviceMarker tests report by writing a marker line to managed debug output:

```text
[ITEST] <TestName> PASS: <detail>
[ITEST] <TestName> FAIL: <reason>
```

`IntegrationTest.Pass/Fail` (in `src\integrationTests\TestSupport\IntegrationTest.cs`) emits
these, and `Run-IntegrationTests.ps1` parses them. A device app never exits with a status code —
these markers *are* the exit code. Emit one as soon as the outcome is known, before any idle
loop. Adding a new integration test means: new project under `src\integrationTests`, emit the
marker, add it to `$testCatalog` in `Run-IntegrationTests.ps1`.

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

Check `gh issue list --state open` first — a duplicate is worse than nothing. Label with
`type:` and `area:` to match the rest of the list, and link the pull request or issue that
turned the finding up.

Worth filing: a defect confirmed by reproduction or by reading the source; a constant or
assumption the code asserts without evidence; a residual gap in something just shipped, where
the mitigation is "remember to do X"; work deliberately cut from a change to keep it
reviewable.

Not worth filing: style preferences; refactors with no stated payoff; anything already covered
by an open issue or already documented in this file as intentional; and anything you have not
actually checked.

## Development process

[`CONTRIBUTING.md`](CONTRIBUTING.md) is the source of truth for how work moves: trunk-based
branching off a protected `main`, Conventional Commits enforced on PR titles only, and what CI
can and cannot verify.

The part worth knowing before you touch anything: **CI builds every project and runs the unit
tests on the nanoclr virtual device, but it cannot run the integration suite** — that needs a
real ESP32, a real network and a real broker. Hardware verification is a manual step, and the
pull request should say what was run on hardware, or say plainly that nothing was.

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
