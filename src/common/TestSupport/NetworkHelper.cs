using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Threading;
using nanoFramework.Networking;

namespace TestSupport
{
    /// <summary>
    /// Shared WiFi connect helper for the device test projects (WifiTest, MqttTest).
    /// </summary>
    public static class NetworkHelper
    {
        /// <summary>
        /// Connects using the WiFi profile already stored on the device (set via
        /// Visual Studio's Device Explorer -> Edit network configuration). Blocks
        /// until connected or the timeout elapses.
        /// </summary>
        /// <remarks>
        /// Uses nanoFramework's own <see cref="WifiNetworkHelper"/> rather than a
        /// hand-rolled scan/connect loop. The hand-rolled version (copied from the
        /// official BasicExample.WiFi sample) races the ESP32's own auto-connect:
        /// the sample checks for an IP once, immediately, and if the radio hasn't
        /// finished associating yet it falls into a manual Connect() -- which then
        /// fails with WifiConnectionStatus.UnspecifiedFailure precisely BECAUSE an
        /// association is already in flight. RoomSensor only appears to work with
        /// that code because running under the VS debugger delays Main() by a couple
        /// of seconds, so auto-connect has already finished and the manual path is
        /// never taken. WifiNetworkHelper waits for the interface to actually come
        /// up instead of racing it.
        ///
        /// requiresDateTime is false on purpose: that would additionally block on
        /// SNTP, which needs working internet access. These tests only talk to a
        /// broker on the LAN, so a valid clock isn't needed and requiring one would
        /// add an unrelated failure mode.
        /// </remarks>
        public static void ConnectToConfiguredNetwork(int timeoutMilliseconds = 60000)
        {
            Debug.WriteLine("Connecting to WiFi using the device's stored configuration...");

            var cancellation = new CancellationTokenSource(timeoutMilliseconds);
            bool success = WifiNetworkHelper.Reconnect(requiresDateTime: false, token: cancellation.Token);

            if (!success)
            {
                var message = $"WiFi connect failed. Status: {WifiNetworkHelper.Status}.";
                Debug.WriteLine(message);

                if (WifiNetworkHelper.HelperException != null)
                {
                    Debug.WriteLine($"HelperException: {WifiNetworkHelper.HelperException}");
                }

                throw new Exception(message);
            }

            var ipAddress = NetworkInterface.GetAllNetworkInterfaces()[0].IPv4Address;
            Debug.WriteLine($"Connected to WiFi network with IP address {ipAddress}");
        }
    }
}
