using SmartHome.HomeAssistant;
using SmartHome.Homie.V4;
using SmartHome.Homie.V4.Builder;
using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.Properties;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using nanoFramework.TestFramework;
using System;
using System.Collections;

namespace SmartHome.UnitTests
{
    /// <summary>
    /// The Homie-to-Home-Assistant mapping, asserted without a broker.
    /// </summary>
    /// <remarks>
    /// <see cref="DiscoveryMapper"/> is a pure function precisely so this file can exist:
    /// every topic, every payload key and every inferred device class is decided here, so
    /// CI can catch a regression in any of them. What CI still cannot say is whether Home
    /// Assistant accepts the result -- that needs a real broker and a real Home Assistant,
    /// and is a manual check.
    /// </remarks>
    [TestClass]
    public class HomeAssistantDiscoveryTests
    {
        private const string _deviceTopicId = "super-car";
        private const string _deviceName = "Super car";

        [Setup]
        public void Setup()
        {
            LogDispatcher.LoggerFactory = new DebugLoggerFactory();
        }

        [Cleanup]
        public void Cleanup()
        {
            LogDispatcher.LoggerFactory = null;
        }

        [TestMethod]
        public void Maps_Every_Property_To_One_Entity()
        {
            var device = BuildDevice();

            var entities = DiscoveryMapper.Map(device, new HomeAssistantSettings());

            // Counted off the device rather than written as a literal. The claim is "one
            // entity per property", and a hard-coded number states it only for as long as
            // nobody adds a property to the fixture -- which is exactly how this assertion
            // first failed, on a count that was wrong the day it was written.
            Assert.AreEqual(PropertyCount(device), entities.Length, "one discovery message per property");
        }

        [TestMethod]
        public void Read_Only_Float_Becomes_A_Sensor_Pointing_At_The_Homie_Topic()
        {
            var device = BuildDevice();

            var entities = DiscoveryMapper.Map(device, new HomeAssistantSettings());
            var entity = Find(entities, "homeassistant/sensor/super-car-engine-temperature/config");

            // The whole point of the design: Home Assistant is pointed at the topic Homie
            // already publishes, so the device sends no second copy of any value.
            AssertContains(entity.Payload, "\"stat_t\":\"homie/super-car/engine/temperature\"");
            AssertContains(entity.Payload, "\"uniq_id\":\"super-car-engine-temperature\"");
            AssertContains(entity.Payload, "\"stat_cla\":\"measurement\"");
            AssertMissing(entity.Payload, "cmd_t");
        }

        [TestMethod]
        public void Infers_A_Device_Class_From_An_Unambiguous_Unit()
        {
            var device = BuildDevice();

            var entities = DiscoveryMapper.Map(device, new HomeAssistantSettings());

            AssertContains(
                Find(entities, "homeassistant/sensor/super-car-engine-temperature/config").Payload,
                "\"dev_cla\":\"temperature\"");
            AssertContains(
                Find(entities, "homeassistant/sensor/super-car-engine-pressure/config").Payload,
                "\"dev_cla\":\"atmospheric_pressure\"");
        }

        [TestMethod]
        public void Leaves_Percent_Without_A_Device_Class()
        {
            // '%' is humidity, battery and moisture alike, and Home Assistant has a
            // separate class for each -- so guessing is worse than saying nothing. The
            // unit itself is still declared, which is what makes the value readable.
            var device = BuildDevice();

            var entities = DiscoveryMapper.Map(device, new HomeAssistantSettings());
            var payload = Find(entities, "homeassistant/sensor/super-car-engine-humidity/config").Payload;

            AssertMissing(payload, "dev_cla");
            AssertContains(payload, "\"unit_of_meas\":\"%\"");
        }

        [TestMethod]
        public void An_Override_Names_The_Device_Class_The_Unit_Cannot()
        {
            var device = BuildDevice(out _, out var humidity, out _, out _, out _, out _);
            var overrides = new Hashtable();
            overrides.Add(humidity.GetTopic(), new EntityOverride { DeviceClass = DeviceClass.Humidity });

            var entities = DiscoveryMapper.Map(device, new HomeAssistantSettings(), overrides);

            AssertContains(
                Find(entities, "homeassistant/sensor/super-car-engine-humidity/config").Payload,
                "\"dev_cla\":\"humidity\"");
        }

        [TestMethod]
        public void An_Excluded_Property_Gets_No_Entity()
        {
            var device = BuildDevice(out _, out var humidity, out _, out _, out _, out _);
            var overrides = new Hashtable();
            overrides.Add(humidity.GetTopic(), new EntityOverride { Excluded = true });

            var entities = DiscoveryMapper.Map(device, new HomeAssistantSettings(), overrides);

            Assert.AreEqual(PropertyCount(device) - 1, entities.Length, "exactly one entity fewer");
            foreach (DiscoveryEntity entity in entities)
            {
                Assert.IsFalse(entity.Topic.IndexOf("humidity") >= 0, "no humidity entity survived");
            }
        }

        [TestMethod]
        public void Settable_Float_Becomes_A_Number_With_Range_Step_And_Command_Topic()
        {
            var device = BuildDevice();

            var entities = DiscoveryMapper.Map(device, new HomeAssistantSettings());
            var payload = Find(entities, "homeassistant/number/super-car-engine-setpoint/config").Payload;

            AssertContains(payload, "\"cmd_t\":\"homie/super-car/engine/setpoint/set\"");
            // Straight out of the Homie $format, uninterpreted.
            AssertContains(payload, "\"min\":5");
            AssertContains(payload, "\"max\":30");
            // One decimal place declared, so the control steps by 0.1. Home Assistant's
            // own default is 1, which would make a fractional setpoint unreachable.
            AssertContains(payload, "\"step\":0.1");
        }

        [TestMethod]
        public void Boolean_Payloads_Are_The_Ones_Homie_Actually_Publishes()
        {
            // Home Assistant defaults to "ON"/"OFF"; Homie v4 defines a boolean payload as
            // exactly "true"/"false". Without this every reading would come out "off".
            var device = BuildDevice();

            var entities = DiscoveryMapper.Map(device, new HomeAssistantSettings());
            var sensor = Find(entities, "homeassistant/binary_sensor/super-car-engine-running/config").Payload;
            var toggle = Find(entities, "homeassistant/switch/super-car-engine-ignition/config").Payload;

            AssertContains(sensor, "\"pl_on\":\"true\"");
            AssertContains(sensor, "\"pl_off\":\"false\"");
            AssertContains(toggle, "\"stat_on\":\"true\"");
            AssertContains(toggle, "\"cmd_t\":\"homie/super-car/engine/ignition/set\"");
        }

        [TestMethod]
        public void Settable_Enum_Becomes_A_Select_Carrying_The_Format_Values()
        {
            var device = BuildDevice();

            var entities = DiscoveryMapper.Map(device, new HomeAssistantSettings());
            var payload = Find(entities, "homeassistant/select/super-car-engine-mode/config").Payload;

            AssertContains(payload, "\"options\":[\"eco\",\"sport\"]");
            AssertContains(payload, "\"cmd_t\":\"homie/super-car/engine/mode/set\"");
        }

        [TestMethod]
        public void Availability_Is_Read_Off_The_Homie_State_Topic()
        {
            // The design's load-bearing claim: one MQTT session can carry both
            // conventions because Home Assistant's availability is derived from the Homie
            // last will rather than needing a second will of its own.
            var device = BuildDevice();

            var entities = DiscoveryMapper.Map(device, new HomeAssistantSettings());
            var payload = Find(entities, "homeassistant/sensor/super-car-engine-temperature/config").Payload;

            AssertContains(payload, "\"avty_t\":\"homie/super-car/$state\"");
            AssertContains(payload, "'ready','alert','sleeping'");
        }

        [TestMethod]
        public void Device_And_Origin_Blocks_Carry_What_Homie_Cannot_Say()
        {
            var device = BuildDevice();
            var settings = new HomeAssistantSettings
            {
                Model = "ESP32",
                SoftwareVersion = "1.2.3",
                OriginName = "SmartHome",
            };

            var entities = DiscoveryMapper.Map(device, settings);
            var payload = Find(entities, "homeassistant/sensor/super-car-engine-temperature/config").Payload;

            AssertContains(payload, "\"dev\":{\"ids\":\"super-car\",\"name\":\"Super car\"");
            AssertContains(payload, "\"mdl\":\"ESP32\"");
            AssertContains(payload, "\"sw\":\"1.2.3\"");
            AssertContains(payload, "\"o\":{\"name\":\"SmartHome\"}");
        }

        [TestMethod]
        public void Expiry_Is_Emitted_Only_When_Asked_For()
        {
            var device = BuildDevice();

            var without = DiscoveryMapper.Map(device, new HomeAssistantSettings());
            var with = DiscoveryMapper.Map(device, new HomeAssistantSettings { ExpireAfterSeconds = 30 });

            AssertMissing(Find(without, "homeassistant/sensor/super-car-engine-temperature/config").Payload, "exp_aft");
            AssertContains(Find(with, "homeassistant/sensor/super-car-engine-temperature/config").Payload, "\"exp_aft\":30");
        }

        [TestMethod]
        public void A_Malformed_Format_Is_Dropped_Rather_Than_Injected()
        {
            // $format is a free string on the Homie side, and min/max go into the payload
            // as raw JSON numbers -- so anything that is not a bare number would produce
            // invalid JSON and lose the whole entity, not just its range.
            var device = new HomieDeviceBuilder(_deviceTopicId, _deviceName)
                .AddNode("engine", "Engine", "V8")
                    .AddFloatProperty("setpoint", "Setpoint", 1.0)
                        .WithSettable(true)
                        .WithFormat("low:high")
                    .BuildProperty(out _)
                .BuildNode()
                .BuildDevice();

            var entities = DiscoveryMapper.Map(device, new HomeAssistantSettings());
            var payload = Find(entities, "homeassistant/number/super-car-engine-setpoint/config").Payload;

            AssertMissing(payload, "\"min\"");
            AssertMissing(payload, "\"max\"");
        }

        [TestMethod]
        public void A_Quote_In_A_Name_Is_Escaped_Rather_Than_Breaking_The_Payload()
        {
            // A device name is free text and reaches the payload verbatim. An unescaped
            // quote would truncate the JSON object, and Home Assistant would drop the
            // entity with a parse error rather than anything that points here.
            var device = new HomieDeviceBuilder(_deviceTopicId, "The \"fast\" one")
                .AddNode("engine", "Engine", "V8")
                    .AddFloatProperty("temperature", "Temperature", 0.0)
                    .BuildProperty(out _)
                .BuildNode()
                .BuildDevice();

            var entities = DiscoveryMapper.Map(device, new HomeAssistantSettings());
            var payload = Find(entities, "homeassistant/sensor/super-car-engine-temperature/config").Payload;

            AssertContains(payload, "\\\"fast\\\"");
        }

        private static Device BuildDevice() =>
            BuildDevice(out _, out _, out _, out _, out _, out _);

        private static Device BuildDevice(
            out FloatProperty temperature,
            out FloatProperty humidity,
            out FloatProperty pressure,
            out FloatProperty setpoint,
            out BooleanProperty ignition,
            out EnumProperty mode)
        {
            return new HomieDeviceBuilder(_deviceTopicId, _deviceName)
                .AddNode("engine", "Engine", "V8")
                    .AddFloatProperty("temperature", "Temperature", 0.0)
                        .WithUnit(Unit.DegreeCelsius)
                    .BuildProperty(out temperature)
                    .AddFloatProperty("humidity", "Humidity", 0.0)
                        .WithUnit(Unit.Percent)
                    .BuildProperty(out humidity)
                    .AddFloatProperty("pressure", "Pressure", 0.0)
                        .WithUnit(Unit.Pascal)
                    .BuildProperty(out pressure)
                    .AddFloatProperty("setpoint", "Setpoint", 20.0)
                        .WithUnit(Unit.DegreeCelsius)
                        .WithSettable(true)
                        .WithFormat("5:30")
                        .WithDecimals(1)
                    .BuildProperty(out setpoint)
                    .AddBooleanProperty("running", "Running", false)
                    .BuildProperty(out _)
                    .AddBooleanProperty("ignition", "Ignition", false)
                        .WithSettable(true)
                    .BuildProperty(out ignition)
                    .AddEnumProperty("mode", "Mode", "eco")
                        .WithFormat("eco,sport")
                        .WithSettable(true)
                    .BuildProperty(out mode)
                .BuildNode()
                .BuildDevice();
        }

        /// <summary>Properties across every node of the device, i.e. how many entities to expect.</summary>
        private static int PropertyCount(Device device)
        {
            var count = 0;
            foreach (var node in device.Nodes)
            {
                count += node.Properties.Length;
            }

            return count;
        }

        private static DiscoveryEntity Find(DiscoveryEntity[] entities, string topic)
        {
            foreach (DiscoveryEntity entity in entities)
            {
                if (entity.Topic == topic)
                {
                    return entity;
                }
            }

            throw new Exception($"No discovery entity was published to '{topic}'.");
        }

        private static void AssertContains(string payload, string fragment) =>
            Assert.IsTrue(payload.IndexOf(fragment) >= 0, $"'{fragment}' is missing from '{payload}'");

        private static void AssertMissing(string payload, string fragment) =>
            Assert.IsFalse(payload.IndexOf(fragment) >= 0, $"'{fragment}' should not be in '{payload}'");
    }
}
