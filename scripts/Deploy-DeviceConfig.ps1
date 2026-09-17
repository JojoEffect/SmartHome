<#
.SYNOPSIS
    Deploy a device's configuration file to its internal storage, without rebuilding or
    reflashing anything.

.DESCRIPTION
    Everything under config\ describes where a device is *installed* rather than what it
    does -- which room a sensor is in, which pin a valve is on, where the broker lives,
    how deep a tank is. The device reads it from I:\configuration.json at boot, so
    changing one of those values is this script plus a reset, not an edit, a rebuild and
    a 90-second flash.

    What it does:
      1. Reads config\<device>.deploy.json -- nanoff's own file-deployment schema.
      2. Refuses it if it names a file that is not there, a destination that is not a
         device path, a .json payload that does not parse, or a SerialPort (see below).
      3. Writes a resolved copy to a temp file: source paths made absolute against this
         checkout, so nanoff does not resolve them against whatever directory it was
         started in.
      4. Runs `nanoff --serialport <port> --filedeployment <resolved>`, and reads its
         OUTPUT rather than only its exit code -- see the note on
         Get-FileDeploymentFailure.

    This does NOT flash firmware and does not touch the deployment partition: the files
    go over the debugger's wire protocol into the littlefs partition the firmware already
    carries, and the app on the device is left exactly as it was. It does not reset the
    device either, and a device reads its configuration once, at boot -- so nothing
    changes until the next reset. The last line printed says how.

    Two deliberate departures from what nanoff would take directly, both filled in here
    rather than committed to the manifest:

      - SourceFilePath is relative to the repository root. nanoff resolves a relative
        path against its own working directory, which is whatever shell started it.
      - There is no SerialPort. That is machine-specific and lives in
        scripts\local.env.ps1 as SMARTHOME_COM_PORT. A manifest carrying one is refused:
        a committed COM port is a per-machine value in a version-controlled file, and the
        next person's device is on a different port.

.PARAMETER Manifest
    The deployment manifest, relative to the repository root (or absolute).

.PARAMETER ResolveOnly
    Validate the manifest, print the resolved deployment, and stop. Touches no device and
    needs no COM port to be plugged in -- worth running after editing a configuration
    file, because everything it checks is everything that can be checked without the
    device.

.NOTES
    Requires:
      - nanoff CLI:  dotnet tool install -g nanoff
      - scripts\local.env.ps1 populated from the template

.EXAMPLE
    .\scripts\Deploy-DeviceConfig.ps1

.EXAMPLE
    .\scripts\Deploy-DeviceConfig.ps1 -Manifest config\room-sensor.deploy.json -ResolveOnly
#>

[CmdletBinding()]
param(
    [string]$Manifest = 'config\room-sensor.deploy.json',

    [switch]$ResolveOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Common.ps1')

# ── Everything above the dot-source guard is a declaration ────────────────────
# Same arrangement, and for the same reason, as Run-IntegrationTests.ps1: these
# functions decide what this script refuses, they are the half a desk can exercise, and
# scripts\tests reaches them by dot-sourcing the shipped file rather than by copying it.
# Anything that reads the environment, touches the disk or talks to the device belongs
# below the guard.

function Test-DeviceConfigManifestProperty {
    <#
        Whether the manifest carries this key at all -- which is a different question from
        what its value is, and the two have to be asked separately.

        A key present with an empty array reads back as $null through the accessor below:
        PowerShell unrolls a returned empty array into no output at all. So "Files is
        missing" and "Files is empty" are indistinguishable there, and they want different
        messages.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        $Object,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($null -eq $Object -or -not ($Object -is [psobject])) {
        return $false
    }

    return ($null -ne $Object.PSObject.Properties[$Name])
}

function Get-DeviceConfigManifestProperty {
    <#
        ConvertFrom-Json hands back a PSCustomObject, and under
        Set-StrictMode -Version Latest reading a property it does not carry throws
        PropertyNotFoundException rather than returning $null. Every read of a manifest
        member goes through here so a missing key becomes a message about the manifest
        instead of a stack trace about PowerShell.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        $Object,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if (-not (Test-DeviceConfigManifestProperty -Object $Object -Name $Name)) {
        return $null
    }

    return $Object.PSObject.Properties[$Name].Value
}

function Get-DeviceConfigManifestError {
    <#
        Returns why the manifest cannot be deployed, or $null when it can.

        Every check here is one that can be made without the device, and each of them has
        a failure that is otherwise only visible as a file quietly not arriving: nanoff
        reports a per-file problem on stdout and still exits 0 (FileDeploymentManager
        .DeployAsync in the pinned nanoFirmwareFlasher checkout returns ExitCodes.OK
        whatever happens to an individual file), so a manifest that was never going to
        work is refused here rather than read as a successful deployment there.

        The payload parse is the one that earns its place for #37's sake: a trailing
        comma added at a tank, at night, is caught at the desk instead of by a device
        that comes up alerting an hour later.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [AllowNull()]
        [string]$Json,

        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    if ([string]::IsNullOrWhiteSpace($Json)) {
        return "$Source is empty."
    }

    $manifest = $null
    try {
        $manifest = $Json | ConvertFrom-Json
    }
    catch {
        return "$Source is not valid JSON: $($_.Exception.Message)"
    }

    if ($null -eq $manifest) {
        return "$Source parsed to nothing."
    }

    if (Test-DeviceConfigManifestProperty -Object $manifest -Name 'SerialPort') {
        return @"
$Source declares a SerialPort. That is a per-machine value and this file is version-controlled,
so the port comes from SMARTHOME_COM_PORT in scripts\local.env.ps1 instead. Remove the key.
"@
    }

    if (-not (Test-DeviceConfigManifestProperty -Object $manifest -Name 'Files')) {
        return "$Source has no 'Files' array; there is nothing to deploy."
    }

    # @() around it because a single-entry JSON array comes back as one object rather than
    # as an array, and .Count on a PSCustomObject is not the count of anything.
    $files = @(Get-DeviceConfigManifestProperty -Object $manifest -Name 'Files')
    if ($files.Count -eq 0) {
        return "$Source has an empty 'Files' array; there is nothing to deploy."
    }

    for ($i = 0; $i -lt $files.Count; $i++) {
        $entry = $files[$i]
        $label = "$Source, Files[$i]"

        $destination = Get-DeviceConfigManifestProperty -Object $entry -Name 'DestinationFilePath'
        if ([string]::IsNullOrWhiteSpace($destination)) {
            return "$label has no DestinationFilePath."
        }

        # 'I:\configuration.json', not 'configuration.json'. The device has no working
        # directory to resolve a bare name against, and nanoff would pass it through.
        if ($destination -notmatch '^[A-Za-z]:\\') {
            return "$label has DestinationFilePath '$destination'; a device path is rooted on a drive, e.g. 'I:\configuration.json'."
        }

        $source = Get-DeviceConfigManifestProperty -Object $entry -Name 'SourceFilePath'
        if ([string]::IsNullOrWhiteSpace($source)) {
            # nanoff reads an absent source as "delete this file from the device". That is
            # a real operation and not one this script has any business doing silently
            # from a manifest whose name says deploy.
            return "$label has no SourceFilePath. This script deploys files; it does not delete them from the device."
        }

        if ([System.IO.Path]::IsPathRooted($source)) {
            return "$label has an absolute SourceFilePath '$source'; paths in a manifest are relative to the repository root so the same file works from any checkout."
        }

        $resolved = Join-Path $RepoRoot $source
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            return "$label names a file that is not there: $resolved"
        }

        if ($source -match '\.json$') {
            try {
                $payload = Get-Content -LiteralPath $resolved -Raw
                if ([string]::IsNullOrWhiteSpace($payload)) {
                    return "$resolved is empty; a device reading it would come up alerting."
                }

                $payload | ConvertFrom-Json | Out-Null
            }
            catch {
                return "$resolved is not valid JSON: $($_.Exception.Message)"
            }
        }
    }

    return $null
}

function ConvertTo-DeviceConfigDeployment {
    <#
        Turns a validated manifest into the object nanoff is actually handed: the port
        filled in, and every source path absolute.

        Assumes Get-DeviceConfigManifestError has already passed. Splitting the two keeps
        each one readable -- the validator is a list of refusals with a message each, and
        this is the transformation with none.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Json,

        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,

        [Parameter(Mandatory = $true)]
        [string]$ComPort
    )

    $manifest = $Json | ConvertFrom-Json
    $files = @()

    foreach ($entry in @(Get-DeviceConfigManifestProperty -Object $manifest -Name 'Files')) {
        $source = Get-DeviceConfigManifestProperty -Object $entry -Name 'SourceFilePath'

        # GetFullPath rather than the joined path as-is: nanoff prints the source in both
        # its progress and its error lines, and 'C:\repo\config\..\config\x.json' in an
        # error message is a second thing to work out while reading it.
        $files += [ordered]@{
            DestinationFilePath = [string](Get-DeviceConfigManifestProperty -Object $entry -Name 'DestinationFilePath')
            SourceFilePath      = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $source))
        }
    }

    return [ordered]@{
        SerialPort = $ComPort
        Files      = $files
    }
}

function Get-FileDeploymentFailure {
    <#
        Returns why the deployment did not land, or $null when it did.

        The exit code is not enough, and that is measured rather than assumed: in
        nanoFirmwareFlasher's FileDeploymentManager.DeployAsync, a file that fails to
        upload prints "Error deploying content file ..." and the loop continues; the
        method returns ExitCodes.OK regardless, so nanoff exits 0 having deployed nothing.
        Its own README says as much -- "If a file can't be uploaded because of a problem,
        the deployment of the other files will continue and an error will be displayed."

        So the output is read three ways: the exit code for the failures that do set one
        (no device, device stuck in the initialised state), the error lines for the ones
        that do not, and a count of the files that actually reported OK for anything
        neither of those catches. The count is what turns "nanoff printed nothing at all"
        from a silent success into a failure.
    #>
    param(
        [string[]]$Output,

        [Parameter(Mandatory = $true)]
        [int]$ExitCode,

        [Parameter(Mandatory = $true)]
        [int]$ExpectedFileCount
    )

    $lines = @($Output | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })

    $errors = @($lines | Where-Object { $_ -match '^\s*(Error|Exception) deploying content file' })
    if ($errors.Count -gt 0) {
        return "nanoff could not deploy $($errors.Count) file(s): " + (($errors | ForEach-Object { $_.Trim() }) -join '; ')
    }

    if ($ExitCode -ne 0) {
        return "nanoff exited $ExitCode without deploying the files."
    }

    # 'Deploying file <source> to <destination>...OK' -- Write then WriteLine, so the
    # confirmation lands on the end of the same line.
    $deployed = @($lines | Where-Object { $_ -match 'Deploying file .+\.\.\.\s*OK\s*$' }).Count
    if ($deployed -lt $ExpectedFileCount) {
        return "nanoff exited 0 but reported only $deployed of $ExpectedFileCount file(s) deployed; nothing can be assumed about the rest."
    }

    return $null
}

# $MyInvocation.InvocationName is '.' for every spelling of a dot-source and the path or
# '&' for every spelling of a run. `return`, not `exit`: at file scope `exit` in a
# dot-sourced script ends the host.
if ($MyInvocation.InvocationName -eq '.') {
    return
}

Import-SmartHomeLocalEnv

$repoRoot = Get-SmartHomeRepoRoot

$manifestPath = if ([System.IO.Path]::IsPathRooted($Manifest)) { $Manifest } else { Join-Path $repoRoot $Manifest }

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    Write-Error @"
Manifest not found: $manifestPath
The manifests live in config\ -- one <device>.deploy.json per device. See config\README.md.
"@
    exit 1
}

$manifestJson = Get-Content -LiteralPath $manifestPath -Raw

$manifestError = Get-DeviceConfigManifestError -Json $manifestJson -Source $manifestPath -RepoRoot $repoRoot
if ($manifestError) {
    Write-Error $manifestError
    exit 1
}

# After the validation, deliberately: -ResolveOnly is the desk check, and a run that
# cannot name a COM port should still be able to tell you the manifest is wrong.
$comPort = if ($ResolveOnly) { '<SMARTHOME_COM_PORT>' } else { Get-RequiredEnvValue -Name 'SMARTHOME_COM_PORT' }

$deployment = ConvertTo-DeviceConfigDeployment -Json $manifestJson -RepoRoot $repoRoot -ComPort $comPort
$fileCount = @($deployment['Files']).Count

Write-Host "Configuration deployment from $manifestPath" -ForegroundColor Cyan
foreach ($file in $deployment['Files']) {
    Write-Host ("  {0}" -f $file['SourceFilePath']) -ForegroundColor DarkGray
    Write-Host ("    -> {0}" -f $file['DestinationFilePath']) -ForegroundColor DarkGray
}

if ($ResolveOnly) {
    Write-Host ""
    Write-Host "-ResolveOnly: the manifest and every file it names are valid. Nothing was deployed." -ForegroundColor Green
    exit 0
}

$nanoff = Get-Command nanoff -ErrorAction SilentlyContinue
if (-not $nanoff) {
    Write-Error @"
nanoff not found on PATH.
Install it with:  dotnet tool install -g nanoff
Then restart this shell so the new PATH takes effect.
"@
    exit 1
}

# The resolved manifest is written next to the temp directory rather than into config\:
# it carries this machine's COM port and this checkout's absolute paths, and neither
# belongs beside a version-controlled file where it can be committed by accident.
$resolvedManifest = Join-Path ([System.IO.Path]::GetTempPath()) ("smarthome-filedeployment-{0}.json" -f $PID)

try {
    # -Depth because ConvertTo-Json flattens past 2 levels by default, and Files is an
    # array of objects -- at the default the entries come out as type names.
    $deployment | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resolvedManifest -Encoding UTF8

    Write-Host ""
    Write-Host "Deploying to $comPort via nanoff..." -ForegroundColor Cyan

    # stdout captured so the failure check below can read it; stderr deliberately left to
    # flow to the console. Piping a native tool through 2>&1 in Windows PowerShell 5.1
    # wraps its ordinary stderr in a NativeCommandError and sets $? to false, which is how
    # a healthy tool gets read as a failed one.
    $output = nanoff --serialport $comPort --filedeployment $resolvedManifest
    $nanoffExit = $LASTEXITCODE

    $output | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
}
finally {
    Remove-Item -LiteralPath $resolvedManifest -Force -ErrorAction SilentlyContinue
}

$failure = Get-FileDeploymentFailure -Output $output -ExitCode $nanoffExit -ExpectedFileCount $fileCount
if ($failure) {
    Write-Error @"
$failure
Nothing can be assumed about what is on the device now. If nanoff could not reach it, check that
nothing else holds the port -- Visual Studio's device window and a running Watch-Device* capture
both do.
"@
    exit 1
}

Write-Host ""
Write-Host "Deployed $fileCount file(s) to the device on $comPort." -ForegroundColor Green
Write-Host "A device reads its configuration once, at boot, so reset it before this takes effect:" -ForegroundColor Cyan
Write-Host "  .\scripts\Watch-DeviceDebugOutput.ps1 -DurationSeconds 20" -ForegroundColor Cyan

exit 0
