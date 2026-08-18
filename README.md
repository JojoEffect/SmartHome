# SmartHome

SmartHome is a .NET **nanoFramework** solution for ESP32-based home automation devices using the **Homie v4** MQTT convention.

## Local agentic workflow

1. Copy the local config templates:
   ```powershell
   Copy-Item scripts\local.env.template.ps1 scripts\local.env.ps1
   Copy-Item scripts\nanoFramework.local.env.template.ps1 scripts\nanoFramework.local.env.ps1
   ```
2. Edit the config files for your machine.
3. Start the full local workspace:
   ```powershell
   .\scripts\Start-AgentWorkspace.ps1
   ```
4. Build and flash the RoomSensor:
   ```powershell
   .\scripts\Deploy-ToDevice.ps1
   ```

## Included automation

- `scripts\Start-AgentWorkspace.ps1` syncs the key nanoFramework repositories beside `SmartHome`, then starts Mosquitto and subscribes to `homie/#`.
- `scripts\Sync-NanoFrameworkRepos.ps1` keeps the local nanoFramework companion repositories current.
- `scripts\Start-DevEnv.ps1` runs only the local MQTT observation environment.
- `scripts\Deploy-ToDevice.ps1` builds and deploys a selected nanoFramework project to the ESP32 via `nanoff`.

See `.github\copilot-instructions.md` for the full repo-specific workflow, relevant nanoFramework repositories, and Skills Discovery / MCP integration guidance.