---
name: smarthome-watch-serial
description: Capture raw serial boot output from a SmartHome device without needing Visual Studio's debugger. Use when a device isn't showing expected MQTT/behavior and you need to check whether it's even booting or reaching app_main().
---

# Watch device serial output

```powershell
.\scripts\Watch-DeviceSerial.ps1                          # 25s capture, resets the device first
.\scripts\Watch-DeviceSerial.ps1 -DurationSeconds 60 -NoReset
```

Opens the COM port directly at 115200 baud and prints whatever comes across — the ESP32 ROM
bootloader and nanoFramework 2nd-stage bootloader/nanoCLR startup banner are plain ASCII and
readable this way, no debugger required. Useful as a first, fast check of "did it boot, did it
reach `Calling app_main()`, is it crash-looping" before reaching for Visual Studio.

**Limitation, know this going in**: this does *not* show managed `Debug.WriteLine` output or
generally anything from the deployed application once nanoCLR hands off, unless a debugger is
attached — a session confirmed identical, byte-for-byte serial output across three separate
boots (two Debug-config, one Release-config) that all went silent right after `app_main()`, and
still couldn't determine from serial alone whether that was a real hang or the app running fine
but silently. For anything past that point — actual WiFi/MQTT/application behavior — attach the
Visual Studio debugger instead; that's the only way to see real managed-code output and
exceptions with full stack traces.

Resetting the device (the default) interrupts whatever it's currently doing, same as pressing its
reset button — not a hardware-write like `smarthome-deploy`/`smarthome-test`, but still worth a
heads-up if something else was mid-run.
