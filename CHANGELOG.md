# Changelog

Written by hand, on purpose. A generated changelog lists what changed; the useful part is
usually *why*, and which failure it was written against. That does not come out of commit
subjects.

Format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions
are cut manually — see the Releases section of [`CONTRIBUTING.md`](CONTRIBUTING.md).

## Unreleased

Nothing released yet. The entries below describe what exists on `main` today, so the first
tagged version has something to say for itself.

### Fixed

- **Float properties publish a value a controller can read back.** They rendered with
  `double.ToString()`, which on nanoFramework uses `"G"` and turns `21.5` into
  `21.499999999999999` — value-dependently, since `0.1` came out correctly, which is how
  it survived this long. They now render at a declared precision (two decimals by
  default, set per property with `WithDecimals`).

  The alternatives were measured on the virtual device rather than assumed: round-trip
  format `"R"` throws `NotImplementedException` on this runtime, and `"N"` inserts a
  thousands separator that would corrupt the payload outright.

  The conformance check now compares float echoes as exact strings. It previously
  compared numerically with a tolerance, which tolerated the defect instead of measuring
  it. ([#10](https://github.com/JojoEffect/SmartHome/issues/10))

### Devices

- **RoomSensor** publishes real BMP280 temperature, humidity and pressure over Homie v4
  every 5 seconds, declares `$unit` for all three, and moves to `alert` when a reading is
  invalid. It survives a transient I2C or publish fault instead of rebooting.
- **IrrigationControl** and **OvenControl** are stubs. See
  [#11](https://github.com/JojoEffect/SmartHome/issues/11) and
  [#12](https://github.com/JojoEffect/SmartHome/issues/12) — both blocked on what is
  physically wired to the board.

### Libraries

- **`SmartHome.Homie`** implements the Homie v4 convention: a device model built with
  `HomieDeviceBuilder`, and an `IHomieClient` with `Connect`/`ConnectWithRetry`,
  `Disconnect`, `Alert`, `Sleep`, `Ready` and an `OnCommand` event that distinguishes a
  controller's `/set` from the device's own update.

  The client owns its MQTT session, because Homie requires the connection to carry a last
  will and a will can only be declared in CONNECT. It re-announces after a reconnect —
  Homie state lives in the broker's retained store, and a restarted broker has an empty
  one — returning to `alert` or `sleeping` if that is where the device was, rather than
  silently clearing an alert.

- **`SmartHome.Mqtt`** provides `ReconnectingMqttClient`: auto-reconnect and subscription
  replay over `nanoFramework.M2Mqtt`, knowing nothing about Homie.

### Testing

- 34 unit tests, runnable on hardware or on the nanoclr virtual device.
- Five on-device integration checks with a single entry point, covering WiFi, an MQTT
  round trip, the BMP280, broker-outage recovery *including subscription replay*, and a
  device-agnostic Homie v4 conformance check.

### Tooling

- One script per workflow under `scripts/`, each with a matching project skill.
- CI builds all 14 projects and runs the unit tests on the virtual device for every pull
  request.
