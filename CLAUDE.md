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

## The reliable entry points

The goal for this repo is a dev loop that works equally well by hand or from an agent: one
script per workflow, sourced from `scripts\local.env.ps1`, non-interactive, real exit codes.
Prefer these over ad-hoc `msbuild`/`nanoff`/`mosquitto_sub` invocations — if a workflow doesn't
have a script yet, that's a gap worth closing rather than working around.

| Script | Does | Touches hardware? |
|---|---|---|
| `scripts\Start-DevEnv.ps1` | Starts local Mosquitto, subscribes to `homie/#` — this is how you observe/debug device behavior | No |
| `scripts\Deploy-ToDevice.ps1 [-Project <path>] [-Configuration Debug\|Release]` | Builds an `.nfproj` and flashes it via `nanoff` | **Yes** |
| `scripts\Run-Tests.ps1` | Builds `NFUnitTest` and runs it via `vstest.console` + the nanoFramework test adapter | **Yes** |
| `scripts\Sync-NanoFrameworkRepos.ps1` | Clones/updates the sibling nanoFramework repos beside `SmartHome` | No |
| `scripts\Start-AgentWorkspace.ps1` | `Sync-NanoFrameworkRepos.ps1` + `Start-DevEnv.ps1` in one call | No |
| `scripts\Common.ps1` | Shared helpers (env loading, MSBuild/vstest/adapter discovery, repo-sync) — dot-source, don't duplicate its logic | No |

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

Cloned beside `SmartHome` by `Sync-NanoFrameworkRepos.ps1`, kept on the branch configured in
`scripts\nanoFramework.local.env.ps1`. Check here before assuming something isn't possible in
nanoFramework — full list and version-alignment policy in
[`.github/copilot-instructions.md`](.github/copilot-instructions.md). Lookup order: sibling
`nanoFramework.WebServer` (Skills/MCP) → `Samples` → `nanoFramework.IoT.Device` (sensor drivers)
→ `nanoframework.github.io` (docs) → `nf-interpreter` (firmware/runtime) → `Home` (repo index).

## On-device Skills/MCP (forward-looking)

`nanoFramework.WebServer.Skills`/`.Mcp` let a device expose agent-discoverable diagnostics and
actuator control directly (`[Skill]`/`[SkillAction]` + `SkillRegistry`, or
`[McpServerTool]`/`[McpServerPrompt]` + `McpToolRegistry`/`McpPromptRegistry`). Not built yet
anywhere in this repo. If asked to add it, follow those patterns rather than inventing a custom
protocol — likely first candidates are RoomSensor readings and Irrigation/Oven commands.

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

- **device-ops** — build/deploy/test/mosquitto-observe workflows through the scripts above.
  Always confirms before anything hardware-touching.
- **nanoframework-sync** — sibling-repo sync and package-version alignment against the
  companion nanoFramework repos.
