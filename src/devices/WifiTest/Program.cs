using System.Diagnostics;
using System.Threading;
using TestSupport;

namespace WifiTest
{
    // Isolated connectivity check: only verifies the device can scan for and
    // connect to the configured WiFi network. No MQTT, no sensor.
    public class Program
    {
        public static void Main()
        {
            NetworkHelper.ConnectToConfiguredNetwork();

            Debug.WriteLine("WifiTest: connected successfully.");

            while (true)
            {
                Thread.Sleep(30000);
                Debug.WriteLine("WifiTest: still connected, idling...");
            }
        }
    }
}
