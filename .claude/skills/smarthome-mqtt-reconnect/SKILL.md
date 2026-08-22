---
name: smarthome-mqtt-reconnect
description: Run or debug the MQTT broker-outage integration check, which kills and recreates the broker under a live device and asserts it publishes again. Use when asked whether the device survives a broker restart, or when MqttReconnectCheck fails.
---

# MQTT reconnect / broker-outage check

`MqttReconnectCheck` is the one test in the suite whose verdict comes from the **host**, not
from the device. It answers: when the broker dies and a new one takes its place, does the device
start publishing again on its own?

```powershell
.\scripts\Run-IntegrationTests.ps1 -Tests MqttReconnectCheck    # this check alone
.\scripts\Run-IntegrationTests.ps1                              # runs it as part of the suite
```

**This flashes and runs code on real hardware. Always confirm with the user first** — same rule
as `smarthome-deploy`.

## What it actually does

1. Deploys `src\integrationTests\MqttReconnectCheck`, which connects through `ReconnectingMqttClient` (`SmartHome.Mqtt`),
   publishes a heartbeat on `homie/mqtt-reconnect-check/heartbeat` every 2s forever, and
   subscribes to `homie/mqtt-reconnect-check/echo/set`, echoing whatever arrives back on
   `homie/mqtt-reconnect-check/echo`.
2. Waits for the first heartbeat (up to 90s: boot + WiFi + connect).
3. `Stop-DevEnv.ps1` — broker and subscriber both die, exactly as if the machine had lost it.
4. Waits out the outage, then `Start-DevEnv.ps1 -Detached` brings up a **fresh** broker.
5. Asserts a heartbeat reappears within 90s. Repeats for a second, longer outage.
6. Publishes a nonce to the echo command topic and requires it back within 30s.

Two outages by design: **3s** (inside one 5s reconnect cycle) and **20s** (several failed
attempts, so the retry loop itself is under test). On Windows there's no graceful mosquitto
shutdown available — `Stop-Process` is `TerminateProcess` either way — so outage length is the
only real variable between "restart" and "kill".

## Why the host decides

The device asserting "I reconnected" is weaker evidence than a message actually arriving at the
recreated broker. So this project emits **no** `[ITEST]` marker — that would be a second,
competing verdict. Its debug output is still worth reading when it fails; the heartbeat, the
publish failures during the outage, and `ReconnectingMqttClient`'s reconnect logging all go there.

The heartbeat is published **non-retained** deliberately: a retained message would be handed to
any fresh subscriber by the broker itself, which looks identical to the device having
republished when it hadn't. `Start-DevEnv.ps1` also truncates the subscriber log on every start,
so each phase reads a log that can only contain heartbeats from after that phase's broker came up.

## Why step 6 exists

Heartbeats prove the *connection* came back, and nothing more. Publishing resumes the instant the
socket is up, so a reconnect that restored the session and replayed **no subscriptions** is
indistinguishable from a healthy one — from the broker's side both just show heartbeats. That
state is not hypothetical: a throw out of `ResubscribeCachedTopics` used to leave the client
connected, publishing normally, and deaf to every `/set` until reboot, with a single
`LogWarning` as the only evidence.

The echo closes that gap. Only a subscription that was actually replayed can turn a publish *to*
the device into a publish *from* it, so `FAIL — never echoed` means the reconnect dropped the
subscriptions even though the heartbeats look fine.

## Reading a failure

- `FAIL — no heartbeat within 90s of the broker returning` — the device did not reconnect. That
  is the real finding this test exists for: `ReconnectingMqttClient`'s auto-reconnect (enabled by
  `Connect()`, retrying every 5s via `ReconnectHandler`) either didn't fire or couldn't recover.
  CLAUDE.md has long called that path WIP, "blocked on an ESP32 nanoFramework target bug" —
  check the device log for the reconnect attempts before assuming the test is wrong.
- `FAIL — heartbeats resumed but '<nonce>' was never echoed` — the connection came back without
  its subscriptions. See "Why step 6 exists": the device is publishing normally and ignoring
  every inbound message. Check the device log for `Resubscribing cached topics` and for a warning
  from the reconnect thread immediately after a successful connect.
- `NO-RESULT — no heartbeat within 90s` — it never connected in the first place, so nothing was
  disconnected. Check WiFi and the broker address; run `smarthome-integration-tests` for
  `WifiCheck`/`MqttCheck` first, which isolate those.
- `ERROR — could not restart the broker` — the host side broke, not the device. Usually a port
  still held by a leftover process: `.\scripts\Stop-DevEnv.ps1 -IncludeOrphans`.

## Constraints

It cannot run with `-NoBroker` — it has to own the broker's lifetime, and tearing down a broker
someone else started is not its business. The runner refuses that combination outright.

Its settings (outage lengths, timeouts, heartbeat topic) live in `$testCatalog` in
`scripts\Run-IntegrationTests.ps1`. The topic and the broker address are cross-checked against
the project's own constants at start-up, so a drift between the two shows up as a warning rather
than a confusing failure.
