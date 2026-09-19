using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using System;
using System.Diagnostics;
using System.Threading;
using SmartHome.Networking;
using SmartHome.IntegrationTests.TestSupport;

namespace SmartHome.IntegrationTests.WifiCheck
{
    // Isolated connectivity check: only verifies the device can scan for and
    // connect to the configured WiFi network. No MQTT, no sensor.
    //
    // The IntegrationTest.Pass/Fail markers are what scripts\Run-IntegrationTests.ps1
    // greps for -- emit them as soon as the outcome is known, before the idle loop.
    public class Program
    {
        public static void Main()
        {
            // NetworkHelper logs through ILogger, and the default factory is null, whose
            // logger drops everything. Without this the capture would carry the [ITEST]
            // verdict and none of the reasoning behind it -- which on the one test whose
            // whole subject is "did WiFi connect" is most of the value.
            LogDispatcher.LoggerFactory = new DebugLoggerFactory();

            try
            {
                NetworkHelper.ConnectToConfiguredNetwork();
            }
            catch (Exception ex)
            {
                IntegrationTest.Fail(typeof(Program), ex.Message);
                return;
            }

            IntegrationTest.Pass(typeof(Program), "connected to the configured WiFi network");

            while (true)
            {
                Thread.Sleep(30000);
                Debug.WriteLine("WifiTest: still connected, idling...");
            }
        }
    }
}
