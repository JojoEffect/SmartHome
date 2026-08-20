# SmartHome

SmartHome is a .NET **nanoFramework** solution for ESP32-based home automation devices using the **Homie v4** MQTT convention.

## Local workflow

1. Copy the local config templates:
   ```powershell
   Copy-Item scripts\local.env.template.ps1 scripts\local.env.ps1
   Copy-Item scripts\nanoFramework.local.env.template.ps1 scripts\nanoFramework.local.env.ps1
   ```
2. Edit the config files for your machine (COM port, Mosquitto directory, companion-repo branch).
3. Start the local workspace — syncs the companion nanoFramework repos, then runs Mosquitto and subscribes to `homie/#`:
   ```powershell
   .\scripts\Start-DevEnv.ps1
   ```
4. Build and flash the RoomSensor:
   ```powershell
   .\scripts\Deploy-ToDevice.ps1
   ```

## Included automation

| Script | Purpose |
|---|---|
| `scripts\Start-DevEnv.ps1` | Sync companion repos (`-NoSync` to skip), start Mosquitto, subscribe to `homie/#`. `-Detached` backgrounds it |
| `scripts\Stop-DevEnv.ps1` | Stop what `Start-DevEnv.ps1` started. Safe to call when nothing is running |
| `scripts\Deploy-ToDevice.ps1` | Build and deploy a nanoFramework project to the ESP32 via `nanoff` |
| `scripts\Run-Tests.ps1` | Build and run the `NFUnitTest` unit test suite on the device |
| `scripts\Run-IntegrationTests.ps1` | Run the whole `src\integrationTests` suite (WiFi, MQTT, BMP280) in one call |
| `scripts\Sync-NanoFrameworkRepos.ps1` | Keep the companion nanoFramework repositories current |
| `scripts\Restore-Packages.ps1` | Restore `packages.config` NuGet packages from the local cache |
| `scripts\Watch-DeviceSerial.ps1` | Capture the device's raw native boot log |
| `scripts\Watch-DeviceDebugOutput.ps1` | Stream managed debug output (`Debug.WriteLine`, exceptions) over CLI |

`Deploy-ToDevice.ps1`, `Run-Tests.ps1`, and `Run-IntegrationTests.ps1` write to and execute code on the physical device.

## Project structure

- `src\devices\` — real device applications (RoomSensor, IrrigationControl, OvenControl)
- `src\integrationTests\` — on-device end-to-end checks, one external dependency each
- `src\tests\` — unit tests (`NFUnitTest`, `TestRunner`)
- `src\common\` — shared libraries (`HomieNano`)
- `tools\` — host-side tooling (`DeviceDebugMonitor`)

See [`CLAUDE.md`](CLAUDE.md) for the full repo-specific workflow, the companion nanoFramework repositories, and Skills Discovery / MCP integration guidance.
