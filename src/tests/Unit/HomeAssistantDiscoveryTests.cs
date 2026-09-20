using SmartHome.DeviceModel;
using SmartHome.DeviceModel.Builder;
using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.Formats;
using SmartHome.DeviceModel.Properties;
using SmartHome.HomeAssistant;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using nanoFramework.TestFramework;
using System;

namespace SmartHome.UnitTests
{
    /// <summary>
    /// The device-to-Home-Assistant mapping, asserted without a broker.
    /// </summary>
    /// <remarks>
    /// <see cref="DiscoveryMapper"/> is a pure function precisely so this file can exist:
    /// every topic, every payload key and every device class is decided there, so CI can
    /// catch a regression in any of them. What CI still cannot say is whether Home
    /// Assistant *accepts* the result -- that needs a real instance, and is the subject of
    /// its own issue.
    ///
    /// The refusals get as much attention as the mappings. A configuration Home Assistant
    /// drops is invisible from the device's side: the publish succeeds, the entity simply
    /// never appears, and the log that says why is inside Home Assistant.
    /// </remarks>
    [TestClass]
    public class HomeAssistantDiscoveryTests
    {
        private const string _deviceId = "super-car";
        private const string _deviceName = "Super car";
        private const string _nodeId = "engine";
        private const string _nodeName = "Engine";
        private const string _nodeType = "V8";

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
            // nobody adds a property to the fixture.
            Assert.AreEqual(PropertyCount(device), entities.Length, "one discovery message per property");
        }

        [TestMethod]
        public void A_Read_Only_Number_Becomes_A_Sensor_On_This_Adapters_Own_Topic()
        {
            // The design in one assertion: the state topic is this adapter's, under its
            // own root, not another convention's property topic. The client publishes
            // every value there itself.
            var device = BuildDevice();

            var entities = DiscoveryMapper.Map(device, new HomeAssistantSettings());
            var entity = Find(entities, "homeassistant/sensor/super-car_engine_temperature/config");

            AssertContains(entity.Payload, "\"stat_t\":\"smarthome/super-car/engine/temperature\"");
            AssertContains(entity.Payload, "\"uniq_id\":\"super-car_engine_temperature\"");
            AssertContains(entity.Payload, "\"stat_cla\":\"measurement\"");
            AssertContains(entity.Payload, "\"qos\":1");
            AssertMissing(entity.Payload, "cmd_t");
        }

        [TestMethod]
        public void One_Whole_Configuration_Of_Each_Shape()
        {
            // Two payloads in full, byte for byte, rather than fragments: a discovery
            // config is a single JSON object Home Assistant either accepts whole or drops
            // whole, so the thing worth pinning is the whole object -- every key that is
            // there, every key that is not, and the order they were written in. The
            // fragment assertions elsewhere in this file say what each key means; this one
            // says what actually goes out.
            var entities = DiscoveryMapper.Map(BuildDevice(), new HomeAssistantSettings());

            var device = "\"dev\":{\"ids\":\"super-car\",\"name\":\"Super car\",\"mf\":\"SmartHome\"}";
            var origin = "\"o\":{\"name\":\"SmartHome.HomeAssistant\"}";

            Assert.AreEqual(
                "{\"name\":\"Temperature\"" +
                ",\"uniq_id\":\"super-car_engine_temperature\"" +
                ",\"stat_t\":\"smarthome/super-car/engine/temperature\"" +
                ",\"avty_t\":\"smarthome/super-car/status\"" +
                ",\"qos\":1" +
                $",\"unit_of_meas\":\"{Units.DegreeCelsius}\"" +
                ",\"dev_cla\":\"temperature\"" +
                ",\"stat_cla\":\"measurement\"" +
                $",{device},{origin}}}",
                Find(entities, "homeassistant/sensor/super-car_engine_temperature/config").Payload,
                "the read-only sensor");

            Assert.AreEqual(
                "{\"name\":\"Setpoint\"" +
                ",\"uniq_id\":\"super-car_engine_setpoint\"" +
                ",\"stat_t\":\"smarthome/super-car/engine/setpoint\"" +
                ",\"avty_t\":\"smarthome/super-car/status\"" +
                ",\"qos\":1" +
                ",\"cmd_t\":\"smarthome/super-car/engine/setpoint/set\"" +
                $",\"unit_of_meas\":\"{Units.DegreeCelsius}\"" +
                ",\"dev_cla\":\"temperature\"" +
                ",\"min\":5,\"max\":30,\"step\":0.1" +
                $",{device},{origin}}}",
                Find(entities, "homeassistant/number/super-car_engine_setpoint/config").Payload,
                "the settable number");
        }

        [TestMethod]
        public void A_Quantity_Kind_Names_The_Device_Class_A_Unit_Cannot()
        {
            // The one member of the model that exists mainly for this adapter. '%' is
            // humidity, battery charge and soil moisture alike, so the unit alone cannot
            // say which -- and an entity with the wrong device class is either refused or
            // quietly mis-drawn.
            var device = BuildDevice();

            var entities = DiscoveryMapper.Map(device, new HomeAssistantSettings());

            AssertContains(
                Find(entities, "homeassistant/sensor/super-car_engine_temperature/config").Payload,
                "\"dev_cla\":\"temperature\"");
            AssertContains(
                Find(entities, "homeassistant/sensor/super-car_engine_humidity/config").Payload,
                "\"dev_cla\":\"humidity\"");
            AssertContains(
                Find(entities, "homeassistant/sensor/super-car_engine_pressure/config").Payload,
                "\"dev_cla\":\"pressure\"");
        }

        [TestMethod]
        public void A_Unit_Alone_Implies_No_Device_Class()
        {
            // Deliberately not inferred from the unit, which is what an earlier attempt
            // did: a property that says nothing about what its number means gets an
            // entity that shows the value and the unit, and nothing is guessed.
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("level", "Level", 0.0)
                        .WithUnit(Units.Percent)
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            var payload = Find(
                DiscoveryMapper.Map(device, new HomeAssistantSettings()),
                "homeassistant/sensor/super-car_engine_level/config").Payload;

            AssertMissing(payload, "dev_cla");
            AssertContains(payload, "\"unit_of_meas\":\"%\"");
        }

        [TestMethod]
        public void A_Cumulative_Device_Class_Gets_No_Measurement_State_Class()
        {
            // Home Assistant reads an energy or a volume figure as a meter reading and
            // allows only total and total_increasing there. Which of the two a value is
            // cannot be inferred from anything the model carries, so nothing is published
            // rather than a guess -- and 'measurement' would keep the entity out of
            // long-term statistics while looking correct.
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("consumed", "Consumed", 0.0)
                        .WithUnit(Units.KilowattHour)
                        .WithQuantityKind(QuantityKind.Energy)
                    .BuildProperty()
                    .AddFloatProperty("tank", "Tank", 0.0)
                        .WithUnit(Units.Liter)
                        .WithQuantityKind(QuantityKind.Volume)
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            var entities = DiscoveryMapper.Map(device, new HomeAssistantSettings());

            var energy = Find(entities, "homeassistant/sensor/super-car_engine_consumed/config").Payload;
            AssertContains(energy, "\"dev_cla\":\"energy\"");
            AssertMissing(energy, "stat_cla");

            AssertMissing(Find(entities, "homeassistant/sensor/super-car_engine_tank/config").Payload, "stat_cla");
        }

        [TestMethod]
        public void A_Settable_Number_Carries_Its_Range_Step_And_Command_Topic()
        {
            var device = BuildDevice();

            var payload = Find(
                DiscoveryMapper.Map(device, new HomeAssistantSettings()),
                "homeassistant/number/super-car_engine_setpoint/config").Payload;

            AssertContains(payload, "\"cmd_t\":\"smarthome/super-car/engine/setpoint/set\"");
            // Whole bounds render as whole numbers whatever the property's precision --
            // and never through NumericRange.ToString(), whose "G" formatting prints 21.5
            // as 21.499999999999999 on this runtime.
            AssertContains(payload, "\"min\":5");
            AssertContains(payload, "\"max\":30");
            // One decimal place declared, so the control steps by 0.1. Home Assistant's
            // own default is 1, which would make a fractional setpoint unreachable.
            AssertContains(payload, "\"step\":0.1");
        }

        [TestMethod]
        public void A_Fractional_Bound_Is_Rendered_The_Way_The_Property_Publishes_It()
        {
            // A declared bound and the values published against it have to be written the
            // same way, or a controller can refuse its own device's readings.
            var device = BuildSetpointDevice(NumericRange.Between(21.5, 23.25), decimals: 2, initialValue: 22.0);

            var payload = Find(
                DiscoveryMapper.Map(device, new HomeAssistantSettings()),
                "homeassistant/number/super-car_engine_setpoint/config").Payload;

            AssertContains(payload, "\"min\":21.50");
            AssertContains(payload, "\"max\":23.25");
            AssertContains(payload, "\"step\":0.01");
        }

        [TestMethod]
        public void A_Declared_Step_Is_Published_As_Declared()
        {
            var device = BuildSetpointDevice(NumericRange.Between(0, 10).WithStep(0.5), decimals: 1);

            var payload = Find(
                DiscoveryMapper.Map(device, new HomeAssistantSettings()),
                "homeassistant/number/super-car_engine_setpoint/config").Payload;

            AssertContains(payload, "\"step\":0.5");
        }

        [TestMethod]
        public void A_Derived_Step_Stops_At_The_Smallest_Home_Assistant_Accepts()
        {
            // Home Assistant's number schema refuses a step below 0.001 -- not clamped,
            // the whole configuration fails and the entity never appears. Capping the
            // step derived from the property's precision costs resolution in its controls
            // and nothing else: every value they can produce is one the property holds.
            var device = BuildSetpointDevice(NumericRange.Between(0, 1), decimals: 5);

            var payload = Find(
                DiscoveryMapper.Map(device, new HomeAssistantSettings()),
                "homeassistant/number/super-car_engine_setpoint/config").Payload;

            AssertContains(payload, "\"step\":0.001");
        }

        [TestMethod]
        public void Boolean_Payloads_Are_The_Ones_The_Model_Publishes()
        {
            // Home Assistant defaults to "ON"/"OFF"; a boolean property publishes exactly
            // "true"/"false" and accepts nothing else. Without these every reading would
            // be read as off and every command refused by the property.
            //
            // Note that these come from the datatype, not from BooleanLabels: labels say
            // what to call the two values for a human and do not define the payloads.
            var device = BuildDevice();

            var entities = DiscoveryMapper.Map(device, new HomeAssistantSettings());
            var sensor = Find(entities, "homeassistant/binary_sensor/super-car_engine_running/config").Payload;
            var toggle = Find(entities, "homeassistant/switch/super-car_engine_ignition/config").Payload;

            AssertContains(sensor, "\"pl_on\":\"true\"");
            AssertContains(sensor, "\"pl_off\":\"false\"");
            AssertMissing(sensor, "cmd_t");

            AssertContains(toggle, "\"pl_on\":\"true\"");
            AssertContains(toggle, "\"cmd_t\":\"smarthome/super-car/engine/ignition/set\"");
        }

        [TestMethod]
        public void Labels_Do_Not_Become_Payloads()
        {
            // The trap this mapping is most likely to fall into: payload_on and
            // payload_off look like the same idea as a boolean's labels and are the
            // opposite -- they *define* the payloads, where labels only name the values.
            // A device labelled "closed,open" still publishes true and false.
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddBooleanProperty("hatch", "Hatch", false)
                        .WithLabels("closed", "open")
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            var payload = Find(
                DiscoveryMapper.Map(device, new HomeAssistantSettings()),
                "homeassistant/binary_sensor/super-car_engine_hatch/config").Payload;

            AssertContains(payload, "\"pl_on\":\"true\"");
            AssertContains(payload, "\"pl_off\":\"false\"");
            AssertMissing(payload, "closed");
            AssertMissing(payload, "open");
        }

        [TestMethod]
        public void A_Settable_Enum_Becomes_A_Select_Carrying_The_Declared_Options()
        {
            var device = BuildDevice();

            var payload = Find(
                DiscoveryMapper.Map(device, new HomeAssistantSettings()),
                "homeassistant/select/super-car_engine_mode/config").Payload;

            // Trimmed by the model when the options were parsed, which is what keeps the
            // offered option and the payload the property accepts the same string.
            AssertContains(payload, "\"options\":[\"eco\",\"sport\"]");
            AssertContains(payload, "\"cmd_t\":\"smarthome/super-car/engine/mode/set\"");
        }

        [TestMethod]
        public void A_Settable_Enum_Without_Options_Becomes_A_Text_Entity()
        {
            // A select needs options, and Home Assistant refuses a configuration that
            // carries none -- so a select built from nothing is an entity that never
            // appears. An enum property that declares no options accepts any payload,
            // which is exactly a free-text control.
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddEnumProperty("mode", "Mode", "eco")
                        .WithSettable(true)
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            var payload = Find(
                DiscoveryMapper.Map(device, new HomeAssistantSettings()),
                "homeassistant/text/super-car_engine_mode/config").Payload;

            AssertContains(payload, "\"cmd_t\":\"smarthome/super-car/engine/mode/set\"");
            AssertMissing(payload, "options");
        }

        [TestMethod]
        public void A_Read_Only_Enum_With_Options_Becomes_An_Enum_Sensor()
        {
            // Home Assistant's 'enum' device class is exactly an enum property: a value
            // from a declared set. It forbids a unit and a state class -- both together
            // with options make the configuration invalid.
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddEnumProperty("gear", "Gear", "park")
                        .WithFormat("park,drive,reverse")
                        .WithUnit(Units.CountOrAmount)
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            var payload = Find(
                DiscoveryMapper.Map(device, new HomeAssistantSettings()),
                "homeassistant/sensor/super-car_engine_gear/config").Payload;

            AssertContains(payload, "\"dev_cla\":\"enum\"");
            AssertContains(payload, "\"options\":[\"park\",\"drive\",\"reverse\"]");
            AssertMissing(payload, "unit_of_meas");
            AssertMissing(payload, "stat_cla");
        }

        [TestMethod]
        public void A_Non_Numeric_Sensor_Carries_No_Unit()
        {
            // Home Assistant reads a unit of measurement as a statement that the state is
            // a number, and then reports every reading of a text sensor as an error.
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddStringProperty("plate", "Plate", "B-MW 1")
                        .WithUnit(Units.CountOrAmount)
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            var payload = Find(
                DiscoveryMapper.Map(device, new HomeAssistantSettings()),
                "homeassistant/sensor/super-car_engine_plate/config").Payload;

            AssertMissing(payload, "unit_of_meas");
            AssertMissing(payload, "stat_cla");
        }

        [TestMethod]
        public void A_Settable_Colour_Becomes_A_Text_Entity_Carrying_The_Triple()
        {
            // Not a light: Home Assistant's light is a composite of on/off, brightness
            // and colour across several topics and requires a command topic for the
            // on/off half that a colour property does not have. A text entity carries the
            // "<r>,<g>,<b>" the property already publishes and accepts.
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddColorProperty("trim", "Trim", new ColorValue())
                        .WithSettable(true)
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            var payload = Find(
                DiscoveryMapper.Map(device, new HomeAssistantSettings()),
                "homeassistant/text/super-car_engine_trim/config").Payload;

            AssertContains(payload, "\"cmd_t\":\"smarthome/super-car/engine/trim/set\"");
        }

        [TestMethod]
        public void Availability_Points_At_This_Adapters_Own_Status_Topic()
        {
            // A plain topic carrying 'online' or 'offline', which are Home Assistant's own
            // defaults -- so there is no availability template, and nothing here reads
            // another convention's lifecycle attribute.
            var device = BuildDevice();

            var payload = Find(
                DiscoveryMapper.Map(device, new HomeAssistantSettings()),
                "homeassistant/sensor/super-car_engine_temperature/config").Payload;

            AssertContains(payload, "\"avty_t\":\"smarthome/super-car/status\"");
            AssertMissing(payload, "avty_tpl");
        }

        [TestMethod]
        public void Entity_Ids_Span_The_Whole_Tree_Without_Colliding()
        {
            // Two different node/property pairs that a hyphen would join into one string:
            // 'tank-level' + 'litres' and 'tank' + 'level-litres'. Both configurations are
            // published retained to the same discovery topic, so the second overwrites the
            // first and one entity silently disappears. The separator is an underscore
            // precisely because an id cannot contain one.
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode("tank-level", "Tank level", "gauge")
                    .AddFloatProperty("litres", "Litres", 0.0)
                    .BuildProperty()
                .BuildNode()
                .AddNode("tank", "Tank", "gauge")
                    .AddFloatProperty("level-litres", "Level in litres", 0.0)
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            var entities = DiscoveryMapper.Map(device, new HomeAssistantSettings());

            Assert.AreEqual(2, entities.Length, "one entity per property");
            Assert.AreNotEqual(entities[0].Topic, entities[1].Topic, "two distinct discovery topics");
            AssertContains(entities[0].Payload, "\"uniq_id\":\"super-car_tank-level_litres\"");
            AssertContains(entities[1].Payload, "\"uniq_id\":\"super-car_tank_level-litres\"");
        }

        [TestMethod]
        public void A_Multi_Node_Device_Folds_The_Node_Name_Into_The_Entity_Name()
        {
            // Home Assistant has no node level and composes a displayed name from the
            // device and the entity. Two zones with a 'valve' property each are both
            // "Valve" without this, and read identically in every list Home Assistant
            // draws.
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode("zone-1", "Zone 1", "valve")
                    .AddBooleanProperty("valve", "Valve", false)
                    .BuildProperty()
                .BuildNode()
                .AddNode("zone-2", "Zone 2", "valve")
                    .AddBooleanProperty("valve", "Valve", false)
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            var entities = DiscoveryMapper.Map(device, new HomeAssistantSettings());

            AssertContains(
                Find(entities, "homeassistant/binary_sensor/super-car_zone-1_valve/config").Payload,
                "\"name\":\"Zone 1 Valve\"");
            AssertContains(
                Find(entities, "homeassistant/binary_sensor/super-car_zone-2_valve/config").Payload,
                "\"name\":\"Zone 2 Valve\"");

            // A single-node device says only the property's name: the node would be
            // repeating what the device already is.
            AssertContains(
                Find(DiscoveryMapper.Map(BuildDevice(), new HomeAssistantSettings()),
                     "homeassistant/sensor/super-car_engine_temperature/config").Payload,
                "\"name\":\"Temperature\"");
        }

        [TestMethod]
        public void Device_And_Origin_Blocks_Carry_What_The_Model_Cannot_Say()
        {
            var device = BuildDevice();
            var settings = new HomeAssistantSettings
            {
                Model = "ESP32",
                SoftwareVersion = "1.2.3",
                OriginName = "SmartHome",
            };

            var payload = Find(
                DiscoveryMapper.Map(device, settings),
                "homeassistant/sensor/super-car_engine_temperature/config").Payload;

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

            AssertMissing(Find(without, "homeassistant/sensor/super-car_engine_temperature/config").Payload, "exp_aft");
            AssertContains(Find(with, "homeassistant/sensor/super-car_engine_temperature/config").Payload, "\"exp_aft\":30");

            // Sensors only. A binary sensor is usually change-driven -- a window contact
            // publishes when the window moves and then stays silent for months -- so an
            // expiry would report a healthy device as unavailable.
            AssertMissing(Find(with, "homeassistant/binary_sensor/super-car_engine_running/config").Payload, "exp_aft");
        }

        [TestMethod]
        public void A_Quote_In_A_Name_Is_Escaped_Rather_Than_Breaking_The_Payload()
        {
            // A name is free text and reaches the payload verbatim. An unescaped quote
            // would truncate the JSON object, and Home Assistant would drop the entity
            // with a parse error rather than anything that points here.
            var device = new DeviceBuilder(_deviceId, "The \"fast\" one")
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("temperature", "Temperature", 0.0)
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            var payload = Find(
                DiscoveryMapper.Map(device, new HomeAssistantSettings()),
                "homeassistant/sensor/super-car_engine_temperature/config").Payload;

            AssertContains(payload, "\\\"fast\\\"");
        }

        [TestMethod]
        public void Refuses_A_Datatype_Home_Assistant_Has_No_Entity_For()
        {
            // Refused where the device is built rather than published as an approximation.
            // Each of these could be made to produce something, and each something would
            // be wrong in a way only a Home Assistant installation would show.
            var settings = new HomeAssistantSettings();

            Assert.ThrowsException(typeof(ArgumentException),
                () => DiscoveryMapper.Map(BuildDateTimeDevice(), settings));

            Assert.ThrowsException(typeof(ArgumentException),
                () => DiscoveryMapper.Map(BuildDurationDevice(), settings));

            Assert.ThrowsException(typeof(ArgumentException),
                () => DiscoveryMapper.Map(BuildJsonDevice(), settings));
        }

        [TestMethod]
        public void Refuses_A_Quantity_Kind_On_A_Property_That_Holds_No_Number()
        {
            // A quantity kind says what a *number* means. Home Assistant's device classes
            // for a boolean or a text entity are a separate vocabulary (door, motion,
            // moisture as wet-or-dry) and nothing in the model implies one.
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddBooleanProperty("wet", "Wet", false)
                        .WithQuantityKind(QuantityKind.Moisture)
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            Assert.ThrowsException(typeof(ArgumentException),
                () => DiscoveryMapper.Map(device, new HomeAssistantSettings()));
        }

        [TestMethod]
        public void Refuses_A_Unit_The_Device_Class_Does_Not_Accept()
        {
            // Home Assistant validates the pair and refuses the whole configuration when
            // they disagree, so the entity never appears -- and nothing on the device or
            // the broker says why.
            var wrongUnit = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("humidity", "Humidity", 0.0)
                        .WithUnit(Units.DegreeCelsius)
                        .WithQuantityKind(QuantityKind.Humidity)
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            Assert.ThrowsException(typeof(ArgumentException),
                () => DiscoveryMapper.Map(wrongUnit, new HomeAssistantSettings()));

            // No unit at all is the same mis-declaration: the class is valid only with one
            // of its units, and an entity with neither shows a bare number that Home
            // Assistant logs as invalid on every start.
            var noUnit = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("humidity", "Humidity", 0.0)
                        .WithQuantityKind(QuantityKind.Humidity)
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            Assert.ThrowsException(typeof(ArgumentException),
                () => DiscoveryMapper.Map(noUnit, new HomeAssistantSettings()));
        }

        [TestMethod]
        public void Refuses_A_Settable_Number_That_Does_Not_Declare_What_It_Accepts()
        {
            // Home Assistant's number entity always has a minimum and a maximum -- its own
            // defaults are 0 and 100 -- and it *drops* any state outside them. A property
            // that declares no range would have its own readings refused by the controller
            // meant to display them, and one that declares a single end has the same
            // failure on the other side.
            var settings = new HomeAssistantSettings();

            Assert.ThrowsException(typeof(ArgumentException),
                () => DiscoveryMapper.Map(BuildSetpointDevice(null, decimals: 1), settings));

            Assert.ThrowsException(typeof(ArgumentException),
                () => DiscoveryMapper.Map(BuildSetpointDevice(NumericRange.AtLeast(5), decimals: 1, initialValue: 5.0), settings));

            Assert.ThrowsException(typeof(ArgumentException),
                () => DiscoveryMapper.Map(BuildSetpointDevice(NumericRange.AtMost(30), decimals: 1), settings));

            // A read-only property is untouched by any of this: a sensor has no bounds to
            // declare, so a range it cannot carry is simply not published.
            var readOnly = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("temperature", "Temperature", 0.0)
                        .WithRange(NumericRange.AtLeast(-40))
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            Assert.AreEqual(1, DiscoveryMapper.Map(readOnly, settings).Length);
        }

        [TestMethod]
        public void Refuses_A_Step_Below_What_Home_Assistant_Accepts()
        {
            // Declared, not derived: a step this small is a statement about the property
            // that Home Assistant's schema cannot carry at all, and widening it quietly
            // would advertise a granularity the device did not declare.
            var device = BuildSetpointDevice(NumericRange.Between(0, 1).WithStep(0.0005), decimals: 4);

            Assert.ThrowsException(typeof(ArgumentException),
                () => DiscoveryMapper.Map(device, new HomeAssistantSettings()));
        }

        [TestMethod]
        public void Refuses_A_Bound_The_Property_Itself_Could_Not_Publish()
        {
            // An integer property's payloads are whole numbers, so a controller offered
            // 0.5 would send a value the property refuses.
            var fractionalOnAnInteger = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddIntegerProperty("gear", "Gear", 1)
                        .WithSettable(true)
                        .WithRange(NumericRange.Between(0.5, 6))
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            Assert.ThrowsException(typeof(ArgumentException),
                () => DiscoveryMapper.Map(fractionalOnAnInteger, new HomeAssistantSettings()));

            // And a bound a float property cannot write at its own precision is a
            // different bound once rounded: exactly the payloads at the edge would be
            // advertised as acceptable and then rejected.
            var tooPrecise = BuildSetpointDevice(NumericRange.Between(0.125, 10), decimals: 2);

            Assert.ThrowsException(typeof(ArgumentException),
                () => DiscoveryMapper.Map(tooPrecise, new HomeAssistantSettings()));
        }

        /// <summary>
        /// One settable float, so a test can vary nothing but its range.
        /// </summary>
        /// <remarks>
        /// The initial value is a parameter because the model holds it to the declared
        /// range: a fixture that seeded a value outside the range under test would throw
        /// from the builder, and a test expecting the mapper to refuse something would
        /// pass on the wrong exception.
        /// </remarks>
        private static Device BuildSetpointDevice(NumericRange? range, int decimals, double initialValue = 1.0) =>
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("setpoint", "Setpoint", initialValue)
                        .WithSettable(true)
                        .WithRange(range)
                        .WithDecimals(decimals)
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

        private static Device BuildDateTimeDevice() =>
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddDateTimeProperty("serviced", "Last serviced")
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

        private static Device BuildDurationDevice() =>
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddDurationProperty("uptime", "Uptime")
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

        private static Device BuildJsonDevice() =>
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddJsonProperty("reading", "Reading", "{}")
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

        private static Device BuildDevice() =>
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("temperature", "Temperature", 0.0)
                        .WithUnit(Units.DegreeCelsius)
                        .WithQuantityKind(QuantityKind.Temperature)
                    .BuildProperty()
                    .AddFloatProperty("humidity", "Humidity", 0.0)
                        .WithUnit(Units.Percent)
                        .WithQuantityKind(QuantityKind.Humidity)
                    .BuildProperty()
                    .AddFloatProperty("pressure", "Pressure", 0.0)
                        .WithUnit(Units.Pascal)
                        .WithQuantityKind(QuantityKind.Pressure)
                    .BuildProperty()
                    .AddFloatProperty("setpoint", "Setpoint", 20.0)
                        .WithUnit(Units.DegreeCelsius)
                        .WithQuantityKind(QuantityKind.Temperature)
                        .WithSettable(true)
                        .WithRange(NumericRange.Between(5, 30))
                        .WithDecimals(1)
                    .BuildProperty()
                    .AddBooleanProperty("running", "Running", false)
                    .BuildProperty()
                    .AddBooleanProperty("ignition", "Ignition", false)
                        .WithSettable(true)
                    .BuildProperty()
                    .AddEnumProperty("mode", "Mode", "eco")
                        .WithFormat("eco, sport")
                        .WithSettable(true)
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

        /// <summary>Properties across every node, i.e. how many entities to expect.</summary>
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
