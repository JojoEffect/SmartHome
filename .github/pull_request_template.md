## Summary

<!-- One sentence describing what this PR does -->

Closes #<!-- issue number -->

## Changes

<!-- Bullet list of what changed and why -->
-

## Type of Change

- [ ] Bug fix
- [ ] New feature
- [ ] Refactor / chore
- [ ] Infrastructure / tooling

## Testing Done

<!--
  CI builds the solution and runs the unit tests on the nanoclr virtual device. It
  cannot run the integration suite: that needs real WiFi, a real broker and a real
  BMP280. Say here what you ran on hardware, or say that you ran nothing.
-->
-

## Checklist

- [ ] Code compiles without errors
- [ ] Relevant tests pass (or N/A for non-code changes)
- [ ] No secrets or credentials committed
- [ ] PR title follows Conventional Commits (it becomes the merge commit message)

### If this PR changes a NuGet package version

<!-- Delete this section if it does not. -->

- [ ] I have run `.\scripts\Run-Tests.ps1` **and** `.\scripts\Run-IntegrationTests.ps1` on hardware

A green CI run does **not** clear a package bump. `CLAUDE.md`'s version-alignment policy
pins `nanoFramework.CoreLibrary`, `Hardware.Esp32`, `Logging` and `M2Mqtt` to versions
matching the firmware flashed on the device, and a mismatch produces a device-side
`Link failure` at *runtime* rather than a build error. CI runs against the virtual
device, which is not the firmware on your board — so this is exactly the case where CI
can be green and the deploy still fails.
