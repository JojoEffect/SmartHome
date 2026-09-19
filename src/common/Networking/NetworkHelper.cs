using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using System;
using System.Net.NetworkInformation;
using System.Threading;
using nanoFramework.Networking;

namespace SmartHome.Networking
{
    /// <summary>
    /// Shared WiFi connect helper for every project that needs the network: the real
    /// device apps (RoomSensor) and the integration tests (WifiCheck, MqttCheck) alike.
    /// </summary>
    public static class NetworkHelper
    {
        /// <remarks>
        /// Resolved per call rather than cached in a static field. A static field is
        /// initialised on first touch of this type, which can be before the app has set
        /// <c>LogDispatcher.LoggerFactory</c> -- and it would then cache the null-factory
        /// logger for the life of the process, silently swallowing everything afterwards.
        /// This method runs once per boot, so the one allocation costs nothing and the
        /// ordering trap disappears.
        /// </remarks>
        private static ILogger Logger => LogDispatcher.GetLogger("SmartHome.Networking.NetworkHelper");

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
        /// association is already in flight. RoomSensor carried that loop until
        /// 2026-08-20 and only appeared to work because running under the VS debugger
        /// delays Main() by a couple of seconds, so auto-connect has already finished
        /// and the manual path is never taken; on a plain reset it failed to join the
        /// network at all, repeatedly, with error 5. WifiNetworkHelper waits for the
        /// interface to actually come up instead of racing it.
        ///
        /// requiresDateTime is false on purpose: that would additionally block on
        /// SNTP, which needs working internet access. Nothing here talks past the
        /// LAN broker, so a valid clock isn't needed and requiring one would add an
        /// unrelated failure mode.
        /// </remarks>
        public static void ConnectToConfiguredNetwork(int timeoutMilliseconds = 60000)
        {
            var logger = Logger;

            logger.LogInformation("Connecting to WiFi using the device's stored configuration...");

            var cancellation = new CancellationTokenSource(timeoutMilliseconds);
            bool success = WifiNetworkHelper.Reconnect(requiresDateTime: false, token: cancellation.Token);

            if (!success)
            {
                var message = $"WiFi connect failed. Status: {WifiNetworkHelper.Status}.";
                logger.LogError(message);

                if (WifiNetworkHelper.HelperException != null)
                {
                    logger.LogError($"HelperException: {WifiNetworkHelper.HelperException}");
                }

                throw new Exception(message);
            }

            // Purely informational, and therefore guarded: the connect has already
            // succeeded by this point, so nothing here is allowed to fail it. An
            // unguarded [0] on an empty interface list threw out of a *successful*
            // connect -- WifiCheck caught it and reported FAIL for a network the device
            // had actually joined, and RoomSensor rethrew from Main and crash-looped.
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();

                if (interfaces.Length == 0)
                {
                    logger.LogInformation("Connected to WiFi network (no network interface reported an address).");
                    return;
                }

                // Index 0 is the station interface on this board. On a board exposing
                // several (AP plus station, or Ethernet) it is not guaranteed to be the
                // one that just connected, so treat the value as a hint, not a fact.
                logger.LogInformation($"Connected to WiFi network with IP address {interfaces[0].IPv4Address}");
            }
            catch (Exception ex)
            {
                logger.LogInformation($"Connected to WiFi network (could not read the IP address: {ex.Message}).");
            }
        }
    }
}
