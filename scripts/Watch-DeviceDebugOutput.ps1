<#
.SYNOPSIS
    Stream a device's real managed-code debug output (Debug.WriteLine, exceptions)
    to the console -- no Visual Studio needed.

.DESCRIPTION
    Builds and runs tools\DeviceDebugMonitor, a small .NET console app built on
    nanoFramework.Tools.Debugger.Net -- the same library Visual Studio's own
    nanoFramework debugger extension is built on. This is the only reliable way
    to see actual managed-code output: nanoCLR silences plain-text ESP-IDF
    logging the instant it starts (see app_main.c in the synced nf-interpreter
    sibling repo) and switches the UART to binary WireProtocol framing instead,
    so Watch-DeviceSerial.ps1 can only ever show the native boot log, never
    anything from here on.

.PARAMETER DurationSeconds
    How long to listen. Default 30.

.PARAMETER NoReboot
    Connect and just listen, without rebooting the device first. Only useful if
    you connect within a couple of seconds of another reset (e.g. right after a
    Deploy-ToDevice.ps1 flash) -- connecting even a few seconds late means you
    see nothing, not because nothing ran, but because the app already finished
    or crashed before you attached. Without this switch, the tool reboots the
    device itself right after connecting, so you're guaranteed to see the full
    boot sequence live.

.EXAMPLE
    .\scripts\Watch-DeviceDebugOutput.ps1
    .\scripts\Watch-DeviceDebugOutput.ps1 -DurationSeconds 60
    .\scripts\Deploy-ToDevice.ps1; .\scripts\Watch-DeviceDebugOutput.ps1 -NoReboot
#>

[CmdletBinding()]
param(
    [int]$DurationSeconds = 30,

    [switch]$NoReboot,

    # The monitor is a host-side tool that doesn't change between captures, so a
    # caller taking several captures in a row (Run-IntegrationTests.ps1) builds it
    # once with -BuildOnly and passes -NoBuild for every capture after that. On its
    # own this script still builds, so a plain invocation needs no ceremony.
    [switch]$NoBuild,

    [switch]$BuildOnly,

    # Stop as soon as a line containing this text arrives; -DurationSeconds then acts
    # as a timeout rather than a fixed wait.
    [string]$Until,

    # Dump the device's flash partition table instead of capturing output. The monitor
    # has supported this since the deploy-address investigation, but no script exposed
    # it -- so the only way to reach it was to hand-invoke `dotnet run`, outside the one
    # entry point per workflow this repo works by, and outside the confirm-before-
    # hardware path every other device-touching switch sits behind.
    [switch]$DumpConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Common.ps1')
Import-SmartHomeLocalEnv

$comPort = Get-RequiredEnvValue -Name 'SMARTHOME_COM_PORT'
$repoRoot = Get-SmartHomeRepoRoot
$toolProject = Join-Path $repoRoot 'tools\DeviceDebugMonitor\DeviceDebugMonitor.csproj'

if (-not (Test-Path $toolProject)) {
    Write-Error "DeviceDebugMonitor project not found: $toolProject"
    exit 1
}

$toolArgs = @($comPort, $DurationSeconds)
if ($NoReboot) {
    $toolArgs += '--no-reboot'
}
if ($Until) {
    $toolArgs += '--until'
    $toolArgs += $Until
}
if ($DumpConfig) {
    # The monitor early-returns on this, so nothing else in $toolArgs applies.
    $toolArgs += '--dump-config'
}

# Build separately from run -- a --no-build run that fails because the device
# is unreachable is a normal, expected outcome, not a reason to silently
# re-invoke the tool (which would reboot the device a second time).
if (-not $NoBuild) {
    dotnet build $toolProject --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Error "DeviceDebugMonitor failed to build (exit code $LASTEXITCODE)."
        exit $LASTEXITCODE
    }
}

if ($BuildOnly) {
    exit 0
}

dotnet run --project $toolProject --no-build -- @toolArgs
exit $LASTEXITCODE
