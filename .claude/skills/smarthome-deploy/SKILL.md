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

`nanoff` erases only as much as it writes, so a smaller app flashed after a larger one would
leave the larger one's tail where the CLR still finds and loads it. The script has the **device**
erase anything past the new image's end, over the debugger connection, immediately before the
flash — so a deploy from Visual Studio or a hardware unit-test run in between needs nothing
remembered and no switch. There is no per-machine record any more, and no `-FullPad`.

Two things it prints that are worth reading rather than scrolling past:

- **"Could not clear the deployment area"** — the device would not talk, usually because Visual
  Studio holds the debugger connection. Not a failure: the flash goes ahead with the image padded
  to the whole partition, which is slower and equally safe. Closing VS's device window makes the
  next deploy quick again.
- **A refused flash naming a different deploy address** — the device reports its deploy partition
  somewhere other than `-DeployAddress`. Take that seriously: flashing at the wrong address writes
  into a partition the CLR never scans, `nanoff` reports success anyway, and the app silently never
  runs. Pass the address the device reported.

To deploy an integration test (`src\integrationTests\*`), prefer
`smarthome-integration-tests` — it deploys, captures, and reads the verdict in one call instead
of leaving you to eyeball debug output.

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
