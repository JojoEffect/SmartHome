---
name: smarthome-script-tests
description: Run the host-side script tests — the desk-provable half of scripts\, covering Common.ps1's helpers and the integration runner's verdict logic. Use before and after any change under scripts\, or when a host-side function needs proving without a device. For the on-device suite use smarthome-integration-tests instead.
---

# Run the host-side script tests

```powershell
.\scripts\Run-ScriptTests.ps1
```

No device, no broker, no network, no `local.env.ps1`, no restored `packages\`. About 50 seconds
for ~250 cases — nearly all of that is the handful that wait out a real deadline to prove a poll
loop gives up, plus the capture-window cases that start a real subscriber process and hold a
window open for its settle. So the wall clock tracks how many of *those* there are rather than
the case count, and a run a few seconds either side of 50s is normal. Exit 0 if everything
passed, 1 otherwise — including when *nothing ran*,
which is a failure on purpose: this repo already shipped three commits on a green `vstest` run
that executed nothing.

Useful switches:

```powershell
.\scripts\Run-ScriptTests.ps1 -File Common          # one subject
.\scripts\Run-ScriptTests.ps1 -Name '*retained*'    # one claim, across files
.\scripts\Run-ScriptTests.ps1 -Detailed             # every case, not just group counts
```

## What it covers, and what it deliberately does not

`scripts\` decides what the integration suite reports, and it is the half of the suite a desk can
exercise: `Get-CatalogValidationError`, `ConvertFrom-HomieCaptureLine`, `ConvertTo-HomieSnapshot`,
`Get-HomieLivePayloads`, `Get-AttributeFailure`, `Get-ConformanceCaptureSeconds`,
`Wait-ForAnnounceWitnessed`, `Get-SubscriberLogLineCount`, `Test-DeviceConstant`,
`Invoke-CommandRetryRounds`, plus `Common.ps1`'s path globs, dev-environment state and the
deployment-geometry parse the deploy cross-checks its flash address against.

Three of the verdict functions are covered here too, which is newer than the boundary this file
used to describe:

- **`Invoke-BrokerOutageCheck`** — everything it decides comes from the subscriber log, so only
  the three broker calls are stubbed and the waits run for real against a fixture log.
- **`Start-HomieCapture` / `Stop-HomieCapture`**, `Get-HomieRetainedSnapshot` with them — driven
  against a scripted `cmd.exe` subscriber whose stdout stands in for `mosquitto_sub`'s. A real
  process, a real redirect, a real teardown; no broker.
- **`Wait-ForRetainedValue`** — the snapshot reader is a queue, so what the caller sees differs
  round to round.

It still does **not** cover `Invoke-HomieConformanceCheck` / `Measure-HomieConformance` beyond
their extracted pieces, and it never covers a device: a passing run here says nothing about
whether the ESP32 announces, reconnects or conforms. Those need `Run-IntegrationTests.ps1` on
real hardware, and running this suite is not a substitute for saying what was verified there in a
pull request.

`Get-AttributeFailure` is the first *assertion* inside `Measure-HomieConformance` that is covered
here — the conformance machinery around it, `Get-ConformanceCaptureSeconds` and the lifecycle
table, already was. It was nested inside that function and closed over its snapshot until issue
#84 gave it the snapshot as a parameter; the rest of the assertions still need a device, and the
uncovered remainder is what #84 tracks.

Two cases here pin a defect rather than endorse it, so the fix has to come past a failing test:
`Test-DeviceConstant`'s silence under a bracketed path (#80) and `Wait-ForRetainedValue`
accepting a payload in the wrong case (#93). Both say so in a comment; closing either issue means
inverting its case, not deleting it.

## Adding a test

One file per subject, `scripts\tests\<Subject>.Tests.ps1`, dot-sourcing the subject at the top:

```powershell
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'Common.ps1')

Describe 'Get-Something' {
    It 'does the thing' {
        Assert-Equal -Expected 'x' -Actual (Get-Something)
    }
}
```

`Run-IntegrationTests.ps1` can be dot-sourced the same way: a guard near the bottom of it means a
dot-source defines the catalog and the functions and runs nothing. Do not go back to lifting
functions out of it with the PowerShell AST — a test that reads its subject through a parser can
drift from what actually runs, which is what issue #74 was about.

The vocabulary is in `scripts\tests\TestRunner.ps1`: `Describe`, `It`, `Assert-Equal`,
`Assert-ArrayEqual`, `Assert-True`/`Assert-False`, `Assert-Null`/`Assert-NotNull`,
`Assert-Match`, `Assert-Contains`, `Assert-Throws`, plus `New-TestDirectory` and
`Set-TestFileContent` for fixtures. Every assertion takes `-Because`, which is what the failure
message leads with.

Two things about them worth knowing before writing a case:

- **Comparisons are case-sensitive** (`Assert-Equal`, `Assert-ArrayEqual`, `Assert-Contains`);
  the first and last take `-IgnoreCase`. `Assert-Match` is a regex and keeps `-match`'s own
  case-insensitive default.
- **`Assert-Equal` and `Assert-Match` refuse a collection** rather than comparing one, and say
  so. PowerShell's `-eq` and `-match` *filter* a collection instead of returning a boolean, so
  `@('a','b') -ceq 'a'` is `@('a')` — truthy — and an assertion built on that would pass
  whenever the expected collection merely *contained* the actual value. Use `Assert-ArrayEqual`
  for collections, and join at the call site for `Assert-Match`.

A test file may stub a function the subject calls (`Get-SmartHomeDevEnvPath`, say) by defining
it after the dot-source; command lookup walks the calling scope chain, so the stub wins inside
that file or that `Describe` and nowhere else.

Three things that have each cost a debugging round already:

- **Give a `Describe`'s recorders names of their own.** `$script:` variables are file-scoped, so
  two groups both using `$script:published` share one, and the suite then passes only because
  both reset first and run in file order.
- **A group's own cleanup must not go through a name a case stubs.** The capture-window group
  tears its subscriber down with `Stop-SmartHomeProcessTree` rather than
  `Stop-SmartHomeRecordedProcess`, because one case replaces the latter with a stub that throws
  — and that cleanup runs in that case's `finally`.
- **Anything that starts a process must stop it in a `finally`.** A surviving `cmd.exe` holds its
  capture file open, and the next case then spends its removal budget on it.

And run mutations before believing a group. Break the behaviour each case claims to pin, one at a
time, and check that *that* case fails — then re-run the mutation after adding any case written
to catch it. Cases that read correctly and cannot fail are the specific failure this suite exists
to prevent, and this repository has shipped them twice.

## Why not Pester

Pester 5 does not ship with Windows. What ships, and what is on this machine, is Pester **3.4.0**,
whose dialect (`Should Be`, not `Should -Be`) is incompatible with anything written for 5. Adopting
Pester would mean `Install-Module` on every desk, a pinned install step in CI, a row in
`Test-Setup.ps1`, and an explicit version guard at every import so PowerShell does not auto-load
the in-box 3.4.0 instead. The point of this suite is that proving a host-side change costs nothing,
and a prerequisite that has to be installed first is a reason not to run it.

That trade is worth revisiting if mocking, fixtures across files, or JUnit output ever become
load-bearing — not before.

## CI

The `Run host-side script tests` step is the first thing in `.github\workflows\ci.yml`, before the
MSBuild and nanoFramework setup, because it needs none of it. It is the only automated coverage
this repository has of the integration tooling itself; #22's unit tests run on the virtual device
and cannot reach a PowerShell function.
