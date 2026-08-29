using Microsoft.Extensions.Logging;
using nanoFramework.Hardware.Esp32;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using SmartHome.Homie.V4;
using SmartHome.Homie.V4.Builder;
using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.Properties;
using SmartHome.Mqtt;
using SmartHome.Networking;
using System;
using System.Device.I2c;
using System.Threading;

namespace SmartHome.Devices.RainwaterCistern
{
    public class Program
    {
        private const int I2cBusId = 1;
        private const int I2cDataPin = 21;
        private const int I2cClockPin = 22;

        // A cistern fills over hours and empties over minutes at worst, so a minute
        // between readings is already generous. Publishing every 5s like RoomSensor
        // would fill the broker's retained store with the same number.
        private const int MeasurementIntervalMs = 60000;

        private const int SamplesPerReading = 8;

        private const double MilliAmpsPerAmp = 1000.0;

        // Named rather than inline at the call site, so it carries the exact shape
        // Run-IntegrationTests.ps1's stale-constant pre-flight greps for. That check
        // does not read this file yet -- it opens RoomSensor's Program.cs and the
        // integration tests', and this device is in neither set -- so the constant
        // drifting from SMARTHOME_MQTT_BROKER is currently silent. Adding the path to
        // the pre-flight is all it would take, once the device is worth running.
        private const string BrokerHost = "192.168.1.238";

        private static FloatProperty _waterDepthProperty;
        private static FloatProperty _levelProperty;
        private static FloatProperty _volumeProperty;
        private static FloatProperty _loopCurrentProperty;
        private static ILogger _logger;

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

                using var sensor = SetupSensor();

                while (true)
                {
                    // Same shape as RoomSensor's loop, for the same reason: Main's catch
                    // rethrows and the CLR turns that into a reboot, so a single I2C NACK
                    // must not cost a restart, a WiFi re-association and a 'lost' will on
                    // a device that is fine. A loop fault is not an unexpected condition
                    // here -- PublishReading drives 'alert' for that.
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
                    .AddNode(Constants.NodeTankTopicId, Constants.NodeTankName, Constants.NodeTankType)
                        // Millimetre resolution, published unclamped: this is the
                        // measurement, and the two derived values below are the
                        // convenient readings of it.
                        .AddFloatProperty(Constants.PropertyWaterDepthTopicId, Constants.PropertyWaterDepthName, 0.0)
                            .WithUnit(Unit.Meter)
                            .WithDecimals(3)
                        .BuildProperty(out _waterDepthProperty)
                        .AddFloatProperty(Constants.PropertyLevelTopicId, Constants.PropertyLevelName, 0.0)
                            .WithUnit(Unit.Percent)
                            .WithFormat("0:100")
                            .WithDecimals(1)
                        .BuildProperty(out _levelProperty)
                        .AddFloatProperty(Constants.PropertyVolumeTopicId, Constants.PropertyVolumeName, 0.0)
                            .WithUnit(Unit.Liter)
                            .WithDecimals(0)
                        .BuildProperty(out _volumeProperty)
                        // Published in amps because that is the unit Homie's recommended
                        // list carries. Worth having on the wire at all because it is the
                        // one value that separates an empty cistern from a broken loop
                        // while somebody is standing at the tank with a multimeter.
                        .AddFloatProperty(Constants.PropertyLoopCurrentTopicId, Constants.PropertyLoopCurrentName, 0.0)
                            .WithUnit(Unit.Ampere)
                            .WithDecimals(5)
                        .BuildProperty(out _loopCurrentProperty)
                    .BuildNode()
                .BuildDevice();

            return device;
        }

        public static IReconnectingMqttClient SetupMqttClient() => new ReconnectingMqttClient(BrokerHost);

        private static CurrentLoopSensor SetupSensor()
        {
            // Same bus and pins as RoomSensor's BMP280: one I2C wiring convention for
            // every device in this repo.
            Configuration.SetPinFunction(I2cDataPin, DeviceFunction.I2C1_DATA);
            Configuration.SetPinFunction(I2cClockPin, DeviceFunction.I2C1_CLOCK);

            var settings = new I2cConnectionSettings(I2cBusId, Ads1115Protocol.DefaultI2cAddress);

            return new CurrentLoopSensor(I2cDevice.Create(settings));
        }

        private static void PublishReading(CurrentLoopSensor sensor, IHomieClient homieClient)
        {
            var milliAmps = sensor.ReadMilliAmps(SamplesPerReading);

            // Published before the fault check, not after: a faulted loop is exactly when
            // somebody wants to see the current, and a near-zero value on the wire says
            // broken supply where a missing value says nothing at all.
            _loopCurrentProperty.Update(milliAmps / MilliAmpsPerAmp);

            if (LevelCalculation.IsLoopFault(milliAmps))
            {
                // Homie's 'alert' state is for "send this message when something is
                // wrong". Deriving a depth from 0.2 mA and publishing it would be worse
                // than publishing nothing: a controller cannot tell that reading apart
                // from a genuinely empty cistern.
                _logger.LogError($"Loop current {milliAmps} mA is outside {LevelCalculation.UnderRangeFaultMilliAmps}-{LevelCalculation.OverRangeFaultMilliAmps} mA; the probe, its wiring or its supply is faulty.");

                if (homieClient.State != State.Alert)
                {
                    homieClient.Alert();
                }

                return;
            }

            if (homieClient.State == State.Alert)
            {
                _logger.LogInformation("Loop current back in range.");
                homieClient.Ready();
            }

            var depthMeters = LevelCalculation.MilliAmpsToDepthMeters(milliAmps, Calibration.ProbeRangeMeters);

            _waterDepthProperty.Update(depthMeters);
            _levelProperty.Update(LevelCalculation.DepthToPercent(depthMeters, Calibration.FullDepthMeters));
            _volumeProperty.Update(LevelCalculation.DepthToLitres(depthMeters, Calibration.CisternInnerDiameterMeters));
        }

        // The Homie client owns the MQTT session on purpose: it is the only thing that
        // can declare the Homie last will (homie/<device-id>/$state = lost), and a will
        // can only be set in CONNECT. Connecting the transport first would produce a
        // session without one.
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
