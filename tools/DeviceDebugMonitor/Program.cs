// Minimal CLI equivalent of Visual Studio's nanoFramework debug Output window.
// Connects to a real device over its COM port using the same
// nanoFramework.Tools.Debugger.Net library VS's extension is built on, and prints
// decoded debug messages (Debug.WriteLine, unhandled exceptions) to the console --
// the actual managed-code output that Watch-DeviceSerial.ps1 cannot see, since
// nanoCLR switches the UART to binary WireProtocol framing right at app_main().
//
// Usage: DeviceDebugMonitor <COM port> [duration seconds, default 30] [--no-reboot]
//
// --no-reboot: connect and just listen, without triggering a CLR reboot. Use this
// right after a fresh nanoff flash (which already hardware-resets the device) --
// rebooting again on top of that double-triggers execution and can leave native
// peripherals (e.g. WiFi) in a busy state a single clean boot wouldn't hit.
//
// --erase-deployment <keep-bytes>: not a capture at all. Reports the deploy
// partition's real geometry, and makes sure nothing but erased flash lies past the
// first <keep-bytes> bytes of it. That is what lets Deploy-ToDevice.ps1 flash a bare
// image instead of guessing how far to pad it -- see the "Clearing the deployment
// area" section of CLAUDE.md. It stops the CLR and leaves it stopped, so the device
// runs nothing again until it is flashed or reset.

using nanoFramework.Tools.Debugger;
using nanoFramework.Tools.Debugger.Extensions;
using nanoFramework.Tools.Debugger.WireProtocol;

// --until <text>: stop as soon as a line containing <text> arrives, instead of
// sitting out the whole duration. The duration then acts as a timeout rather than a
// sleep, which is most of the integration suite's wall clock: a device that reports
// in 12s used to hold the port for the full 75s window anyway.
string? until = null;
var untilIndex = Array.IndexOf(args, "--until");
if (untilIndex >= 0 && untilIndex + 1 < args.Length)
{
    until = args[untilIndex + 1];
}

// --erase-deployment <keep-bytes>: how many bytes from the start of the deploy
// partition the caller is about to write itself. Everything past them has to be
// erased flash for the CLR not to find a previous, larger app's tail there.
long eraseKeepBytes = -1;
var eraseIndex = Array.IndexOf(args, "--erase-deployment");
if (eraseIndex >= 0)
{
    if (eraseIndex + 1 >= args.Length || !long.TryParse(args[eraseIndex + 1], out eraseKeepBytes) || eraseKeepBytes < 0)
    {
        Console.Error.WriteLine("--erase-deployment needs a non-negative byte count.");
        return 1;
    }
}

var positional = args
    .Where((a, i) => a != "--no-reboot"
                     && a != "--dump-config"
                     && a != "--until"
                     && a != "--erase-deployment"
                     && !(untilIndex >= 0 && i == untilIndex + 1)
                     && !(eraseIndex >= 0 && i == eraseIndex + 1))
    .ToArray();
bool noReboot = args.Contains("--no-reboot");
bool dumpConfig = args.Contains("--dump-config");
bool eraseDeployment = eraseIndex >= 0;

if (positional.Length < 1)
{
    Console.Error.WriteLine("Usage: DeviceDebugMonitor <COM port> [duration seconds] [--no-reboot] [--dump-config] [--erase-deployment <keep-bytes>]");
    return 1;
}

string comPort = positional[0];
int durationSeconds = positional.Length > 1 && int.TryParse(positional[1], out var parsedDuration) ? parsedDuration : 30;

Console.WriteLine($"Watching for a nanoFramework device on {comPort}...");

var portManager = PortBase.CreateInstanceForSerial(startDeviceWatchers: true);

NanoDeviceBase? device = null;
var deadline = DateTime.UtcNow.AddSeconds(15);
while (device is null && DateTime.UtcNow < deadline)
{
    device = portManager.NanoFrameworkDevices.FirstOrDefault(dev =>
        dev.ConnectionId?.Contains(comPort, StringComparison.OrdinalIgnoreCase) == true ||
        dev.Description?.Contains(comPort, StringComparison.OrdinalIgnoreCase) == true);

    if (device is null)
    {
        Thread.Sleep(500);
    }
}

if (device is null && portManager.NanoFrameworkDevices.Count == 1)
{
    device = portManager.NanoFrameworkDevices[0];
}

if (device is null)
{
    Console.Error.WriteLine($"No nanoFramework device found on {comPort} after 15s.");
    Console.Error.WriteLine($"Devices found: {portManager.NanoFrameworkDevices.Count}");
    foreach (var d in portManager.NanoFrameworkDevices)
    {
        Console.Error.WriteLine($"  - {d.Description} ({d.ConnectionId})");
    }
    return 1;
}

Console.WriteLine($"Found device: {device.Description}. Connecting debug engine...");

// The device streams in chunks that don't align to lines, so match against a rolling
// buffer rather than each chunk.
var matched = new ManualResetEventSlim(false);
var seen = new System.Text.StringBuilder();

// Not subscribed in --erase-deployment mode. That mode's stdout is a contract -- the
// caller parses `DEPLOYMENT start=...` with an anchored pattern -- and the device is
// still running its app between Connect and the pause below. Console.Write emits no
// trailing newline, so one Debug.WriteLine landing in that window would prefix the
// geometry line and make it unparseable, which reads downstream as a device that
// reported no geometry rather than as output that got mixed together.
if (!eraseDeployment)
{
    device.DebugEngine!.OnMessage += (message, text) =>
    {
        Console.Write(text);

        if (until is null || matched.IsSet)
        {
            return;
        }

        lock (seen)
        {
            seen.Append(text);
            if (seen.ToString().Contains(until))
            {
                matched.Set();
            }

            // Keep the buffer bounded; anything older than a couple of lines cannot
            // start a match that isn't already complete.
            if (seen.Length > 4096)
            {
                seen.Remove(0, seen.Length - 1024);
            }
        }
    };
}

bool connected = device.DebugEngine!.Connect(5000, force: true, requestCapabilities: true);
if (!connected)
{
    Console.Error.WriteLine("Failed to connect debug engine to the device.");
    return 1;
}

if (eraseDeployment)
{
    // The deploy partition's real geometry, straight from the device, rather than the
    // hand-measured constants Deploy-ToDevice.ps1 carries.
    //
    // From the flash sector map and NOT from Debugging_Deployment_Status, which looks
    // like the more direct question and does not work here: on this ESP32 it answers
    // with no usable geometry, and nothing in nf-debugger's own ESP32 path depends on
    // it either -- DeploymentExecuteFull is the only caller, and the ESP32 reports
    // IncrementalDeployment, so DeploymentExecuteIncremental (which reads this same
    // sector map) is what actually runs. Measured on the device, not reasoned about.
    //
    // Either way it is the partition that is being reported, NOT how much of it is in
    // use. Nothing reports that: Monitor_DeploymentMap, the command that looks like it
    // would, is a stub in the firmware that replies with an empty payload, and reading
    // the region back is refused outright (CheckPermission has no DEPLOYMENT case for
    // AccessMemory_Read). Erasing it is permitted, so this asks the device to make the
    // region blank rather than asking it what is in there.
    var sectorMap = device.DebugEngine.GetFlashSectorMap();
    var deploymentSectors = (sectorMap ?? [])
        .Where(s => (s.Flags & Commands.Monitor_FlashSectorMap.c_MEMORY_USAGE_MASK)
                    == Commands.Monitor_FlashSectorMap.c_MEMORY_USAGE_DEPLOYMENT)
        .OrderBy(s => s.StartAddress)
        .ToList();

    if (deploymentSectors.Count == 0)
    {
        Console.Error.WriteLine("The device's flash sector map lists no deployment region.");
        return 1;
    }

    // Summed rather than "the first one": the map is a list of sector groups, and a
    // target is free to describe its deployment area as several. Contiguity is checked
    // rather than assumed -- a gap would make one length a lie about two regions, and
    // erasing from the start of the first would then say nothing about the second.
    uint storageStart = deploymentSectors[0].StartAddress;
    uint storageLength = 0;
    foreach (var sector in deploymentSectors)
    {
        if (sector.StartAddress != storageStart + storageLength)
        {
            Console.Error.WriteLine($"The device reports a deployment area in more than one piece (a gap before 0x{sector.StartAddress:X8}); this tool only handles a contiguous one.");
            return 1;
        }

        storageLength += sector.NumBlocks * sector.BytesPerBlock;
    }

    if (storageLength == 0)
    {
        Console.Error.WriteLine("The device reports a deployment region of no size.");
        return 1;
    }

    // One machine-readable line, so the caller can cross-check its own flash address
    // against what the device says -- a mismatch there is the failure that made every
    // nanoff deploy silently never run (see -DeployAddress in Deploy-ToDevice.ps1).
    Console.WriteLine($"DEPLOYMENT start=0x{storageStart:X8} length={storageLength}");

    if (eraseKeepBytes > storageLength)
    {
        Console.Error.WriteLine($"Asked to keep {eraseKeepBytes} bytes, but the deployment partition is only {storageLength}.");
        return 1;
    }

    // Nothing lies past an image that fills the partition, so there is nothing to erase
    // -- and asking anyway would be actively dangerous. EraseMemory(start + length, 0)
    // addresses one byte past the region, and CheckPermission lists BLOCKTYPE_CONFIG
    // alongside BLOCKTYPE_DEPLOYMENT under AccessMemory_Erase, so on a layout where
    // config abuts deploy that call resolves into the config partition and takes the
    // device's WiFi profiles and certificates with it. Handled here rather than left to
    // the caller: Deploy-ToDevice.ps1's own fit check is `-gt` too, by design -- an image
    // that exactly fills the partition is a legal deploy.
    if (eraseKeepBytes == storageLength)
    {
        Console.WriteLine($"DEPLOYMENT blank-past={eraseKeepBytes}");
        return 0;
    }

    // Same sequence VS's own deploy uses before it erases (DeploymentExecute in
    // nf-debugger's WireProtocol\Engine.cs): stop the CLR first. It executes assemblies
    // straight out of this memory-mapped flash, so erasing under a running app is not
    // something to find out about halfway through.
    var executionState = device.DebugEngine.GetExecutionMode();
    if (executionState == Commands.DebuggingExecutionChangeConditions.State.Unknown)
    {
        Console.Error.WriteLine("Could not read the device's execution state.");
        return 1;
    }

    if (!executionState.IsDeviceInStoppedState() && !device.DebugEngine.PauseExecution())
    {
        Console.Error.WriteLine("Could not stop the CLR before erasing the deployment partition.");
        return 1;
    }

    // The firmware short-circuits when the region is already blank (IsBlockErased runs
    // from this address to the end of the partition), so this costs a round trip and
    // nothing else on a device whose current app is at least as big as the next one.
    // When it is NOT blank, ESP32 erases the WHOLE partition rather than the tail --
    // Esp32FlashDriver_EraseBlock ignores the address and calls esp_partition_erase_range
    // over the whole thing -- so the device is left with no app at all until the caller's
    // flash lands. That is the caller's problem to sequence, and it is why this runs
    // immediately before the flash rather than at some tidy-up point.
    var (eraseError, eraseOk) = device.DebugEngine.EraseMemory(
        (uint)(storageStart + eraseKeepBytes),
        (uint)(storageLength - eraseKeepBytes));

    if (!eraseOk || eraseError != AccessMemoryErrorCodes.NoError)
    {
        Console.Error.WriteLine($"Erase of the deployment partition past {eraseKeepBytes} bytes failed: {eraseError}.");
        return 1;
    }

    // The CLR was stopped above and is deliberately left that way -- resuming one whose
    // assemblies have just been erased is not something to do on a hunch. Said out loud
    // because the caller that flashes next resets the device anyway, so it is only a
    // hand-run of this mode that is left looking at a device doing nothing.
    Console.WriteLine("The CLR is left stopped; flash the device or reset it to run again.");

    // Deliberately not "erased": the device does not report which of the two happened,
    // and both leave the caller with the same guarantee. Claiming an erase that did not
    // happen would be the more useful-sounding, less true line.
    Console.WriteLine($"DEPLOYMENT blank-past={eraseKeepBytes}");
    return 0;
}

if (dumpConfig)
{
    // Read-only diagnostic: is the "config" partition (WiFi profiles, certs) at
    // 0x3C0000 actually blank/erased, or does it hold real filesystem content?
    // Monitor_ReadMemory needs the device halted (not running user code) --
    // reboot to get it into the paused/initialize state, but deliberately don't
    // resume execution afterward, since we're not validating app behavior here.
    Console.WriteLine("Rebooting device to a halted state for a safe memory read...");
    device.DebugEngine.RebootDevice(RebootOptions.NormalReboot);
    Thread.Sleep(1500);

    const uint configAddress = 0x3C0000;
    const uint dumpLength = 512;

    Console.WriteLine($"Reading {dumpLength} bytes from 0x{configAddress:X} (config partition)...");
    var (buffer, errorCode, success) = device.DebugEngine.ReadMemory(configAddress, dumpLength);

    if (!success)
    {
        Console.Error.WriteLine($"ReadMemory failed, error code 0x{errorCode:X8}.");
        return 1;
    }

    int blankCount = buffer.Count(b => b == 0xFF);
    int zeroCount = buffer.Count(b => b == 0x00);
    Console.WriteLine($"Read {buffer.Length} bytes. 0xFF (erased) bytes: {blankCount}, 0x00 bytes: {zeroCount}.");

    if (blankCount == buffer.Length)
    {
        Console.WriteLine("=> ENTIRELY 0xFF: this region is fully erased/blank flash. No filesystem content at all.");
    }
    else if (zeroCount == buffer.Length)
    {
        Console.WriteLine("=> ENTIRELY 0x00: this region was zeroed out, not left in an erased state.");
    }
    else
    {
        Console.WriteLine("=> NOT blank: contains varied non-erased data (consistent with real filesystem content).");
    }

    Console.WriteLine();
    Console.WriteLine("First 256 bytes (hex):");
    for (int i = 0; i < Math.Min(256, buffer.Length); i += 16)
    {
        var chunk = buffer.Skip(i).Take(16);
        Console.WriteLine($"  {i:X4}: {string.Join(" ", chunk.Select(b => b.ToString("X2")))}");
    }

    return 0;
}

if (noReboot)
{
    Console.WriteLine("Connected. Listening without rebooting (--no-reboot):");
}
else
{
    Console.WriteLine("Connected. Rebooting device to capture the full boot sequence live...");
}
Console.WriteLine(new string('-', 69));

if (!noReboot)
{
    device.DebugEngine.RebootDevice(RebootOptions.NormalReboot);

    // Give the reboot a moment before checking initialize state -- otherwise we
    // might observe the pre-reboot state.
    Thread.Sleep(1000);
}

if (device.DebugEngine.IsDeviceInInitializeState())
{
    Console.WriteLine("(device is paused waiting for debugger -- resuming execution)");
    device.DebugEngine.ResumeExecution();
}

if (until is null)
{
    Thread.Sleep(TimeSpan.FromSeconds(durationSeconds));
}
else if (matched.Wait(TimeSpan.FromSeconds(durationSeconds)))
{
    Console.WriteLine();
    Console.WriteLine($"(matched '{until}' -- stopping early)");
}
else
{
    Console.WriteLine();
    Console.WriteLine($"(no line containing '{until}' within {durationSeconds}s)");
}

Console.WriteLine(new string('-', 69));
Console.WriteLine("Done.");
return 0;
