---
name: smarthome-deploy-config
description: Deploy a SmartHome device's configuration file (broker address, room, pins, intervals, calibration) to its internal storage with nanoff, without rebuilding or reflashing. Use when asked to change where a device is installed or what it talks to, to commission a sensor, or when a device reports a configuration alert.
---

# SmartHome device configuration deploy

Everything under `config\` describes **where a device is installed**, not what it does. It lives on
the device's internal storage as `I:\configuration.json` and is read once, at boot — so changing
the broker address, the room a sensor claims to be in, a GPIO pin, a measurement interval or a
calibration constant is an edit here plus one command, not an edit, a rebuild and a 90-second
flash.

```powershell
.\scripts\Deploy-DeviceConfig.ps1                     # config\room-sensor.deploy.json
.\scripts\Deploy-DeviceConfig.ps1 -ResolveOnly        # validate everything, touch no device
.\scripts\Deploy-DeviceConfig.ps1 -Manifest config\room-sensor.deploy.json
```

`-ResolveOnly` is the one to reach for first after editing a configuration file. It checks
everything that can be checked without the device — the manifest parses, every file it names is
there, every `.json` payload parses, no committed COM port, destinations are real device paths —
and deploys nothing. It needs no device plugged in.

## What it does and does not touch

It writes **files** over the debugger's wire protocol into the littlefs partition the firmware
already carries. It does **not** flash firmware, does not touch the deployment partition, and
leaves the app on the device exactly as it was. So it is not in the same class as
`Deploy-ToDevice.ps1` — nothing is erased and nothing is at risk of being left half-written.

It also does not reset the device, and **a device reads its configuration once, at boot**, so
nothing changes until the next reset. The script's last line says so and names the command:

```powershell
.\scripts\Watch-DeviceDebugOutput.ps1 -DurationSeconds 20
```

which resets it and shows whether the configuration was read — look for
`Read the device configuration from 'I:\configuration.json'`, or the failure in its place.

## When a device is alerting about its configuration

A device whose configuration is missing, unreadable or malformed **connects anyway and alerts**:
it announces itself, raises the alert id `configuration` with the reason as the message, and
announces none of the nodes that would have come from data it does not have. On the Homie v4 wire
that is `$state = alert` with no sensor node.

That is deliberate and is the shared contract in `SmartHome.DeviceConfiguration` — not a bug to
work around by putting a default back into the firmware. The fix is always to deploy a
configuration and reset. The reason is in the alert message and in the device's own log, and they
say the same thing.

## Do not put machine-specific values in the manifest

The manifest is version-controlled, so it carries no `SerialPort` (the script takes that from
`SMARTHOME_COM_PORT` in `scripts\local.env.ps1`) and its `SourceFilePath`s are relative to the
repository root. The script refuses a manifest that breaks either rule rather than honouring it.

## Adding a device

Two files in `config\` — `<device>.json` and `<device>.deploy.json` — plus a configuration class
in the device app implementing `IValidatableConfiguration`. `config\README.md` has the details.
The JSON keys are the class's property names spelled exactly; the parser on the device is
case-sensitive.

Whatever a device's `Validate()` will not accept must include zero for every numeric field. An
absent or misspelt JSON key leaves its field at the type's default, and a device reading pin 0
while announcing itself as healthy is the exact failure this mechanism exists to prevent.

## If it fails

`nanoff` reports a per-file upload failure on stdout and **still exits 0**, so the script reads
its output as well as its exit code and will refuse a run that confirmed fewer files than the
manifest named. If it says so, take it at face value: nothing can be assumed about what is on
the device.

The usual cause of a run that cannot reach the device is something else holding the port —
Visual Studio's device window and a running `Watch-Device*` capture both do.
