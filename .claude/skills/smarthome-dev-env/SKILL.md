---
name: smarthome-dev-env
description: Start the local Mosquitto broker and watch homie/# MQTT traffic from SmartHome devices. Use when asked to observe device output, watch MQTT traffic, or start the dev environment.
---

# SmartHome dev environment (Mosquitto + MQTT watch)

Run `scripts\Start-DevEnv.ps1` — starts Mosquitto with an explicit `listener <port> 0.0.0.0` +
`allow_anonymous true` config (Mosquitto 2.x defaults to localhost-only otherwise, which silently
made the broker unreachable from any real device — confirmed and fixed the hard way), then
subscribes to `homie/#` and streams it to the console.

```powershell
.\scripts\Start-DevEnv.ps1
```

No hardware confirmation needed — this only touches the local machine (starts a local Mosquitto
process, listens on the network) and stops the broker cleanly when the subscriber exits.

This is a foreground, long-running command (blocks until Ctrl+C) — when running it yourself for
a bounded check rather than handing it to the user, run it in the background and read its output
file rather than waiting on it synchronously.

Expected topics from RoomSensor: `homie/room-sensor-office/$homie`, `.../$state`,
`.../sensor/temperature`, `.../sensor/humidity`, `.../sensor/pressure`. No traffic within ~30s of
a deploy (past the app's MQTT retry window) means something's wrong upstream of MQTT — see
`smarthome-watch-serial` to check whether the device even reached that point.
