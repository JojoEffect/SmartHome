---
name: smarthome-watch-debug-output
description: Stream a SmartHome device's real managed-code debug output (Debug.WriteLine, exceptions) over CLI, without Visual Studio. Use when you need to see what a deployed app is actually doing/throwing and can't or don't want to use VS's debugger.
---

# Watch device debug output (no VS needed)

```powershell
.\scripts\Watch-DeviceDebugOutput.ps1                              # 30s, reboots device to capture from boot
.\scripts\Watch-DeviceDebugOutput.ps1 -DurationSeconds 60
.\scripts\Deploy-ToDevice.ps1; .\scripts\Watch-DeviceDebugOutput.ps1 -NoReboot   # attach right after a flash
.\scripts\Watch-DeviceDebugOutput.ps1 -Until 'ITEST'               # stop on a matching line; duration becomes a timeout
.\scripts\Watch-DeviceDebugOutput.ps1 -BuildOnly                   # build the monitor, capture nothing
.\scripts\Watch-DeviceDebugOutput.ps1 -DumpConfig                  # flash partition table instead of a capture
```

`-DumpConfig` prints the device's flash partition table and exits without capturing. It is what
the deploy-address investigation was done with, and it is the thing to reach for when a deploy
lands but the CLR resolves zero assemblies — compare the deployment partition's address against
`-DeployAddress` in `smarthome-deploy`.

Builds and runs `tools\DeviceDebugMonitor`, a small standalone .NET console app on
`nanoFramework.Tools.Debugger.Net` — the same library Visual Studio's nanoFramework debugger
extension is built on. This exists because `smarthome-watch-serial`'s raw serial capture
**cannot** see managed-code output: `nf-interpreter`'s `app_main()` silences plain-text ESP-IDF
logging the instant it starts (`esp_log_level_set("*", ESP_LOG_NONE)`) and switches the UART to
binary WireProtocol framing instead. This tool speaks that protocol directly, so it sees exactly
what VS's Output window would show — `Debug.WriteLine` calls, unhandled exceptions with full
stack traces, and the CLR's own assembly-resolution log.

**Default mode reboots the device right after connecting**, to guarantee capturing the full boot
sequence live — don't skip this by reaching for `-NoReboot` out of habit. `-NoReboot` is only
useful immediately after another reset (e.g. chained right after `Deploy-ToDevice.ps1`, which
already hard-resets via `nanoff`) — connecting even a few seconds late means you'll see nothing,
not because nothing ran, but because the app already finished or crashed before you attached.
Rebooting on top of an already-fresh boot occasionally left the WiFi peripheral in a busy state
(`CLR_E_BUSY` on `ScanAsync`) in one observed case — if that happens, just retry with the default
reboot mode rather than `-NoReboot` timed tightly against another reset.

Not a hardware-write action like `smarthome-deploy`/`smarthome-test` — this only reads and resets
execution state, same tier as `smarthome-watch-serial`.

## Why this exists — a real, resolved mystery

This tool is what finally answered "why does every nanoff-deployed app go silent after
`app_main()`, regardless of which app": connected right after a `nanoff`-only reboot and saw the
CLR itself report `LoadDeploymentAssemblies()` finding **zero** assemblies, then failing with
`CLR_E_WRONG_TYPE`. Root cause: `nanoff`'s hardcoded default deploy address (`0x1B0000`, from
`nanoFirmwareFlasher`'s own `Esp32Firmware.cs`) didn't match this device's actual `deploy`
partition offset (`0x1E0000`) — see `Deploy-ToDevice.ps1`'s `-DeployAddress` parameter and its
comment for the full story. Visual Studio's debugger-based deploy never hit this, since it
pushes assemblies over the wire directly rather than trusting `nanoff`'s address guess — which is
exactly why VS "just worked" while every scripted deploy that night silently didn't.
