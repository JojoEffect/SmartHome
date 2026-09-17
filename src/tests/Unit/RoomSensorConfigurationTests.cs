using SmartHome.DeviceConfiguration;
using SmartHome.Devices.RoomSensor;
using nanoFramework.TestFramework;

namespace SmartHome.UnitTests
{
    // RoomSensorConfiguration's own rules -- the ones that decide whether that device
    // comes up reading a sensor or comes up alerting. The class is linked into this
    // project rather than referenced (see Unit.nfproj), because it is pure validation
    // logic and a ProjectReference would drag a device exe in with it.
    //
    // Two of these cases exist because a review caught the code disagreeing with its own
    // stated contract: the remarks said zero was refused everywhere while the pin checks
    // only refused negatives, and the bus id accepted a value the pin wiring cannot
    // honour. Both are the same failure in different clothes -- a configuration that
    // looks plausible, passes validation, and then behaves wrongly on the device, which
    // is the one outcome this whole mechanism exists to prevent.
    [TestClass]
    public class RoomSensorConfigurationTests
    {
        [TestMethod]
        public void A_Complete_Configuration_Is_Accepted()
        {
            // The values config\room-sensor.json actually ships, so this fails if a bound
            // is ever tightened past the real installation.
            Assert.IsNull(Build().Validate(), "the shipped configuration must be usable");
        }

        [TestMethod]
        public void A_Missing_Top_Level_Value_Is_Refused_By_Name()
        {
            AssertRefused(Build(deviceId: null), "DeviceId");
            AssertRefused(Build(deviceId: string.Empty), "DeviceId");
            AssertRefused(Build(deviceId: "   "), "DeviceId");
            AssertRefused(Build(deviceName: null), "DeviceName");
            AssertRefused(Build(brokerHost: null), "BrokerHost");
        }

        [TestMethod]
        public void A_Missing_Sensor_Block_Is_Refused()
        {
            var configuration = Build();
            configuration.Sensor = null;

            AssertRefused(configuration, "Sensor");
        }

        [TestMethod]
        public void A_Pin_Of_Zero_Is_Refused()
        {
            // The case the review found. An absent or misspelt JSON key leaves the field
            // at zero, GPIO0 is a real pin, and a "not negative" check would have let it
            // through and configured it -- on the ESP32's boot-strapping pin, at that.
            AssertRefused(Build(dataPin: 0), "DataPin");
            AssertRefused(Build(clockPin: 0), "ClockPin");
        }

        [TestMethod]
        public void A_Negative_Pin_Is_Refused()
        {
            AssertRefused(Build(dataPin: -1), "DataPin");
            AssertRefused(Build(clockPin: -1), "ClockPin");
        }

        [TestMethod]
        public void One_Pin_Cannot_Be_Both_Halves_Of_The_Bus()
        {
            AssertRefused(Build(dataPin: 21, clockPin: 21), "ClockPin");
        }

        [TestMethod]
        public void A_Bus_The_Pin_Wiring_Cannot_Honour_Is_Refused()
        {
            // The other case the review found. Program.SetupSensor assigns
            // DeviceFunction.I2C1_DATA/I2C1_CLOCK unconditionally, so bus 2 would leave
            // the pins on bus 1 and open bus 2 -- failing at the first read with nothing
            // to say why. Bus 2 is a real bus on this runtime, which is precisely why
            // accepting it here would be wrong.
            AssertRefused(Build(i2cBusId: 2), "I2cBusId");
            AssertRefused(Build(i2cBusId: 0), "I2cBusId");
            AssertRefused(Build(i2cBusId: -1), "I2cBusId");
        }

        [TestMethod]
        public void An_Interval_Of_Zero_Is_Refused()
        {
            AssertRefused(Build(measurementIntervalMs: 0), "MeasurementIntervalMs");
            AssertRefused(Build(measurementIntervalMs: -1), "MeasurementIntervalMs");
        }

        [TestMethod]
        public void The_Whole_Configuration_Round_Trips_From_Its_Own_File_Format()
        {
            // Byte-for-byte the shape of config\room-sensor.json, through the real parser
            // rather than through property setters. This is the case that would fail if a
            // property were renamed on one side only -- the JSON keys are the property
            // names, and the parser is case-sensitive on purpose.
            var result = ConfigurationParser.Parse(
                "{ \"DeviceId\": \"room-sensor-office\", \"DeviceName\": \"Raumsensor Buero\", " +
                "\"BrokerHost\": \"192.168.1.238\", \"Sensor\": { \"I2cBusId\": 1, \"DataPin\": 21, " +
                "\"ClockPin\": 22, \"MeasurementIntervalMs\": 5000 } }",
                typeof(RoomSensorConfiguration),
                "I:\\configuration.json");

            Assert.IsTrue(result.IsValid, "the shipped file must parse and validate: " + result.FailureReason);

            var configuration = (RoomSensorConfiguration)result.Value!;
            Assert.AreEqual("room-sensor-office", configuration.DeviceId);
            Assert.AreEqual("Raumsensor Buero", configuration.DeviceName);
            Assert.AreEqual("192.168.1.238", configuration.BrokerHost);

            var sensor = configuration.Sensor!;
            Assert.AreEqual(1, sensor.I2cBusId);
            Assert.AreEqual(21, sensor.DataPin);
            Assert.AreEqual(22, sensor.ClockPin);
            Assert.AreEqual(5000, sensor.MeasurementIntervalMs);
        }

        private static void AssertRefused(RoomSensorConfiguration configuration, string field)
        {
            var reason = configuration.Validate();

            Assert.IsNotNull(reason, "an unusable configuration must be refused");
            Assert.IsTrue(
                reason!.IndexOf(field) >= 0,
                $"the reason must name {field} so it can be corrected, got: {reason}");
        }

        // Everything valid unless a case says otherwise, so each case states only what it
        // is about. The defaults are the values config\room-sensor.json ships.
        private static RoomSensorConfiguration Build(
            string? deviceId = "room-sensor-office",
            string? deviceName = "Raumsensor Buero",
            string? brokerHost = "192.168.1.238",
            int i2cBusId = 1,
            int dataPin = 21,
            int clockPin = 22,
            int measurementIntervalMs = 5000)
            => new RoomSensorConfiguration
            {
                DeviceId = deviceId,
                DeviceName = deviceName,
                BrokerHost = brokerHost,
                Sensor = new SensorConfiguration
                {
                    I2cBusId = i2cBusId,
                    DataPin = dataPin,
                    ClockPin = clockPin,
                    MeasurementIntervalMs = measurementIntervalMs,
                },
            };
    }
}
