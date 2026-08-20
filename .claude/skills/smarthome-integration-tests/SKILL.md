---
name: smarthome-integration-tests
description: Run the whole SmartHome on-device integration suite (WiFi, MQTT round-trip, BMP280) in one call. Use when asked to run the integration tests, verify the device's external dependencies, or isolate whether WiFi/broker/sensor is the broken one.
---

# SmartHome integration test suite

Run `scripts\Run-IntegrationTests.ps1` — one call covers every project under
`src\integrationTests`. Every test is deployed first; the verdict comes from one of two kinds:

- **`DeviceMarker`** (WifiCheck, MqttCheck, Bmp280Check) — the runner reboots the device,
  captures managed debug output, and reads the `[ITEST] <name> PASS/FAIL` marker.
- **`BrokerOutage`** (MqttReconnectCheck) — the runner kills and recreates the broker under the
  live device and asserts heartbeats reappear on `homie/#`. See `smarthome-mqtt-reconnect` for
  that one; it cannot run with `-NoBroker`.

```powershell
.\scripts\Run-IntegrationTests.ps1                              # all four checks
.\scripts\Run-IntegrationTests.ps1 -Tests WifiCheck,MqttCheck
.\scripts\Run-IntegrationTests.ps1 -NoBroker                    # a broker is already running
```

**This flashes and runs code on real hardware, once per test. Always confirm with the user
first** — state which tests and which COM port (from `scripts\local.env.ps1`), same rule as
`smarthome-deploy`.

A local Mosquitto broker is started detached for the run and stopped again at the end, even on
failure. Pass `-NoBroker` if one is already up (e.g. from `smarthome-dev-env`) — without it,
`Start-DevEnv.ps1` will fail fast on the occupied port rather than silently attaching to the
other broker.

**The suite leaves the last test flashed on the device** (MqttReconnectCheck by default). Redeploy the
real app afterwards if the device is expected to go back to doing its job:
`.\scripts\Deploy-ToDevice.ps1` — a separate hardware action, so confirm it separately.

Reading the result:

- **exit 0** — every test passed. The summary is the whole story; nothing further to look at.
- **exit 1** — the summary names each non-passing test and prints its captured device log path.
  Start there.

Outcomes other than PASS/FAIL:

- `NO-RESULT` — no marker within the capture window. The app crashed before reporting, or wasn't
  where the CLR could load it. Check the log for `CLR_E_WRONG_TYPE` / zero-assembly resolution,
  which means `Deploy-ToDevice.ps1`'s `-DeployAddress` is stale.
- `ERROR` — deploy or capture itself failed; the message is the sub-script's own error.
- `WRONG-TEST` — the device emitted a marker naming a different test: a stale deploy, or that
  project's `TestName` constant doesn't match its project name.
- `RESTARTED` — BrokerOutage only. The device did publish again, but its heartbeat counter went
  backwards, so it recovered by rebooting rather than reconnecting.

Tests run WiFi-first on purpose: `MqttCheck` can only fail confusingly when the network itself is
broken. `MqttCheck` targets a broker address hardcoded in its `Program.cs` — the script warns up
front when that constant isn't an address of this machine, which is the usual reason it fails on
an otherwise healthy device.

Adding a test: new project under `src\integrationTests`, emit `IntegrationTest.Pass`/`Fail` as
soon as the outcome is known (before any idle loop), then add it to `$testCatalog` in
`Run-IntegrationTests.ps1`.

This is not `smarthome-test` — that one runs the `SmartHome.UnitTests` unit suite through
`vstest.console`. These are separate suites with separate entry points.
