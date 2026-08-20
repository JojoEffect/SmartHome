using System;
using System.Diagnostics;
using System.Threading;
using TestSupport;

namespace WifiTest
{
    // Isolated connectivity check: only verifies the device can scan for and
    // connect to the configured WiFi network. No MQTT, no sensor.
    //
    // The IntegrationTest.Pass/Fail markers are what scripts\Run-IntegrationTests.ps1
    // greps for -- emit them as soon as the outcome is known, before the idle loop.
    public class Program
    {
        private const string TestName = "WifiTest";

        public static void Main()
        {
            try
            {
                NetworkHelper.ConnectToConfiguredNetwork();
            }
            catch (Exception ex)
            {
                IntegrationTest.Fail(TestName, ex.Message);
                return;
            }

            IntegrationTest.Pass(TestName, "connected to the configured WiFi network");

            while (true)
            {
                Thread.Sleep(30000);
                Debug.WriteLine("WifiTest: still connected, idling...");
            }
        }
    }
}
