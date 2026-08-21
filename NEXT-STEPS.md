# Next steps

Work that is known, wanted, and not done yet. Each item says what it is and why it matters, so
it can be picked up cold. Delete an item when it ships — this is a to-do list, not a changelog;
the git history is the changelog.

**Agreed order (2026-08-21): ~~3~~ → 1 (with 2 folded in) → 5.** RoomSensor's real sensor data
shipped on 2026-08-21. Next up is the conformance check, which is the regression net for every
later Homie change, with topic-id validation folded into it since that item builds a device
specifically to poke at the convention. The actuator last, because it is blocked on what hardware
is physically attached, not on code.

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
- **Scope decided (2026-08-21): the full matrix, every datatype included.** Assert all mandatory
  device attributes (`$homie`, `$name`, `$state`, `$nodes`, `$extensions`), node and property
  attributes, retained flags, the `init` -> `ready` lifecycle, a `/set` command applied *and
  reflected back* to the property topic, the `alert` and `sleeping` transitions, the last will
  delivered on an unclean drop, and a full re-announce after the broker is replaced — plus an
  integer, float, boolean, string, enum and colour property, each with `$format` and `$unit`
  where the datatype takes one, asserted on the wire.
- Known mechanics before starting: the runner must *drive* the device (publish to `/set` via
  `mosquitto_pub`), which is a third test kind alongside `DeviceMarker` and `BrokerOutage`. And
  asserting "attributes MUST be retained" needs the subscriber to report the retain flag, which
  `mosquitto_sub -v` does not — that means `-F '%t %r %p'` and a changed log format, which both
  `Wait-Heartbeat` and the homie-log parsing read. Either a second subscriber for this test, or
  change the format globally and update the parsers.

Note the harness for most of this already exists: the `BrokerOutage` test kind in
`scripts\Run-IntegrationTests.ps1` knows how to take the broker away and read `homie/#`, and
`Stop-DevEnv`/`Start-DevEnv` give a genuinely fresh broker.

## 2. Homie topic-id validation

The spec: a topic-level id "MAY contain lowercase letters from `a` to `z`, numbers from `0` to
`9` as well as the hyphen character" and "MUST NOT start or end with a hyphen". Nothing validates
this today — `HomieDeviceBuilder` accepts any string, so an invalid id fails silently on the wire
instead of loudly at construction. Belongs in the builder.

## 4. `/set` handling is unproven on hardware (subsumed by item 1)

The `/set` topic fix (2026-08-21) is covered by unit tests only: no device app currently has a
settable property, so nothing exercises a controller command on real hardware. Not standalone
work — item 1 covers it completely, and so would the first actuator app. Kept here only so the
gap isn't forgotten if item 1 is descoped.

## 5. IrrigationControl and OvenControl are empty stubs

Both are 20-line "Hello from nanoFramework" projects with no Homie usage. They are the reason
`IHomieClient` and `OnCommand` exist — the first one written will be the real test of whether the
Homie library is genuinely device-agnostic.
