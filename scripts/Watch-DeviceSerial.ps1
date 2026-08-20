<#
.SYNOPSIS
    Capture raw serial output from the device -- native boot log, no debugger needed.

.DESCRIPTION
    Opens the configured COM port directly (not through nanoff or the VS debugger's
    WireProtocol channel) and captures whatever comes across at 115200 baud. The
    ESP32/nanoFramework native boot log (ROM bootloader, 2nd-stage bootloader,
    nanoCLR startup through "Calling app_main()") is plain ASCII at this baud rate
    and readable without Visual Studio.

    This does NOT show managed Debug.WriteLine output or anything after nanoCLR
    hands off to the deployed application without a debugger attached -- for that,
    attach the Visual Studio debugger instead. Treat this as a first check ("did it
    boot, did it reach app_main, is it crash-looping") before reaching for VS.

.NOTES
    Opening the port and toggling RTS resets the device (same mechanism nanoff's
    post-deploy "Hard resetting via RTS pin" uses) -- this restarts whatever was
    running, same as pressing the board's reset button. Not a hardware-write
    action like Deploy-ToDevice.ps1/Run-Tests.ps1, but it does interrupt current
    execution, so mention that if running this while something else was mid-test.

.EXAMPLE
    .\scripts\Watch-DeviceSerial.ps1
    .\scripts\Watch-DeviceSerial.ps1 -DurationSeconds 60 -NoReset
#>

[CmdletBinding()]
param(
    [int]$DurationSeconds = 25,

    # Skip the RTS reset and just listen to whatever the device is already doing.
    [switch]$NoReset
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Common.ps1')
Import-SmartHomeLocalEnv

$comPort = Get-RequiredEnvValue -Name 'SMARTHOME_COM_PORT'

$port = New-Object System.IO.Ports.SerialPort $comPort, 115200, ([System.IO.Ports.Parity]::None), 8, ([System.IO.Ports.StopBits]::One)
$port.ReadTimeout = 500

try {
    $port.Open()
}
catch {
    Write-Error "Could not open ${comPort} : $_`nIs another process (nanoff, Visual Studio, a previous run of this script) holding it open?"
    exit 1
}

try {
    if (-not $NoReset) {
        Write-Host "Resetting device on $comPort via RTS toggle..." -ForegroundColor Cyan
        $port.RtsEnable = $true
        Start-Sleep -Milliseconds 200
        $port.RtsEnable = $false
    }

    Write-Host "Capturing $comPort for ${DurationSeconds}s (Ctrl+C to stop early)..." -ForegroundColor Cyan
    Write-Host ('-' * 69)

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $buf = New-Object byte[] 4096
    $totalBytes = 0

    while ($sw.Elapsed.TotalSeconds -lt $DurationSeconds) {
        try {
            $n = $port.Read($buf, 0, $buf.Length)
            if ($n -gt 0) {
                $totalBytes += $n
                $text = [System.Text.Encoding]::ASCII.GetString($buf, 0, $n)
                Write-Host -NoNewline $text
            }
        }
        catch [System.TimeoutException] {
            # no data right now, keep waiting
        }
    }

    Write-Host ""
    Write-Host ('-' * 69)
    Write-Host "Captured $totalBytes bytes over ${DurationSeconds}s." -ForegroundColor Cyan
    if ($totalBytes -eq 0) {
        Write-Host "Nothing received -- device may not be booting, or is on a different COM port." -ForegroundColor Yellow
    }
}
finally {
    $port.Close()
}
