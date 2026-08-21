# Next steps

Work that is known, wanted, and not done yet. Each item says what it is and why it matters, so
it can be picked up cold. Delete an item when it ships — this is a to-do list, not a changelog;
the git history is the changelog.

**Agreed order (2026-08-21): ~~3~~ → ~~1 (with 2 folded in)~~ → 5.** RoomSensor's real sensor
data, the device-agnostic `HomieClientCheck` and topic-id validation all shipped on 2026-08-21.
What remains is the first actuator, which is blocked on what hardware is physically attached
rather than on code.

## 1. Float values don't round-trip their own payload

A float property set to `21.5` comes back as `21.499999999999999`: nanoFramework's
`double.ToString()`. Not a spec violation — the convention only says a float payload is a number
— but a controller that writes a value and reads back something else is a poor experience, and
`HomieClientCheck` has to compare floats with a tolerance because of it. Worth deciding whether
the library should format float payloads deliberately (round-trip precision, or a fixed number
of decimals) rather than inheriting whatever `ToString()` does.

## 5. IrrigationControl and OvenControl are empty stubs

Both are 20-line "Hello from nanoFramework" projects with no Homie usage. They are the reason
`IHomieClient` and `OnCommand` exist — the first one written will be the real test of whether the
Homie library is genuinely device-agnostic.
