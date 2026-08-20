using HomieNano.Version4;
using HomieNano.Version4.Builder;
using HomieNano.Version4.Properties;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using System;
using System.Threading;
using nanoFramework.M2Mqtt.Exceptions;
using SmartHome.Device;

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

                NetworkHelper.ConnectToConfiguredNetwork();

                var device = SetupHomieDevice();
                var mqttClient = SetupMqttClient();
                ConnectMqttWithRetry(mqttClient, Constants.DeviceName);
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
            var mqttClient = new HomieMqttClient("192.168.1.238");
            return mqttClient;

        }

        private static void ConnectMqttWithRetry(IHomieMqttClient mqttClient, string clientId)
        {
            const int maxAttempts = 10;
            const int retryDelayMs = 3000;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    _logger.LogDebug($"Connecting MQTT to broker (attempt {attempt}/{maxAttempts})...");
                    mqttClient.Connect(clientId);
                    _logger.LogInformation("MQTT connected.");
                    return;
                }
                catch (MqttConnectionException ex)
                {
                    _logger.LogWarning(ex, $"MQTT connect failed (attempt {attempt}/{maxAttempts}).");
                    if (attempt == maxAttempts)
                    {
                        throw;
                    }

                    Thread.Sleep(retryDelayMs);
                }
            }
        }
    }
}
