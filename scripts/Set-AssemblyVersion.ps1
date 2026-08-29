<#
.SYNOPSIS
    Stamps a version into every AssemblyInfo.cs under src.

.DESCRIPTION
    .nfproj is a classic MSBuild project: it has no auto-generated assembly info, so it
    takes its version from a checked-in AssemblyInfo.cs, and every one checked in here is
    hardcoded to 1.0.0.0. That is why MinVer alone is not enough here -- MinVer sets the
    MSBuild $(Version) property, which SDK-style projects turn into assembly attributes
    and classic ones ignore entirely.

    So the release build calls this first, with the version MinVer derived from the tag.

    Only AssemblyVersion and AssemblyFileVersion are touched. Two things are deliberately
    left alone:

      - AssemblyNativeVersion. That is a nanoFramework attribute tracking the *native*
        assembly signature, and its own comment says to change it only when that
        signature changes. It has nothing to do with the release version.
      - The commented-out '// [assembly: AssemblyVersion("1.0.*")]' sample line, which a
        loose regex would happily rewrite into nonsense.

.PARAMETER Version
    The version to stamp. Accepts full semver (1.2.0-alpha.0.5) as produced by MinVer;
    the prerelease and build-metadata parts are dropped for the assembly attributes,
    which only accept four numeric components.

.PARAMETER Check
    Report what would change and exit non-zero if anything would, without writing. Use
    to assert a clean tree, not to stamp.

.EXAMPLE
    .\scripts\Set-AssemblyVersion.ps1 -Version 1.2.0

.EXAMPLE
    .\scripts\Set-AssemblyVersion.ps1 -Version (minver -t v)
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Common.ps1')

$repoRoot = Get-SmartHomeRepoRoot

# Assembly attributes take four numeric components and nothing else, so 1.2.0-alpha.0.5
# has to become 1.2.0.0. The full string is still what names the release and its
# artifacts; it is only these two attributes that cannot carry it.
$numeric = ($Version -split '[-+]')[0]

if ($numeric -notmatch '^\d+(\.\d+){0,3}$') {
    Write-Error "Cannot derive an assembly version from '$Version': '$numeric' is not a numeric version."
    exit 1
}

$parts = @($numeric -split '\.')
while ($parts.Count -lt 4) { $parts += '0' }
$assemblyVersion = $parts -join '.'

$verb = if ($Check) { 'Checking for' } else { 'Stamping' }
Write-Host ("{0} {1}  (from '{2}')" -f $verb, $assemblyVersion, $Version) -ForegroundColor Cyan

$files = @(Get-ChildItem -Path (Join-Path $repoRoot 'src') -Recurse -Filter 'AssemblyInfo.cs' -File)
if ($files.Count -eq 0) {
    Write-Error "No AssemblyInfo.cs found under src. Has the layout changed?"
    exit 1
}

# Anchored at the start of the line so the commented-out sample is not matched, and
# spelled out in full so AssemblyNativeVersion cannot be caught by accident.
$patterns = @(
    @{ Name = 'AssemblyVersion';     Regex = '(?m)^\[assembly:\s*AssemblyVersion\("[^"]*"\)\]' },
    @{ Name = 'AssemblyFileVersion'; Regex = '(?m)^\[assembly:\s*AssemblyFileVersion\("[^"]*"\)\]' }
)

$changed = @()

foreach ($file in $files) {
    # ReadAllText, not Get-Content -Raw. On Windows PowerShell 5.1, Get-Content reads a
    # file without a BOM as Windows-1252, so the UTF-8 bytes C2 A9 for the (c) in the
    # copyright line come back as two characters, and writing them out as UTF-8 again
    # produces C3 82 C2 A9 -- a mojibake "Ac" that silently corrupts every assembly
    # whose AssemblyInfo.cs carries it. ReadAllText decodes as UTF-8 and strips a BOM
    # if present.
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF

    $original = [System.IO.File]::ReadAllText($file.FullName)
    $updated = $original

    foreach ($pattern in $patterns) {
        $replacement = '[assembly: {0}("{1}")]' -f $pattern.Name, $assemblyVersion
        $updated = [regex]::Replace($updated, $pattern.Regex, $replacement)

        if ($updated -notmatch [regex]::Escape($replacement)) {
            Write-Error ("{0}: no {1} attribute to stamp. Every AssemblyInfo.cs in this repo is expected to carry one." -f $file.FullName, $pattern.Name)
            exit 1
        }
    }

    if ($updated -ne $original) {
        $relative = $file.FullName.Substring($repoRoot.Length).TrimStart('\')
        $changed += $relative

        if (-not $Check) {
            # Written back with whatever BOM the file already had, so stamping changes the
            # two attribute lines and nothing else. (Set-Content -Encoding utf8 would add
            # a BOM unconditionally on 5.1, which is its own spurious diff.)
            [System.IO.File]::WriteAllText($file.FullName, $updated, (New-Object System.Text.UTF8Encoding $hasBom))
        }
    }
}

if ($Check) {
    if ($changed.Count -gt 0) {
        Write-Error ("{0} file(s) would change:`n  {1}" -f $changed.Count, ($changed -join "`n  "))
        exit 1
    }

    Write-Host ("All {0} assemblies already at {1}." -f $files.Count, $assemblyVersion) -ForegroundColor Green
    exit 0
}

if ($changed.Count -eq 0) {
    Write-Host ("All {0} assemblies were already at {1}; nothing to do." -f $files.Count, $assemblyVersion) -ForegroundColor Green
}
else {
    Write-Host ("Stamped {0} of {1} assemblies:" -f $changed.Count, $files.Count) -ForegroundColor Green
    $changed | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
}

# Explicit success code -- see the note in Sync-NanoFrameworkRepos.ps1.
exit 0
