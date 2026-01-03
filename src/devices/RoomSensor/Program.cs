
using HomieNano.Version4;
using HomieNano.Version4.Builder;
using HomieNano.Version4.Properties;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using System;
using System.Device.Wifi;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Threading;

namespace RoomSensor
{
    public class Program
    {
        private static FloatProperty _temperatureProperty;
        private static FloatProperty _humidityProperty;
        private static FloatProperty _pressureProperty;
        private static ILogger _logger;

        // private static GpioController s_GpioController;
        public static void Main()
        {
            try
            {
                LogDispatcher.LoggerFactory = new DebugLoggerFactory();

                _logger = LogDispatcher.LoggerFactory.CreateLogger("MainLogger");

                SetupAndConnectNetwork();

                var device = SetupHomieDevice();
                var mqttClient = SetupMqttClient();
                mqttClient.Connect(Constants.DeviceName);
                var homieClient = new HomieClient(device, mqttClient);

                homieClient.Connect();

                // Simulate sensor data updates
                while (true)
                {
                    _logger.LogDebug("Updating sensor values...");
                    // In a real application, replace this with actual sensor readings
                    var random = new Random();
                    var temperature = 20.0 + random.NextDouble() * 10.0; // Simulated temperature
                    var humidity = 40.0 + random.NextDouble() * 20.0;    // Simulated humidity
                    var pressure = 1000.0 + random.NextDouble() * 50.0;  // Simulated pressure
                    _temperatureProperty.Update(temperature);
                    _humidityProperty.Update(humidity);
                    _pressureProperty.Update(pressure);
                    Thread.Sleep(5000); // Update every 5 seconds
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in main.");
                throw;
            }        
        }

        public static Device SetupHomieDevice()
        {
            var builder = new HomieDeviceBuilder(Constants.DeviceTopicId, Constants.DeviceName);
            var device = builder
                    .AddNode(Constants.NodeSensorTopicId, Constants.NodeSensorName, Constants.NodeSensorType)
                        .AddFloatProperty(Constants.PropertyTemperatureTopicId, Constants.PropertyTemperatureName, 0.0).BuildProperty(out _temperatureProperty)
                        .AddFloatProperty(Constants.PropertyHumidityTopicId, Constants.PropertyHumidityName, 0.0).BuildProperty(out _humidityProperty)
                        .AddFloatProperty(Constants.PropertyPressureTopicId, Constants.PropertyPressureName, 0.0).BuildProperty(out _pressureProperty)
                    .BuildNode()
                .BuildDevice();

            return device;
        }

        public static IHomieMqttClient SetupMqttClient()
        {
            var mqttClient = new HomieMqttClient("192.168.1.240");
            return mqttClient;

        }

        /// <summary>
        /// This is a helper function to pick up first available network interface and use it for communication.
        /// </summary>
        private static void SetupAndConnectNetwork()
        {
            // Get the first WiFI Adapter
            var wifiAdapter = WifiAdapter.FindAllAdapters()[0];

            // Begin network scan.
            wifiAdapter.ScanAsync();

            // While networks are being scan, continue on configuration. If networks were set previously, 
            // board may already be auto-connected, so reconnection is not even needed.
            var wiFiConfiguration = Wireless80211Configuration.GetAllWireless80211Configurations()[0];
            var ipAddress = NetworkInterface.GetAllNetworkInterfaces()[0].IPv4Address;
            var needToConnect = string.IsNullOrEmpty(ipAddress) || (ipAddress == "0.0.0.0");
            while (needToConnect)
            {
                foreach (var network in wifiAdapter.NetworkReport.AvailableNetworks)
                {
                    // Show all networks found
                    Debug.WriteLine($"Net SSID :{network.Ssid},  BSSID : {network.Bsid},  rssi : {network.NetworkRssiInDecibelMilliwatts},  signal : {network.SignalBars}");

                    // If its our Network then try to connect
                    if (network.Ssid == wiFiConfiguration.Ssid)
                    {

                        var result = wifiAdapter.Connect(network, WifiReconnectionKind.Automatic, wiFiConfiguration.Password);

                        if (result.ConnectionStatus == WifiConnectionStatus.Success)
                        {
                            Debug.WriteLine($"Connected to Wifi network {network.Ssid}.");
                            needToConnect = false;
                        }
                        else
                        {
                            Debug.WriteLine($"Error {result.ConnectionStatus} connecting to Wifi network {network.Ssid}.");
                        }
                    }
                }

                Thread.Sleep(10000);
            }

            ipAddress = NetworkInterface.GetAllNetworkInterfaces()[0].IPv4Address;
            Debug.WriteLine($"Connected to Wifi network with IP address {ipAddress}");
        }
    }
}
