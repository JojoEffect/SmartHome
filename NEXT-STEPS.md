# Next steps

Work that is known, wanted, and not done yet. Each item says what it is and why it matters, so
it can be picked up cold. Delete an item when it ships — this is a to-do list, not a changelog;
the git history is the changelog.

## 1. HomieClientCheck — device-agnostic Homie conformance test

Wanted: an integration test that exercises **all** the Homie v4 features against a device built
purely for that purpose, rather than testing whichever features RoomSensor happens to use.

Why: today's coverage is accidental. `RoomSensor` has three float properties, none settable, so
whole areas of the convention are exercised by nothing — settable properties and the `/set`
command topic, non-retained properties, the `alert`/`sleeping` states, enum/colour/boolean
datatypes, `$format`, `$unit`. Two real bugs in this area (no last will, and `/set` subscribed on
the wrong topic) were found by reading the spec rather than by a failing test, which is the wrong
way round.

Shape it should take:

- A dedicated project under `src/integrationTests`, building a device that deliberately covers
  the matrix: every datatype, at least one settable and one non-settable property, at least one
  non-retained property, a node with several properties.
- Concrete-device-agnostic: it must not depend on RoomSensor, its topics or its node layout. It
  is a test of `SmartHome.Homie`, not of any shipped device.
- Host-decided verdict, like `MqttReconnectCheck`: the runner subscribes, drives the device
  (publishing to `/set` topics to issue commands), and asserts what actually lands on the broker
  — retained flags included, since "attributes MUST be retained" is a spec rule that only the
  broker can confirm.
- Worth asserting at minimum: all mandatory device attributes present (`$homie`, `$name`,
  `$state`, `$nodes`, `$extensions`), node and property attributes, the `init` -> `ready`
  lifecycle, a `/set` command being applied *and reflected back* to the property topic, the
  `alert` and `sleeping` transitions, the last will delivered on an unclean drop, and a full
  re-announce after the broker is replaced.

Note the harness for most of this already exists: the `BrokerOutage` test kind in
`scripts\Run-IntegrationTests.ps1` knows how to take the broker away and read `homie/#`, and
`Stop-DevEnv`/`Start-DevEnv` give a genuinely fresh broker.

## 2. Homie topic-id validation

The spec: a topic-level id "MAY contain lowercase letters from `a` to `z`, numbers from `0` to
`9` as well as the hyphen character" and "MUST NOT start or end with a hyphen". Nothing validates
this today — `HomieDeviceBuilder` accepts any string, so an invalid id fails silently on the wire
instead of loudly at construction. Belongs in the builder.

## 3. RoomSensor publishes simulated values, not the BMP280

`src/devices/RoomSensor/Program.cs` fills temperature, humidity and pressure with
`random.NextDouble()` and carries the comment "In a real application, replace this with actual
sensor readings". `Bmp280Check` proves the real sensor reads correctly over I2C, so the driver
usage is known-good and can be lifted from there. Until then the device's published data is
fiction, which also means nothing downstream of it can be trusted end to end.

## 4. `/set` handling is unproven on hardware

The `/set` topic fix (2026-08-21) is covered by unit tests only: no device app currently has a
settable property, so nothing exercises a controller command on real hardware. Item 1 above would
cover it; so would the first actuator app.

## 5. IrrigationControl and OvenControl are empty stubs

Both are 20-line "Hello from nanoFramework" projects with no Homie usage. They are the reason
`IHomieClient` and `OnCommand` exist — the first one written will be the real test of whether the
Homie library is genuinely device-agnostic.
