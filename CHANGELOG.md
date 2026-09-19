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

  It now says *what* is wrong rather than only *that* something is: an invalid reading
  raises the keyed alert `sensor` and a valid one clears it, instead of pushing the device
  into a bare alert state. The v4 adapter folds that back into the same `$state = alert`
  the device always published, so the wire did not move, but the id and the diagnostic
  message are there for an adapter that has somewhere to put them. That half of
  [#112](https://github.com/JojoEffect/SmartHome/issues/112) landed with the adapter
  rewrite rather than after it, because the seam has no `Alert()` left for RoomSensor to
  call. What is still #112's is *choosing* the adapter: RoomSensor names `HomieClient` in a
  `new` today, and making that a compiled choice is the remaining work.
- **IrrigationControl** and **OvenControl** are stubs. See
  [#11](https://github.com/JojoEffect/SmartHome/issues/11) and
  [#12](https://github.com/JojoEffect/SmartHome/issues/12) — both blocked on what is
  physically wired to the board.

### Libraries

- **`SmartHome.DeviceModel`** says what a device *is*, in terms no convention owns: the
  device/node/property tree, ids and friendly names, datatypes, structured formats
  (`NumericRange`, `EnumOptions`, `ColorFormats`, `BooleanLabels` — types with one parser
  each, not a format string every consumer re-reads), units, values and their canonical
  encoding, a five-state lifecycle, and alerts as a keyed set of id plus message. Built
  with `DeviceBuilder`.

- **`SmartHome.Protocol`** is the seam an adapter implements: `IDeviceProtocol` — connect,
  announce, sleep, say what is wrong, act on commands — plus the command event. It is
  deliberately not an MQTT client. A device *owns* a connection rather than being one, and
  an app that could publish for itself could publish an attribute non-retained, a
  lifecycle state out of order, or open a session without the last will the convention
  requires.

  Together these two replace "the device description *is* Homie v4" with "the description
  is neutral, and one adapter decides what it goes out as", which is what makes a second
  convention a new adapter rather than a second model.
  ([#108](https://github.com/JojoEffect/SmartHome/issues/108),
  [#109](https://github.com/JojoEffect/SmartHome/issues/109))

- **`SmartHome.Homie`** is the Homie v4 adapter over those two, and owns everything that
  knows the convention: the `homie/` topic grammar, the attribute set, the announce order,
  the `$state` vocabulary, the last will, and the re-announce after a reconnect. It exposes
  no model types — `IHomieClient` is `IDeviceProtocol` plus one read-only `HomieState`, the
  `$state` token the device currently maps to, which is all an app needs that the seam
  cannot say. ([#110](https://github.com/JojoEffect/SmartHome/issues/110))

  The client owns its MQTT session, because Homie requires the connection to carry a last
  will and a will can only be declared in CONNECT. It re-announces after a reconnect —
  Homie state lives in the broker's retained store, and a restarted broker has an empty
  one — returning to `alert` or `sleeping` if that is where the device was, rather than
  silently clearing an alert.

  `$state` is where the adapter does the most work, because the model has no `alert` state
  to publish. Any alert raised while the device is ready becomes `alert`; clearing the last
  one goes back to `ready`; a second alert or a changed message publishes nothing, since v4
  has no way to say which alert is meant; ids and messages are logged rather than quietly
  dropped. Alerts raised while the device sleeps change nothing on the wire, and `Sleep()`
  is refused outright while alerting — v4's `alert` may only return to `ready` or
  disconnect. That last rule is the adapter's alone: the neutral transition table allows
  ready → sleeping whatever alerts are raised, so lifecycle changes have to go through
  `IDeviceProtocol` and never through `Device.TryChangeState`.

  What v4 cannot express is refused when the device is built, not guessed at when it is
  published. The `HomieClient` constructor walks the tree once and throws, naming the
  property by its topic, for a `datetime`, `duration` or `json` property, for a range that
  is open-ended or carries a step, and for a bound the property's own encoding cannot
  render exactly. Publishing an invented token or a `$format` the property will not
  enforce would put the disagreement on a controller's side of the wire, where nobody can
  see it.

  `$format` is now rendered from the parsed model value rather than echoed back from the
  string the builder was given, which also means a malformed declaration declares nothing:
  it parses away in the model, and the adapter — which never sees the text — publishes an
  empty `$format` instead of something a controller would misread.

  The whole rewrite was held to byte-identical output, and that was measured rather than
  argued: `HomieClientCheck` and RoomSensor were captured on hardware from the unchanged
  tree first, then again after the rewrite, and the two sets were diffed topic by topic
  and payload by payload — the announcement, the retained store, the `/set` round trip,
  the `$state` sequence through alert, a refused transition and sleep, the re-announce
  after the broker was replaced, the `SUBSCRIBE` block, and the device's own debug log.
  Nothing moved, and the conformance suite passed on both. A refactor this size is only
  distinguishable from a regression by that evidence, and CI cannot produce it. The one
  intended difference is that a device with more than one node now announces its node
  blocks in declaration order rather than hash order — which is what `$nodes` always
  claimed; both devices in this tree have a single node.

- **`SmartHome.Mqtt`** provides `ReconnectingMqttClient`: auto-reconnect and subscription
  replay over `nanoFramework.M2Mqtt`, knowing nothing about Homie.

### Testing

- Unit tests runnable on hardware or on the nanoclr virtual device, including golden wire
  tests that assert the adapter's whole ordered announce — topic and payload transcribed
  from the hardware captures taken before the v4 rewrite, retained flag from those
  captures' fresh-subscriber snapshots, QoS pinned to what the previous implementation
  passed, since a subscriber sees its own QoS and not the publisher's. That is the half of
  "byte-identical" CI can check on every pull request; the conformance run on real hardware
  is still the other half, and the only one that notices a device app's own description
  drifting away from the transcription.
- Five on-device integration checks with a single entry point, covering WiFi, an MQTT
  round trip, the BMP280, broker-outage recovery *including subscription replay*, and a
  device-agnostic Homie v4 conformance check.

### Tooling

- One script per workflow under `scripts/`, each with a matching project skill.
- CI builds all 16 nanoFramework projects and runs the unit tests on the virtual device for
  every pull request.
