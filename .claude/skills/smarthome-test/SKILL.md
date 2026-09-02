---
name: smarthome-test
description: Run the SmartHome unit test suite (SmartHome.UnitTests) on real ESP32 hardware. Use when asked to run the unit tests or verify device-side unit tests pass. For the WiFi/MQTT/sensor end-to-end checks, use smarthome-integration-tests instead.
---

# SmartHome hardware unit test run

Run `scripts\Run-Tests.ps1` — builds `SmartHome.UnitTests` and runs it via `vstest.console` + the
nanoFramework test adapter, taken from the `packages\nanoFramework.TestFramework.<version>\` this
checkout's own `packages.config` files reference.

```powershell
.\scripts\Run-Tests.ps1
```

`nano.runsettings` has `IsRealHardware=True` — this deploys test code to and executes it on the
physical device on the configured COM port, same as a deploy. **Always confirm with the user
before running this**, same rule as `smarthome-deploy`.

This suite is `src\tests\Unit` only — unit tests, executed on hardware but testing logic,
not the physical environment. The WiFi/broker/sensor end-to-end checks live in
`src\integrationTests` and have their own entry point (`smarthome-integration-tests`).

If it fails with `vstest.console not found`, Visual Studio (or its Build Tools) isn't installed —
tests can still run from VS's own Test Explorer as a fallback. If it fails with the nanoFramework
test adapter not found, the warning above the error names which of the three reasons it is:
the referenced version is not restored (run `smarthome-restore-packages`), nothing in the
checkout references `nanoFramework.TestFramework` at all, or two projects reference different
versions and there is nothing to pick between them. It resolves by reference rather than by
taking the highest-sorting copy under `packages\`, because that sort is textual — `3.0.9` beat
`3.0.80` — and would run the suite against an adapter this checkout does not reference (#79).
