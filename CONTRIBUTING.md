# Contributing to SmartHome

How work moves through this repository. `CLAUDE.md` covers the code — layout, naming, the
hard-won gotchas. This file covers the process around it.

## Branching

Trunk-based. `main` is always releasable.

- Every change starts on a short-lived branch off `main`.
- Open a pull request, get CI green, merge, delete the branch.
- No `develop`, no release branches. A branch that lives long enough to need rebasing
  against a moved `main` is a branch that should have been split.

Branch names take the form `<type>/<short-description>`, using the same types as commit
messages: `feat/`, `fix/`, `chore/`, `ci/`, `docs/`, `refactor/`, `test/`. Agent-created
branches may use a `claude/` prefix.

`main` is meant to be protected: direct pushes blocked, a pull request required, and CI
green before merge. There is no approval requirement — there is one human here, and
requiring a second would just mean disabling the rule the first time it mattered.

> **Not yet enforced.** The ruleset is a repository setting and has to be applied by the
> repository owner; until then this section describes a convention, not a guard rail.
> Tracked in [#23](https://github.com/JojoEffect/SmartHome/issues/23).

## Pull requests

**The title must follow [Conventional Commits](https://www.conventionalcommits.org/)** and
is checked by CI:

```
<type>(<optional scope>): <subject in lower case, no trailing period>
```

for example `fix(homie): boolean properties announce True instead of true`.

Titles are checked; individual commit messages are not. Merging produces one merge commit
named after the pull request, so the changelog can be built from titles while commit
bodies stay free prose. That freedom is deliberate: in this repository the commit body is
usually the only record of *why* a piece of code looks the way it does — which failure it
was written against, what was tried and rejected, what is verified and what is assumed.
Write those properly. They are worth more than the diff.

Link the issue with `Closes #123` so it closes on merge.

## Testing, and what CI can and cannot do

There are three kinds of test, kept deliberately apart (see `CLAUDE.md`):

| | What it is | Runs in CI? |
|---|---|---|
| `src/tests` | Unit tests — logic, no environment | **Yes**, on the nanoclr virtual device |
| `src/integrationTests` | One app per external dependency: WiFi, broker, sensor | **No** — needs real hardware |
| `src/devices` | The applications themselves | n/a |

CI restores, builds all projects, and runs the unit tests against the **virtual device**
(`nano.ci.runsettings`, `IsRealHardware=False`). Same IL, same CLR, no board.

CI **cannot** run the integration suite. Those tests need a real ESP32 on a COM port, a
real WiFi network and a real Mosquitto broker. Until a guarded self-hosted runner exists
(tracked as an issue), running them is a manual step before merge:

```powershell
.\scripts\Run-IntegrationTests.ps1
```

**This flashes and runs code on physical hardware.** So does `Run-Tests.ps1` locally, and
so does `Deploy-ToDevice.ps1`. Say in the pull request what you ran on hardware — or say
plainly that you ran nothing. An unverified change honestly labelled is fine; an
unverified change presented as tested is not.

Because the suite leaves the last test flashed on the device, redeploy the real
application afterwards:

```powershell
.\scripts\Deploy-ToDevice.ps1
```

## Dependencies

Dependabot raises grouped monthly pull requests for GitHub Actions and NuGet.

Actions updates are routine. **NuGet updates are not.** `CLAUDE.md`'s version-alignment
policy pins the nanoFramework packages to versions matching the firmware on the device,
and a mismatch produces a device-side `Link failure` at runtime rather than a build
error. CI runs on the virtual device, so it will not catch it. Every NuGet bump needs a
hardware run before merge, and possibly a firmware update via `nanoff`.

## Issues

Issues are the backlog. There is no to-do file in the repository — there was one, and
having two places to look meant neither was trusted.

Labels: `type:` (bug, feature, task, spike), `area:` (homie, infra, sensor), `status:`
(in-progress, blocked, review). Anything waiting on a decision or on information gets
`status: blocked` and says in the body exactly what it is waiting for, so it can be
picked up cold.

## Releases

> **Not implemented yet.** None of the tooling below exists — tagging today produces a
> git tag and nothing else. Tracked in
> [#24](https://github.com/JojoEffect/SmartHome/issues/24). This section records the
> agreed design so the issue does not have to re-derive it.

The intent: manual and on demand. Tag when a set of changes is worth calling a version.

```bash
git tag v1.2.0 && git push origin v1.2.0
```

MinVer will derive the version from the tag. Assemblies need an explicit stamping step:
`.nfproj` is a classic project and takes its version from a checked-in
`Properties/AssemblyInfo.cs` — all 14 are hardcoded to `1.0.0.0` — so MinVer's
`$(Version)` reaches nothing on its own. The release build will rewrite those before
compiling the artifacts, and attach each device's deployment `.bin`.

The changelog is written by hand — a generated one would list what changed, and the
interesting part is why.

## Local setup

See `CLAUDE.md`. In short: copy the two `local.env` templates, fill in your COM port and
Mosquitto directory, and use the scripts in `scripts/` rather than raw `msbuild` /
`nanoff` / `mosquitto_sub`. Both `local.env.ps1` files are git-ignored and must stay that
way.
