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
