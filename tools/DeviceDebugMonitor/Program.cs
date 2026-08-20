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

using nanoFramework.Tools.Debugger;
using nanoFramework.Tools.Debugger.Extensions;

var positional = args.Where(a => a != "--no-reboot").ToArray();
bool noReboot = args.Contains("--no-reboot");

if (positional.Length < 1)
{
    Console.Error.WriteLine("Usage: DeviceDebugMonitor <COM port> [duration seconds] [--no-reboot]");
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

device.DebugEngine!.OnMessage += (message, text) => Console.Write(text);

bool connected = device.DebugEngine.Connect(5000, force: true, requestCapabilities: true);
if (!connected)
{
    Console.Error.WriteLine("Failed to connect debug engine to the device.");
    return 1;
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

Thread.Sleep(TimeSpan.FromSeconds(durationSeconds));

Console.WriteLine(new string('-', 69));
Console.WriteLine("Done.");
return 0;
