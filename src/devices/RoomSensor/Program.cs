
using HomieNano.Version4;
using HomieNano.Version4.Builder;
using HomieNano.Version4.Properties;
using nanoFramework.Hardware.Esp32;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using nanoFramework.M2Mqtt;
using System;
using System.Threading;

namespace RoomSensor
{
    public class Program
    {
        private static FloatProperty _temperatureProperty;
        private static FloatProperty _humidityProperty;
        private static FloatProperty _pressureProperty;

        // private static GpioController s_GpioController;
        public static void Main()
        {
            LogDispatcher.LoggerFactory = new DebugLoggerFactory();

            var device = SetupHomieDevice();
            var mqttClient = SetupMqttClient();
            var homieClient = new HomieClient(device, mqttClient);

            homieClient.Connect();

            // Simulate sensor data updates
            while (true)
            {
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

        public static IMqttClient SetupMqttClient()
        {
            var mqttClient = new MqttClient("192.168.1.247");
            return mqttClient;

        }
    }
}
