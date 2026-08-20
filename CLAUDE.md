# SmartHome — Claude Code guidance

**nanoFramework** solution for **ESP32** home-automation devices, speaking the **Homie v4** MQTT
convention (topic prefix `homie/`). Primary device today: **RoomSensor**.

This file is the Claude Code counterpart to [`.github/copilot-instructions.md`](.github/copilot-instructions.md)
and root [`AGENTS.md`](AGENTS.md) — same repo, same facts, tool-specific framing. If those
drift apart, treat `.github/copilot-instructions.md` as the source of truth for nanoFramework/
sibling-repo details and reconcile this file.

## Hardware safety — read this first

Two scripts talk to the physical ESP32 over its COM port: `Deploy-ToDevice.ps1` (flashes
firmware) and `Run-Tests.ps1` (`nano.runsettings` has `IsRealHardware=True`, so it deploys and
executes test code on the device too). **Always confirm with the user before running either**,
even mid-session, even if a task seems to obviously call for it. Everything else in this repo
(build, mosquitto, repo sync) is regular reversible work.

## Start here for any framework/library/device debugging

Before investigating anything nanoFramework-, firmware-, or library-behavior related (a socket
exception, an MQTT connect failure, "does this API exist," version-alignment questions), **sync
the sibling repos first** if they aren't already present one level up from this repo
(`..\nf-interpreter`, `..\nanoFramework.m2mqtt`, etc. — check with a quick `Test-Path`, or just
run the idempotent sync):

```powershell
.\scripts\Sync-NanoFrameworkRepos.ps1
```

This is not optional/best-effort — a real debugging session on 2026-08-19 spent hours
diagnosing a socket connect failure (guessing at package versions, capturing raw serial output,
searching GitHub blind) before finally syncing these repos, at which point their source, samples,
and docs became directly available for exactly that failure. Don't repeat that: sync first, then
investigate using `nf-interpreter` source (native firmware behavior),
`nanoFramework.m2mqtt` source (the actual M2Mqtt client code, not just its NuGet package),
and `Samples` (working reference usage) before falling back to web search or guesswork.

## The reliable entry points

The goal for this repo is a dev loop that works equally well by hand or from an agent: one
script per workflow, sourced from `scripts\local.env.ps1`, non-interactive, real exit codes.
Prefer these over ad-hoc `msbuild`/`nanoff`/`mosquitto_sub` invocations — if a workflow doesn't
have a script yet, that's a gap worth closing rather than working around.

| Script | Does | Touches hardware? | Skill |
|---|---|---|---|
| `scripts\Start-DevEnv.ps1` | Starts local Mosquitto (explicit `0.0.0.0` listener — a bare `-p` binds localhost-only on Mosquitto 2.x and silently can't be reached from a real device), subscribes to `homie/#` | No | `smarthome-dev-env` |
| `scripts\Deploy-ToDevice.ps1 [-Project <path>] [-Configuration Debug\|Release]` | Always `/t:Rebuild`s (a plain incremental build silently drops the deployment `.bin`) then flashes via `nanoff` | **Yes** | `smarthome-deploy` |
| `scripts\Run-Tests.ps1` | Builds `NFUnitTest` and runs it via `vstest.console` + the nanoFramework test adapter | **Yes** | `smarthome-test` |
| `scripts\Sync-NanoFrameworkRepos.ps1` | Clones/updates the sibling nanoFramework repos beside `SmartHome` | No | `smarthome-sync-nanoframework` |
| `scripts\Restore-Packages.ps1` | Restores classic `packages.config` NuGet packages from the local NuGet cache — `msbuild /t:Restore` is a no-op for this repo's project style | No | `smarthome-restore-packages` |
| `scripts\Watch-DeviceSerial.ps1 [-DurationSeconds <n>] [-NoReset]` | Raw serial capture of the device's native boot log, no debugger needed (managed `Debug.WriteLine` output still needs VS) | Resets only | `smarthome-watch-serial` |
| `scripts\Start-AgentWorkspace.ps1` | `Sync-NanoFrameworkRepos.ps1` + `Start-DevEnv.ps1` in one call | No | — |
| `scripts\Common.ps1` | Shared helpers (env loading, MSBuild/vstest/adapter discovery, repo-sync) — dot-source, don't duplicate its logic | No | — |

Every script here has a matching project skill under `.claude/skills/smarthome-*` — prefer
invoking the skill (or just running the script directly) over re-deriving the workflow ad hoc.
If a new recurring unit of work shows up that isn't covered by an existing script, add one
(follow the conventions above: `Common.ps1` helpers, `Set-StrictMode`, real exit codes, clear
`Write-Error` remediation) and give it a matching skill — that's the standing expectation for
this repo, not a one-time cleanup.

First-time setup: copy `scripts\local.env.template.ps1` → `scripts\local.env.ps1` and
`scripts\nanoFramework.local.env.template.ps1` → `scripts\nanoFramework.local.env.ps1`, then fill
in `SMARTHOME_COM_PORT` and `SMARTHOME_MOSQUITTO_DIR`. Both `local.env.ps1` files are
git-ignored — never commit them.

## Repository layout

```text
src/
  common/HomieNano/       Shared Homie v4 client library (nanoFramework)
  devices/
    RoomSensor/           Primary device: temperature/humidity/pressure via BMP280
    IrrigationControl/
    OvenControl/
    Test/
  tests/
    NFUnitTest/           Real-hardware unit tests (nanoFramework.TestFramework)
    TestRunner/
  capabilities/Capability.Contracts/
Utils/                    Shared utilities
scripts/                  Dev-environment and agent helper scripts (see table above)
SmartHome.sln
```

`RoomSensor/Program.cs` is the main device logic; `HomieMqttClient` (in `HomieNano`) is the
shared Homie client — as of the last commit its auto-reconnect handling is WIP, blocked on an
ESP32 nanoFramework target bug.

## Companion nanoFramework repos

Cloned beside `SmartHome` by `Sync-NanoFrameworkRepos.ps1` (see previous section — sync these
*before* debugging, not after getting stuck), kept on the branch configured in
`scripts\nanoFramework.local.env.ps1`. Check here before assuming something isn't possible in
nanoFramework — full list and version-alignment policy in
[`.github/copilot-instructions.md`](.github/copilot-instructions.md). Lookup order: sibling
`nanoFramework.WebServer` (Skills/MCP) → `Samples` → `nanoFramework.IoT.Device` (sensor drivers)
→ `nanoframework.github.io` (docs) → `nf-interpreter` (firmware/runtime) → `CoreLibrary`
(mscorlib source — a separate repo from `nf-interpreter`, easy to miss) → `Home` (repo index).

## On-device Skills/MCP (forward-looking)

`nanoFramework.WebServer.Skills`/`.Mcp` let a device expose agent-discoverable diagnostics and
actuator control directly (`[Skill]`/`[SkillAction]` + `SkillRegistry`, or
`[McpServerTool]`/`[McpServerPrompt]` + `McpToolRegistry`/`McpPromptRegistry`). Not built yet
anywhere in this repo. If asked to add it, follow those patterns rather than inventing a custom
protocol — likely first candidates are RoomSensor readings and Irrigation/Oven commands.

## Project skills

`.claude/skills/smarthome-*` (deploy, test, dev-env, sync-nanoframework, restore-packages,
watch-serial) each wrap one script from the table above with the context/safety notes specific
to that workflow — see the table for which skill goes with which script.

## Response style

The `caveman` skill (`.claude/skills/caveman`) gives ultra-compressed, technically-precise
responses. It auto-triggers on cues like "less tokens" / "be brief" / `/caveman`, or invoke it
explicitly. Code, commits, and PR text stay in normal style regardless. This repo also carries
the same skill for Copilot/Cursor/Cline/Windsurf (`.clinerules`, `.cursor/rules`,
`.windsurf/rules`) plus a curated engineering-discipline skill set under `.claude/skills/` —
`investigate-first`, `lean-build`, `safe-refactor`, `surgical-patch`, `verify-and-stop`,
`migration`, `cavecrew` — reach for whichever matches the shape of the task.

## GitHub integration

[`.github/workflows/claude.yml`](.github/workflows/claude.yml) mirrors what Copilot's coding
agent already does here (`.github/github-app.yml`: `auto_issue_session`, `remote_control`,
`copilot/`-prefixed branches) — comment `@claude implement this` on an issue and it opens a PR;
comment `@claude review this` on a PR and it reviews on demand. No auto-trigger on PR open or
push — deliberately on-demand only for this single-developer repo, not an always-on reviewer.

The workflow file is in the repo but **inert until you complete the one-time setup**, which
needs your own GitHub admin action (I can't do this for you):
1. Install the Claude GitHub App: https://github.com/apps/claude
2. Run `claude setup-token` locally to generate a subscription OAuth token
3. Add it as repo secret `CLAUDE_CODE_OAUTH_TOKEN`

Locally, `/code-review` works today with no setup — reviews your branch's diff in-session,
`--fix` applies findings, `--comment` posts them to a PR (needs `gh` CLI authenticated, which
isn't installed on this machine yet).

## Subagents

Defined in `.claude/agents/` and committed — available in every session on this repo, not tied
to any one conversation. Nothing forces a session to dispatch to them, though; picking one over
working inline is a per-session judgment call, steered by their `description` and this file.

- **device-ops** — build/deploy/test/mosquitto-observe workflows through the scripts above.
  Always confirms before anything hardware-touching.
- **nanoframework-sync** — sibling-repo sync and package-version alignment against the
  companion nanoFramework repos. Reach for it specifically when a lookup turns into more than a
  couple of exploratory `git tag`/`git log`/WebFetch calls (e.g. matching several repos to exact
  release tags) — that kind of investigation is worth keeping out of the main conversation. For
  a plain sync, the `smarthome-sync-nanoframework` skill (just runs the script) is enough.
