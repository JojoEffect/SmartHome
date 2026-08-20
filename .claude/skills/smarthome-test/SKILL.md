---
name: smarthome-test
description: Run the SmartHome NFUnitTest suite on real ESP32 hardware. Use when asked to run tests, check the test suite, or verify device-side unit tests pass.
---

# SmartHome hardware test run

Run `scripts\Run-Tests.ps1` — builds `NFUnitTest` and runs it via `vstest.console` + the
nanoFramework test adapter (auto-discovered under `packages\nanoFramework.TestFramework.*\`).

```powershell
.\scripts\Run-Tests.ps1
```

`nano.runsettings` has `IsRealHardware=True` — this deploys test code to and executes it on the
physical device on the configured COM port, same as a deploy. **Always confirm with the user
before running this**, same rule as `smarthome-deploy`.

If it fails with `vstest.console not found`, Visual Studio (or its Build Tools) isn't installed —
tests can still run from VS's own Test Explorer as a fallback. If it fails with the nanoFramework
test adapter not found, run `smarthome-restore-packages` first — the adapter comes from the
`nanoFramework.TestFramework` package.
