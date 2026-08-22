---
name: smarthome-release
description: Cut a tagged SmartHome release, or stamp assembly versions locally. Use when asked to release, tag a version, publish device images, or when a release build reports the wrong version.
---

# Cut a release

Releases are **manual and on demand**. Nothing fires on an ordinary merge.

```bash
git tag v1.2.0 && git push origin v1.2.0
```

That triggers `.github/workflows/release.yml`, which derives the version with
`minver-cli`, stamps it into every `AssemblyInfo.cs`, rebuilds in `Release`, and attaches
each device's deployment `.bin` to a GitHub release.

**Tag a commit that already passed CI.** The workflow deliberately does not re-run the
unit tests — a release that quietly tests different code than the tag names would be worse
than one that tests none. Check the tagged commit is green first:

```bash
gh run list --branch main --workflow CI --limit 3
```

Write the `CHANGELOG.md` entry **before** tagging. It is hand-written on purpose: a
generated list says what changed, and the part worth reading is why.

## Dry run

`workflow_dispatch` with `dry-run` (the default) builds, stamps and collects the images,
uploading them as a build artifact without publishing a release. Use it after changing
anything in the release path.

## Why there is a stamping step at all

`.nfproj` is a classic MSBuild project with no generated assembly info: it takes its
version from a checked-in `Properties/AssemblyInfo.cs`, and all 14 are hardcoded to
`1.0.0.0`. MinVer works by setting the MSBuild `$(Version)` property, which SDK-style
projects turn into assembly attributes and classic ones ignore entirely — so MinVer alone
would name the release correctly and leave every binary claiming 1.0.0.0.

Hence `scripts\Set-AssemblyVersion.ps1`. Locally:

```powershell
.\scripts\Set-AssemblyVersion.ps1 -Version 1.2.0
.\scripts\Set-AssemblyVersion.ps1 -Version 1.2.0 -Check   # report, change nothing, non-zero if it would
```

It rewrites `AssemblyVersion` and `AssemblyFileVersion` only. Two things it deliberately
leaves alone, and which any replacement must also leave alone:

- **`AssemblyNativeVersion`** — a nanoFramework attribute tracking the *native* assembly
  signature, unrelated to the release version.
- The commented-out `// [assembly: AssemblyVersion("1.0.*")]` sample line, which a loose
  regex will happily rewrite into nonsense.

It also reads with `[System.IO.File]::ReadAllText`, not `Get-Content -Raw`. On Windows
PowerShell 5.1 `Get-Content` decodes a BOM-less file as Windows-1252, so the UTF-8 bytes
behind the `©` in the copyright line come back as two characters and get re-encoded into
mojibake — silently corrupting 13 of the 14 assemblies. That was a real bug, caught by
diffing a test stamp rather than by reading the code.

**If you stamp locally, revert before committing.** Versions belong to tags, not to the
tree:

```powershell
git checkout -- src
```

## Two things to know about the images

- They are **`Release`** builds, so `Debug.WriteLine` is compiled out and
  `smarthome-watch-debug-output` will show almost nothing for them. Build `Debug` when
  diagnosing.
- The workflow uses `/t:Rebuild`, not `/t:Build`. A plain incremental build silently drops
  the deployment `.bin` — the same reason `smarthome-deploy` always rebuilds. Without it a
  release would publish no artifacts and look successful doing it.
