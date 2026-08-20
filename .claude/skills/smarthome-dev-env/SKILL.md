---
name: smarthome-dev-env
description: Start or stop the local Mosquitto broker and watch homie/# MQTT traffic from SmartHome devices. Use when asked to observe device output, watch MQTT traffic, or start/stop the dev environment.
---

# SmartHome dev environment (Mosquitto + MQTT watch)

`scripts\Start-DevEnv.ps1` is the single dev-environment entry point. It syncs the sibling
nanoFramework repos, then starts Mosquitto with an explicit `listener <port> 0.0.0.0` +
`allow_anonymous true` config (Mosquitto 2.x defaults to localhost-only otherwise, which silently
made the broker unreachable from any real device — confirmed and fixed the hard way), then
subscribes to `homie/#` and streams it to the console.

```powershell
.\scripts\Start-DevEnv.ps1
```

Flags:

- `-NoSync` — skip the companion-repo sync. Use when the siblings are known current, or offline.
- `-Detached` — start the broker and subscriber in the background and return immediately.
  Subscriber output goes to a log file whose path is printed; stop it with `Stop-DevEnv.ps1`.

```powershell
.\scripts\Start-DevEnv.ps1 -Detached -NoSync
.\scripts\Stop-DevEnv.ps1
```

`Stop-DevEnv.ps1` stops whatever `Start-DevEnv.ps1` recorded for the configured port, deletes the
generated Mosquitto config, and exits 0 when nothing is running — safe to call unconditionally at
the end of a test run. `-KeepLog` preserves the detached subscriber's `homie/#` log.

No hardware confirmation needed for either — they only touch the local machine.

Without `-Detached` this is a foreground, long-running command (blocks until Ctrl+C). When
running it yourself for a bounded check rather than handing it to the user, prefer `-Detached`
and read the printed log file over waiting on it synchronously.

Expected topics from RoomSensor: `homie/room-sensor-office/$homie`, `.../$state`,
`.../sensor/temperature`, `.../sensor/humidity`, `.../sensor/pressure`. No traffic within ~30s of
a deploy (past the app's MQTT retry window) means something's wrong upstream of MQTT — see
`smarthome-watch-serial` to check whether the device even reached that point, or run
`smarthome-integration-tests` to isolate which dependency (WiFi, broker, sensor) is broken.
