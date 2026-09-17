# Device configuration

Everything here describes **where a device is installed**, not what it does: which room a sensor
is in, which pin a valve is on, where the broker lives, how deep a tank is. None of it is
compiled into the image any more, so changing one is a JSON edit and one command rather than an
edit, a rebuild and a reflash.

> **The deployment step does not work on the ESP32 on COM3 yet — issue #132.** Its firmware
> (`ESP32_REV3`, nanoCLR 1.17.0.339) answers the wire-protocol file write with `PlatformError`
> for every destination, so no file can be placed on it. Reading works: a device that has a
> configuration file reads it correctly. Everything below is right, and the last step is blocked.
> `-ResolveOnly` is unaffected and is worth running whenever you edit a file here.

```powershell
.\scripts\Deploy-DeviceConfig.ps1
```

That reads a manifest, resolves it against this checkout and the COM port in
`scripts\local.env.ps1`, and hands it to `nanoff --filedeployment`. It writes files to the
device's internal storage over the debugger connection — it does **not** flash firmware, and the
app on the device is untouched. The device has to be reset (or power-cycled) afterwards, because
a device reads its configuration once, at boot.

## The two files per device

| File | What it is |
|---|---|
| `<device>.json` | The configuration itself. Its keys are the property names of that device's configuration class, spelled exactly — the parser on the device is case-sensitive on purpose, so the file and the class read as the same thing |
| `<device>.deploy.json` | Which file goes where on the device. `nanoff`'s own file-deployment schema (`Files`, `DestinationFilePath`, `SourceFilePath`) |

Today that is `room-sensor.json` and `room-sensor.deploy.json`.

Two deliberate departures from what `nanoff` would accept directly, both filled in by the script
rather than committed:

- **`SourceFilePath` is relative to the repository root.** `nanoff` resolves a relative path
  against its own working directory, which is whatever shell happened to start it. The script
  makes each one absolute against this checkout, so the same manifest works from any directory
  and from any worktree.
- **There is no `SerialPort`.** That is machine-specific and lives in `scripts\local.env.ps1` as
  `SMARTHOME_COM_PORT`; the script passes it on the command line. A manifest that carries one is
  refused rather than honoured, because a committed COM port is a per-machine value in a
  version-controlled file and the next person's device is on a different port.

## Why it is versioned here

The alternative — a file that exists only on the device — has no history, cannot be reviewed, and
is gone after a mass erase with no way to rebuild it except by walking the floor and measuring
everything again. The strongest case is commissioning a sensor in place (issue #37): the loop
becomes measure, edit JSON, one command, re-measure, without a build in it.

## What happens when it is wrong

A device whose configuration is missing, unreadable or malformed **connects anyway and alerts**.
It announces itself, publishes the failure under the alert id `configuration`, and announces none
of the nodes that would have come from data it does not have — so on the Homie v4 wire it shows
up as `$state = alert` with no `sensor` node, rather than as a healthy-looking device reading a
pin somebody guessed at.

That is the contract, and it is in `SmartHome.DeviceConfiguration` rather than in each device, so
it cannot drift between them.
