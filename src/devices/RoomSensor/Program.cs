using SmartHome.Homie.V4;
using SmartHome.Homie.V4.Builder;
using SmartHome.Homie.V4.Properties;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using System;
using System.Threading;
using nanoFramework.M2Mqtt.Exceptions;
using SmartHome.Mqtt;
using SmartHome.Networking;

namespace SmartHome.Devices.RoomSensor
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

                NetworkHelper.ConnectToConfiguredNetwork();

                var device = SetupHomieDevice();
                var mqttClient = SetupMqttClient();
                var homieClient = new HomieClient(device, mqttClient);

                ConnectWithRetry(homieClient);

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

        public static IReconnectingMqttClient SetupMqttClient()
        {
            var mqttClient = new ReconnectingMqttClient("192.168.1.238");
            return mqttClient;

        }

        // The Homie client owns the MQTT session on purpose: it is the only thing that
        // can declare the Homie last will (homie/<device-id>/$state = lost), and a will
        // can only be set in CONNECT. Connecting the transport here first would produce
        // a session without it -- which is what this app did until 2026-08-21, leaving
        // the device stuck at 'ready' forever whenever it dropped off abruptly.
        private static void ConnectWithRetry(HomieClient homieClient)
        {
            const int maxAttempts = 10;
            const int retryDelayMs = 3000;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                _logger.LogDebug($"Connecting Homie device (attempt {attempt}/{maxAttempts})...");

                if (homieClient.Connect())
                {
                    _logger.LogInformation("Homie device connected.");
                    return;
                }

                if (attempt == maxAttempts)
                {
                    throw new Exception($"Could not connect the Homie device after {maxAttempts} attempts.");
                }

                Thread.Sleep(retryDelayMs);
            }
        }
    }
}
