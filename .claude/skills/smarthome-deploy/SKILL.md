---
name: smarthome-deploy
description: Build and flash a SmartHome nanoFramework device (RoomSensor, IrrigationControl, OvenControl) to the ESP32 over its COM port. Use when asked to deploy, flash, or push code to the device.
---

# SmartHome device deploy

Run `scripts\Deploy-ToDevice.ps1` — don't hand-roll `msbuild`/`nanoff` calls. It always does a
full `/t:Rebuild` (a plain incremental build silently drops the deployment `.bin` — confirmed the
hard way), then flashes via `nanoff`.

```powershell
.\scripts\Deploy-ToDevice.ps1                                                    # RoomSensor, Debug
.\scripts\Deploy-ToDevice.ps1 -Project src\devices\IrrigationControl\IrrigationControl.nfproj
.\scripts\Deploy-ToDevice.ps1 -Configuration Release
```

**This flashes real hardware. Always confirm with the user first** — state the project, the
configuration, and the COM port (from `scripts\local.env.ps1`) before running. Don't chain a
deploy automatically after a build succeeds; surface the build result and ask.

If the deploy fails with "Deploy image not found: ...\.bin", packages may be missing from
`packages\` — run the `smarthome-restore-packages` skill (or `scripts\Restore-Packages.ps1`
directly) first, then retry.

If `scripts\local.env.ps1` doesn't exist, tell the user to copy it from
`scripts\local.env.template.ps1` and fill in `SMARTHOME_COM_PORT` — don't guess the port.

After a successful deploy, the natural next step is watching it come up — see
`smarthome-dev-env` (MQTT traffic) or `smarthome-watch-serial` (raw boot log) — but that's a
separate confirmation, not something to chain automatically.
