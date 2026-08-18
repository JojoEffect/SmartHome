# SmartHome — Copilot Agent Instructions

This is a **nanoFramework** project targeting **ESP32** microcontrollers. Devices communicate over WiFi using the **Homie v4 MQTT convention** (topic prefix `homie/`). The default device in this repo is **RoomSensor**.

The local agent workflow is built around:
- **Mosquitto** for local MQTT observation
- **nanoff** for CLI deploy/flash
- **Visual Studio** for deploy/debug when needed
- **sibling nanoFramework repositories** checked out beside `SmartHome` for source, docs, tools, and examples

---

## Repository layout

```text
src/
  common/HomieNano/       # Shared Homie v4 client library (nanoFramework)
  devices/
    RoomSensor/           # Primary device: temperature/humidity/pressure via BMP280
    IrrigationControl/
    OvenControl/
    Test/
  tests/
    NFUnitTest/
    TestRunner/
Utils/                    # Shared utilities
scripts/                  # Dev-environment and agent helper scripts
SmartHome.sln
```

---

## First-time local setup

1. Copy the machine-local config files:
   ```powershell
   Copy-Item scripts\local.env.template.ps1 scripts\local.env.ps1
   Copy-Item scripts\nanoFramework.local.env.template.ps1 scripts\nanoFramework.local.env.ps1
   ```

2. Edit `scripts\local.env.ps1`:
   - `SMARTHOME_COM_PORT`
   - `SMARTHOME_MOSQUITTO_DIR`
   - optional `SMARTHOME_MQTT_BROKER`
   - optional `SMARTHOME_MQTT_PORT`

3. Edit `scripts\nanoFramework.local.env.ps1`:
   - `SMARTHOME_NANOFW_BRANCH`

Both files are git-ignored and must never be committed.

### Required local tools

- [nanoFramework VS extension](https://marketplace.visualstudio.com/items?itemName=nanoframework.nanoFramework-VS2022-Extension)
- `nanoff` CLI: `dotnet tool install -g nanoff`
- [Mosquitto](https://mosquitto.org/download/)
- Git on PATH

---

## Default agent workflow

### 1. Bootstrap the local workspace

```powershell
.\scripts\Start-AgentWorkspace.ps1
```

This does two things:
1. syncs the relevant nanoFramework repositories beside `SmartHome`
2. starts Mosquitto and subscribes to `homie/#`

If you only want MQTT observation:

```powershell
.\scripts\Start-DevEnv.ps1
```

If you only want the sibling repos refreshed:

```powershell
.\scripts\Sync-NanoFrameworkRepos.ps1
```

### 2. Edit code

- Main device logic: `src\devices\RoomSensor\Program.cs`
- Device constants: `src\devices\RoomSensor\Constants.cs`
- Shared Homie client: `src\common\HomieNano\`

### 3. Deploy to the ESP32

```powershell
.\scripts\Deploy-ToDevice.ps1
```

This builds `RoomSensor.nfproj` and flashes it via `nanoff` using the COM port from `local.env.ps1`.

**Alternative — Visual Studio:**  
Open `SmartHome.sln`, set `RoomSensor` as startup project, configure the COM port in project properties, then use **F5** or **Build → Deploy**.

### 4. Observe device output

Watch the `mosquitto_sub` stream from the bootstrap terminal.

Expected topics:

```text
homie/room-sensor-office/$homie
homie/room-sensor-office/$state
homie/room-sensor-office/sensor/temperature
homie/room-sensor-office/sensor/humidity
homie/room-sensor-office/sensor/pressure
```

---

## nanoFramework companion repositories

These should be cloned **beside** `SmartHome` and kept up to date by `.\scripts\Sync-NanoFrameworkRepos.ps1`.

### Core set for this repo

| Repository | Why it matters here |
|---|---|
| `nanoframework/Home` | Canonical repo index, contribution guidance, org map |
| `nanoframework/nanoframework.github.io` | Source for official docs site and tutorials |
| `nanoframework/Samples` | Working examples for APIs and device scenarios |
| `nanoframework/nf-interpreter` | Firmware, native/runtime internals, target definitions |
| `nanoframework/nanoFramework.WebServer` | AI-agent Skills Discovery and MCP examples/docs |
| `nanoframework/nanoFramework.IoT.Device` | Sensor/device bindings and sample patterns |
| `nanoframework/nanoFramework.Hardware.Esp32` | ESP32-specific managed APIs used by this repo |
| `nanoframework/nanoFramework.Logging` | Logging implementation used by this repo |
| `nanoframework/nanoFramework.m2mqtt` | MQTT stack used by this repo |
| `nanoframework/nanoFirmwareFlasher` | Firmware flashing/update tooling |
| `nanoframework/nf-Visual-Studio-extension` | VS integration behavior and build/deploy tooling |
| `nanoframework/nf-tools` | Supporting nanoFramework CLI/build utilities |

### Version-alignment policy

- This repo currently references package baselines such as:
  - `nanoFramework.CoreLibrary` `1.17.11`
  - `nanoFramework.Hardware.Esp32` `1.6.37`
  - `nanoFramework.Logging` `1.1.161`
  - `nanoFramework.M2Mqtt` `5.1.206`
- Companion repos should default to the configured branch in `scripts\nanoFramework.local.env.ps1`.
- If you adopt a tagged release workflow later, update the sync script/config to target matching release branches or tags.

---

## Skills Discovery and MCP integration

The `nanoFramework.WebServer` project adds:
- `nanoFramework.WebServer`
- `nanoFramework.WebServer.FileSystem`
- `nanoFramework.WebServer.Mcp`
- `nanoFramework.WebServer.Skills`

For embedded AI-agent integration, the important pieces are:

### Skills Discovery

- Define skills with `[Skill(...)]`
- Add actions with `[SkillAction(...)]`
- Register them with `SkillRegistry.DiscoverSkills(...)`
- Host `SkillDiscoveryController`
- Discover with:
  - `GET /.well-known/agent-card.json`
  - `POST /skills/invoke`

### MCP

- Define tools with `[McpServerTool(...)]`
- Optionally define prompts with `[McpServerPrompt(...)]`
- Register them with `McpToolRegistry.DiscoverTools(...)` and `McpPromptRegistry.DiscoverPrompts(...)`
- Host `McpServerController`
- Discover/invoke with `POST /mcp`

### When to use it in SmartHome

Use `nanoFramework.WebServer.Skills` or `.Mcp` when a device should expose:
- agent-discoverable diagnostics
- direct actuator operations
- structured sensor reads
- on-device workflows callable by higher-level automation

For this repo, likely first candidates are:
- Room sensor current readings
- Irrigation control commands/state
- Oven control commands/state

---

## Documentation and example lookup order

When working in this repo, prefer looking here first:

1. sibling checkout `nanoFramework.WebServer` for Skills/MCP code and docs
2. sibling checkout `Samples` for working examples
3. sibling checkout `nanoFramework.IoT.Device` for sensor-driver patterns
4. sibling checkout `nanoframework.github.io` for official docs/tutorials
5. sibling checkout `nf-interpreter` for target/runtime/firmware details
6. sibling checkout `Home` for repo discovery and contribution guidance

---

## Repetitive work that must stay scripted

Prefer scripts under `scripts\` over ad-hoc terminal sequences for:
- syncing supporting nanoFramework repositories
- starting the MQTT dev environment
- full local session bootstrap
- device build/deploy

Current script set:

| Script | Purpose |
|---|---|
| `scripts\Common.ps1` | Shared env/MSBuild/git helper functions |
| `scripts\local.env.template.ps1` | Machine-local SmartHome config template |
| `scripts\nanoFramework.local.env.template.ps1` | Companion repo sync config template |
| `scripts\Sync-NanoFrameworkRepos.ps1` | Clone/update the companion nanoFramework repos beside `SmartHome` |
| `scripts\Start-AgentWorkspace.ps1` | Sync companion repos, then start Mosquitto + subscribe to `homie/#` |
| `scripts\Start-DevEnv.ps1` | Start Mosquitto + subscribe to `homie/#` |
| `scripts\Deploy-ToDevice.ps1` | Build and flash a target nanoFramework project |

---

## Current RoomSensor facts

| Fact | Value |
|---|---|
| Device Homie ID | `room-sensor-office` |
| MQTT broker in code | `192.168.1.240` in `Program.cs` |
| Sensor node | `sensor` |
| Sensor type | `BMP280` |
| Properties | `temperature`, `humidity`, `pressure` |
| Update interval | `5000 ms` |

---

## Agent expectations

- Start with `.\scripts\Start-AgentWorkspace.ps1` for a fresh local session.
- Keep sibling repos current before deep framework/library work.
- Put repeatable workflows into scripts instead of repeating manual steps.
- When adding on-device agent integration, follow `nanoFramework.WebServer` Skills/MCP patterns rather than inventing a custom protocol.

## Suggested next repetitive scripts

If this repo grows more agent-driven device work, good candidates for future scripts are:
- firmware-version check and update helpers
- RoomSensor skills/MCP sample scaffolding
- test-runner deploy shortcuts
- package-version/report generation against sibling nanoFramework repos
