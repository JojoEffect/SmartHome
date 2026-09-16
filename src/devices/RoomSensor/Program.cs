using SmartHome.DeviceModel;
using SmartHome.DeviceModel.Builder;
using SmartHome.DeviceModel.Properties;
using SmartHome.Homie.V4;
using SmartHome.Protocol;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using System;
using System.Device.I2c;
using System.Threading;
using Iot.Device.Bmxx80;
using Iot.Device.Bmxx80.FilteringMode;
using nanoFramework.Hardware.Esp32;
using nanoFramework.M2Mqtt.Exceptions;
using SmartHome.Mqtt;
using SmartHome.Networking;

namespace SmartHome.Devices.RoomSensor
{
    public class Program
    {
        private const int I2cBusId = 1;
        private const int I2cDataPin = 21;
        private const int I2cClockPin = 22;
        private const int MeasurementIntervalMs = 5000;

        // Named rather than inline at the call site, so Run-IntegrationTests.ps1's
        // stale-constant pre-flight can find it: that check greps for exactly this
        // shape, and an inline literal was invisible to it. This address drifts from
        // SMARTHOME_MQTT_BROKER in local.env.ps1 and is the usual reason a healthy
        // device "can't reach the broker".
        private const string BrokerHost = "192.168.1.238";

        // What is wrong, as an id: the sensor. Alerts are keyed, so raising and clearing
        // both name the condition, and a second condition on this device would get its
        // own id rather than overwriting this one.
        private const string SensorAlertId = "sensor";

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

                var device = SetupDevice();
                var mqttClient = SetupMqttClient();

                // The one line that couples this app to a convention. Everything above
                // describes the device and everything below talks to IDeviceProtocol, so
                // speaking a different convention is a different adapter constructed
                // here and nothing else. (Two log strings further down still say "Homie"
                // about whatever is constructed here -- they are this app's own output,
                // and #112 is where the adapter becomes a compiled choice and they have
                // to stop naming one.)
                IDeviceProtocol protocol = new HomieClient(device, mqttClient);

                ConnectWithRetry(protocol);

                using var sensor = SetupSensor();

                while (true)
                {
                    // The measurement loop survives a throw. Main's catch rethrows, which
                    // the CLR turns into a reboot -- so before this, a single I2C NACK or
                    // a publish into a link that had just dropped cost a restart, a WiFi
                    // re-association, an MQTT reconnect, ~30s of readings, and a 'lost'
                    // will fired on a device that was fine.
                    //
                    // Only unexpected faults land here. An invalid-but-readable sensor
                    // result is not one: PublishReading raises and clears the alert for
                    // that, and a dropped link is the reconnect layer's job.
                    try
                    {
                        PublishReading(sensor, device, protocol);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to publish a reading; continuing with the next measurement.");
                    }

                    Thread.Sleep(MeasurementIntervalMs);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in main.");
                throw;
            }
        }

        public static Device SetupDevice()
        {
            var builder = new DeviceBuilder(Constants.DeviceTopicId, Constants.DeviceName);
            var device = builder
                    .AddNode(Constants.NodeSensorTopicId, Constants.NodeSensorName, Constants.NodeSensorType)
                        .AddFloatProperty(Constants.PropertyTemperatureTopicId, Constants.PropertyTemperatureName, 0.0)
                            .WithUnit(Units.DegreeCelsius)
                        .BuildProperty(out _temperatureProperty)
                        .AddFloatProperty(Constants.PropertyHumidityTopicId, Constants.PropertyHumidityName, 0.0)
                            .WithUnit(Units.Percent)
                        .BuildProperty(out _humidityProperty)
                        // Pascals, not hectopascals: Pa is the unit the recommended list
                        // these constants come from carries, and a unit should say what
                        // the value actually is.
                        .AddFloatProperty(Constants.PropertyPressureTopicId, Constants.PropertyPressureName, 0.0)
                            .WithUnit(Units.Pascal)
                        .BuildProperty(out _pressureProperty)
                    .BuildNode()
                .BuildDevice();

            return device;
        }

        public static IReconnectingMqttClient SetupMqttClient() => new ReconnectingMqttClient(BrokerHost);

        private static Bme280 SetupSensor()
        {
            // Same wiring as Bmp280Check, which is the isolated proof that this sensor
            // reads correctly over I2C on this board.
            Configuration.SetPinFunction(I2cDataPin, DeviceFunction.I2C1_DATA);
            Configuration.SetPinFunction(I2cClockPin, DeviceFunction.I2C1_CLOCK);

            var settings = new I2cConnectionSettings(I2cBusId, Bme280.SecondaryI2cAddress);
            var device = I2cDevice.Create(settings);

            return new Bme280(device)
            {
                TemperatureSampling = Sampling.LowPower,
                PressureSampling = Sampling.UltraHighResolution,
                HumiditySampling = Sampling.Standard,
                FilterMode = Bmx280FilteringMode.X2,
            };
        }

        private static void PublishReading(Bme280 sensor, Device device, IDeviceProtocol protocol)
        {
            var reading = sensor.Read();

            if (!reading.TemperatureIsValid || !reading.PressureIsValid || !reading.HumidityIsValid)
            {
                // A sensor that stops answering is exactly what an alert is for: saying
                // that something is wrong rather than publishing the last good value
                // forever, which would be worse than saying nothing. The Homie v4 adapter
                // turns any raised alert into $state = alert; an adapter for a convention
                // with room for the detail publishes the id and the message too.
                //
                // The same text to the log and to the alert, deliberately: an alert whose
                // message differs from the line beside it in the device log is two
                // accounts of one fault. Re-raising it every five seconds costs nothing --
                // the model drops a raise that repeats the message it already holds, and
                // a message that does change (a second channel failing) still leaves the
                // wire quiet, because the v4 token was already 'alert'.
                var diagnostic = $"Invalid BMP280 reading (temperature: {reading.TemperatureIsValid}, pressure: {reading.PressureIsValid}, humidity: {reading.HumidityIsValid}).";

                _logger.LogError(diagnostic);
                protocol.RaiseAlert(SensorAlertId, diagnostic);

                return;
            }

            // HasAlerts rather than the lifecycle state: an alerting device is still Ready
            // as far as the model is concerned -- it is running and it is publishing --
            // and only clearing the alert takes the wire's degraded state back.
            if (device.HasAlerts)
            {
                _logger.LogInformation("BMP280 reading valid again.");
                protocol.ClearAlert(SensorAlertId);
            }

            _temperatureProperty.Update(reading.Temperature.DegreesCelsius);
            _humidityProperty.Update(reading.Humidity.Percent);
            _pressureProperty.Update(reading.Pressure.Pascals);
        }

        // The protocol adapter owns the MQTT session on purpose: it is the only thing
        // that can declare the last will (for Homie v4, homie/<device-id>/$state = lost),
        // and a will can only be set in CONNECT. Connecting the transport here first
        // would produce a session without it -- which is what this app did until
        // 2026-08-21, leaving the device stuck at 'ready' forever whenever it dropped off
        // abruptly.
        //
        // The retry itself lives on IDeviceProtocol now: every device that connects needs
        // it, and three apps had grown their own copy of the same loop.
        private static void ConnectWithRetry(IDeviceProtocol protocol)
        {
            if (!protocol.ConnectWithRetry())
            {
                throw new Exception("Could not connect the Homie device.");
            }

            _logger.LogInformation("Homie device connected.");
        }
    }
}
