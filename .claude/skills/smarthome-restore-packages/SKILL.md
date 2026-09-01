---
name: smarthome-restore-packages
description: Restore classic packages.config NuGet packages for SmartHome's nanoFramework projects from the local NuGet cache. Use when a build fails with a missing package/deployment image, after pinning or reverting a package version, or after switching branches.
---

# Restore SmartHome NuGet packages

```powershell
.\scripts\Restore-Packages.ps1
```

This repo's nanoFramework projects use classic `packages.config` restore, not `PackageReference`.
`msbuild /t:Restore` is a no-op for that style ("None of the specified projects contain packages
to restore" — confirmed), and `nuget.exe` isn't installed on every machine. Without this script,
a missing package version shows up confusingly late — as `Deploy-ToDevice.ps1` failing with
"Deploy image not found" *after* a build that reported "Build succeeded."

The script copies any missing `packages\<Id>.<Version>\` folder from the local NuGet global cache
(`%NUGET_PACKAGES%` or `~\.nuget\packages\`) — every version Visual Studio has ever restored on
this machine is already sitting there. It deliberately never hits the network, so it's safe to
run unattended; if a needed version genuinely isn't cached anywhere locally, it reports exactly
which package/version and says so clearly rather than guessing or downloading silently. In that
case, restore once via Visual Studio (open `SmartHome.sln`, accept the NuGet restore prompt) to
populate the cache, then re-run this script.

It restores only the `packages.config` files of the checkout it runs from. Linked worktrees sit
*inside* the main checkout (`.claude\worktrees\<name>`), each a full copy with its own configs and
its own `packages\`, so a plain recursive glob run from the main checkout would pull every
worktree's referenced versions into it — including a version pinned in a worktree to chase a
regression, which `Get-NanoFrameworkTestAdapterDir` could then pick as the test adapter. The glob
lives in `Get-SmartHomePackagesConfig` (`scripts\Common.ps1`) and is shared with
`Test-Setup.ps1`, so the two always agree about what this checkout needs.

Run this any time you've hand-edited a `packages.config` (e.g. pinning/reverting a version to
test a regression) or after switching to a branch with different package versions, before
building or deploying.
