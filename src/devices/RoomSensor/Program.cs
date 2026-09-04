using SmartHome.Homie.V4;
using SmartHome.HomeAssistant;
using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.Builder;
using SmartHome.Homie.V4.Properties;
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

        // How many missed readings Home Assistant tolerates before it calls a value
        // stale. Six rather than one or two: a single I2C NACK is logged and the loop
        // carries on, so a short gap is normal and must not blank the dashboard. What
        // this is really for is the sensor failing persistently -- the device then goes
        // to Homie 'alert' and stops publishing while staying connected, which without
        // an expiry would leave the last good temperature on display indefinitely.
        private const int HomeAssistantExpiryIntervals = 6;

        // Named rather than inline at the call site, so Run-IntegrationTests.ps1's
        // stale-constant pre-flight can find it: that check greps for exactly this
        // shape, and an inline literal was invisible to it. This address drifts from
        // SMARTHOME_MQTT_BROKER in local.env.ps1 and is the usual reason a healthy
        // device "can't reach the broker".
        private const string BrokerHost = "192.168.1.238";

        private static FloatProperty _temperatureProperty;
        private static FloatProperty _humidityProperty;
        private static FloatProperty _pressureProperty;
        private static HomeAssistantAnnouncer _homeAssistant;
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
                IHomieClient homieClient = new HomieClient(device, mqttClient);

                ConnectWithRetry(homieClient);

                AnnounceToHomeAssistant(device, mqttClient);

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
                    // result is not one: PublishReading drives alert/ready for that, and
                    // a dropped link is the reconnect layer's job.
                    try
                    {
                        PublishReading(sensor, homieClient);
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

        public static Device SetupHomieDevice()
        {
            var builder = new HomieDeviceBuilder(Constants.DeviceTopicId, Constants.DeviceName);
            var device = builder
                    .AddNode(Constants.NodeSensorTopicId, Constants.NodeSensorName, Constants.NodeSensorType)
                        .AddFloatProperty(Constants.PropertyTemperatureTopicId, Constants.PropertyTemperatureName, 0.0)
                            .WithUnit(Unit.DegreeCelsius)
                        .BuildProperty(out _temperatureProperty)
                        .AddFloatProperty(Constants.PropertyHumidityTopicId, Constants.PropertyHumidityName, 0.0)
                            .WithUnit(Unit.Percent)
                        .BuildProperty(out _humidityProperty)
                        // Pascals, not hectopascals: Pa is the unit Homie's recommended list
                        // carries, and $unit should say what the value actually is.
                        .AddFloatProperty(Constants.PropertyPressureTopicId, Constants.PropertyPressureName, 0.0)
                            .WithUnit(Unit.Pascal)
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

        private static void PublishReading(Bme280 sensor, IHomieClient homieClient)
        {
            var reading = sensor.Read();

            if (!reading.TemperatureIsValid || !reading.PressureIsValid || !reading.HumidityIsValid)
            {
                // A sensor that stops answering is exactly what Homie's 'alert' state is
                // for: "send this message when something is wrong". Publishing the last
                // good value forever would be worse than saying nothing.
                _logger.LogError($"Invalid BMP280 reading (temperature: {reading.TemperatureIsValid}, pressure: {reading.PressureIsValid}, humidity: {reading.HumidityIsValid}).");

                if (homieClient.State != State.Alert)
                {
                    homieClient.Alert();
                }

                return;
            }

            if (homieClient.State == State.Alert)
            {
                _logger.LogInformation("BMP280 reading valid again.");
                homieClient.Ready();
            }

            _temperatureProperty.Update(reading.Temperature.DegreesCelsius);
            _humidityProperty.Update(reading.Humidity.Percent);
            _pressureProperty.Update(reading.Pressure.Pascals);
        }

        /// <summary>
        /// Publishes this device to Home Assistant, over the connection Homie just opened.
        /// </summary>
        /// <remarks>
        /// One MQTT session carries both conventions. Nothing here re-declares the device:
        /// the discovery messages describe the same Device model Homie announced and point
        /// Home Assistant at the same topics, so a property added to SetupHomieDevice gets
        /// a Home Assistant entity with no edit here. The only new traffic is one retained
        /// config per property, published once per session.
        ///
        /// Not fatal if it fails, unlike the Homie connect above: this device's job is to
        /// publish readings, and it does that whether or not Home Assistant ever hears
        /// about it. Announce() logs what went wrong, and the re-announce Attach() arms
        /// runs again on the next reconnect.
        /// </remarks>
        private static void AnnounceToHomeAssistant(Device device, IReconnectingMqttClient mqttClient)
        {
            try
            {
                _homeAssistant = new HomeAssistantAnnouncer(device, mqttClient, new HomeAssistantSettings
                {
                    Model = Constants.DeviceModel,
                    ExpireAfterSeconds = (MeasurementIntervalMs / 1000) * HomeAssistantExpiryIntervals,
                });

                // The one thing the Homie declaration cannot say. '%' is equally the unit
                // of humidity, of battery charge and of soil moisture, and Home Assistant
                // has a different device class for each, so DeviceClass.FromUnit
                // deliberately returns nothing for it and the app names it instead.
                // Temperature (°C) and pressure (Pa) are unambiguous and need no such line.
                _homeAssistant.SetDeviceClass(_humidityProperty, DeviceClass.Humidity);

                _homeAssistant.Announce();

                // After the first announce, not before: Attach() only arms the
                // *re*-announce paths (broker back, Home Assistant back), and arming them
                // first would let a reconnect race the initial publish.
                _homeAssistant.Attach();
            }
            catch (Exception ex)
            {
                // Caught here rather than inside the library, which propagates on purpose:
                // only the app can decide that failing to reach Home Assistant is not a
                // reason to stop measuring. Main's catch rethrows and the CLR turns that
                // into a reboot, which is far too much for an optional integration.
                _logger.LogError(ex, "Could not announce the device to Home Assistant; continuing without it.");
            }
        }

        // The Homie client owns the MQTT session on purpose: it is the only thing that
        // can declare the Homie last will (homie/<device-id>/$state = lost), and a will
        // can only be set in CONNECT. Connecting the transport here first would produce
        // a session without it -- which is what this app did until 2026-08-21, leaving
        // the device stuck at 'ready' forever whenever it dropped off abruptly.
        //
        // The retry itself lives on IHomieClient now: every Homie device needs it, and
        // three apps had grown their own copy of the same loop.
        private static void ConnectWithRetry(IHomieClient homieClient)
        {
            if (!homieClient.ConnectWithRetry())
            {
                throw new Exception("Could not connect the Homie device.");
            }

            _logger.LogInformation("Homie device connected.");
        }
    }
}
