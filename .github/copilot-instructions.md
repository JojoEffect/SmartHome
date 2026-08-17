# SmartHome — Copilot Agent Instructions

This is a **nanoFramework** project targeting **ESP32** microcontrollers. Devices communicate over WiFi using the **Homie v4 MQTT convention** (topic prefix `homie/`). The broker used during development is a local **Mosquitto** instance.

---

## Repository layout

```
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
scripts/                  # Dev-environment helper scripts (see below)
SmartHome.sln
```

---

## Dev environment setup (first time)

1. **Copy the config template** and fill in your machine values:
   ```powershell
   Copy-Item scripts\local.env.template.ps1 scripts\local.env.ps1
   # Edit local.env.ps1 — set COM port and Mosquitto directory
   ```
   `local.env.ps1` is git-ignored and will never be committed.

2. **Required tools:**
   - [nanoFramework VS extension](https://marketplace.visualstudio.com/items?itemName=nanoframework.nanoFramework-VS2022-Extension)
   - `nanoff` CLI: `dotnet tool install -g nanoff`
   - [Mosquitto](https://mosquitto.org/download/) — default install at `C:\Program Files\mosquitto`

---

## Iteration loop (agentic workflow)

### 1. Start the MQTT dev environment
```powershell
.\scripts\Start-DevEnv.ps1
```
This starts Mosquitto on localhost:1883, then opens `mosquitto_sub homie/#` so you can observe all device announcements and property updates in real time. Leave this running in a terminal. Stop with `Ctrl+C` (it also shuts down Mosquitto).

### 2. Edit code
- Main device logic: `src/devices/RoomSensor/Program.cs`
- Device constants (topic IDs, names): `src/devices/RoomSensor/Constants.cs`
- Shared Homie client: `src/common/HomieNano/`

### 3. Deploy to device
```powershell
.\scripts\Deploy-ToDevice.ps1
```
This builds `RoomSensor.nfproj` with MSBuild and flashes via `nanoff` on the COM port defined in `local.env.ps1`.

**Alternative — Visual Studio:**  
Open `SmartHome.sln`, right-click `RoomSensor` → Set as Startup Project, configure the COM port in Project Properties → nanoFramework, then press **F5** (deploys + attaches debugger) or **Build → Deploy**.

### 4. Observe output
Watch the `mosquitto_sub` terminal started in step 1. The RoomSensor publishes to:
```
homie/room-sensor-office/$homie          → 4.0.0
homie/room-sensor-office/$state          → ready
homie/room-sensor-office/sensor/temperature  → <float>
homie/room-sensor-office/sensor/humidity     → <float>
homie/room-sensor-office/sensor/pressure     → <float>
```
Updates publish every 5 seconds (configurable in `Program.cs` — `Thread.Sleep(5000)`).

---

## Key source facts

| Fact | Value |
|------|-------|
| Device Homie ID | `room-sensor-office` |
| MQTT broker (dev) | `localhost:1883` (hardcoded in `Program.cs` → `HomieMqttClient`) |
| Sensor node | `sensor` (type: BMP260) |
| Properties | `temperature`, `humidity`, `pressure` (all `float`) |
| Update interval | 5 000 ms |
| WiFi config | Stored in nanoFramework network profile on the device; set once via VS or `nanoff` |

---

## Common tasks

### Change the MQTT broker IP
Edit `Program.cs`:
```csharp
var mqttClient = new HomieMqttClient("192.168.1.240");  // ← change this
```
Then redeploy.

### Add a new property
1. Add a constant in `Constants.cs`.
2. Add a `private static FloatProperty` (or appropriate type) field in `Program.cs`.
3. Call `AddFloatProperty(...)` in `SetupHomieDevice()` and wire up updates in the loop.
4. Redeploy and observe the new topic in `mosquitto_sub`.

### Run unit tests
Tests live in `src/tests/`. They use the nanoFramework unit test runner (`TestRunner`).  
Deploy `TestRunner` to the device via VS or `Deploy-ToDevice.ps1 -Project src\tests\TestRunner\TestRunner.nfproj`.

### Flash firmware only (no app)
```powershell
nanoff --target ESP32_S3 --serialport COM3 --update
```
Adjust `--target` to match your actual ESP32 variant.

---

## Script reference

| Script | Purpose |
|--------|---------|
| `scripts\local.env.template.ps1` | Template for machine config — copy to `local.env.ps1` |
| `scripts\Start-DevEnv.ps1` | Start Mosquitto + subscribe to `homie/#` |
| `scripts\Deploy-ToDevice.ps1` | Build RoomSensor and flash via nanoff |

All scripts accept `-Verbose` for extra output and `-Help` / `Get-Help` for documentation.
