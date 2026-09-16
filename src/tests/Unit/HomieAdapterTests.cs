using SmartHome.DeviceModel;
using SmartHome.DeviceModel.Builder;
using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.Formats;
using SmartHome.DeviceModel.Properties;
using SmartHome.Homie.V4;
using SmartHome.Homie.V4.Settings;
using SmartHome.Protocol;
using SmartHome.Text;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using nanoFramework.TestFramework;
using System;

namespace SmartHome.UnitTests
{
    // Everything the adapter adds to the neutral model: the topic grammar, the $state
    // vocabulary, the attribute renderings, the device attributes the model deliberately
    // does not carry -- and the refusals, which are the other half of "byte-identical on
    // the wire". A convention that cannot express something has to say so when the device
    // is built, not publish an approximation a controller will act on.
    [TestClass]
    public class HomieAdapterTests
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
        public void HomieClient_Is_Driven_Entirely_Through_The_Protocol_Seam()
        {
            // A device app builds a neutral Device, constructs one adapter, and then
            // talks only to IDeviceProtocol -- so which convention it speaks is a choice
            // about the line below and nowhere else. IHomieClient adds exactly one thing
            // on top of the seam, the $state token, for a consumer that has to render or
            // compare it; everything else a v4 device needs is already there.
            var mqttClient = new MockMqttClient();
            IDeviceProtocol protocol = new HomieClient(BuildSinglePropertyDevice(), mqttClient);

            Assert.IsTrue(protocol.Connect());

            Assert.AreEqual(_deviceId, protocol.DeviceId);
            Assert.AreEqual((int)DeviceState.Ready, (int)protocol.State);
            Assert.IsTrue(protocol.IsConnected);
            Assert.AreEqual(HomieStates.Ready, ((IHomieClient)protocol).HomieState);

            protocol.Disconnect();
            Assert.IsFalse(protocol.IsConnected);
        }

        [TestMethod]
        public void HomieStates_Map_The_Model_Lifecycle_Onto_The_v4_Vocabulary()
        {
            // The model has five lifecycle states and a separate keyed alert set; v4 has
            // six $state tokens and nowhere to put an alert's id or message. This is the
            // whole of that mapping, and it is one-way: 'alert' is synthesised from "any
            // alert is raised", so nothing can read the id back off the wire.
            Assert.AreEqual(HomieStates.Init, HomieStates.From(DeviceState.Connecting, false));
            Assert.AreEqual(HomieStates.Ready, HomieStates.From(DeviceState.Ready, false));
            Assert.AreEqual(HomieStates.Alert, HomieStates.From(DeviceState.Ready, true));
            Assert.AreEqual(HomieStates.Sleeping, HomieStates.From(DeviceState.Sleeping, false));
            Assert.AreEqual(HomieStates.Disconnected, HomieStates.From(DeviceState.Disconnecting, false));
            Assert.AreEqual(HomieStates.Lost, HomieStates.From(DeviceState.Lost, false));

            // A sleeping device is not publishing, so it has nothing to say about an
            // alert either -- and v4's 'alert' is a running device in trouble, not a
            // sleeping one. An alert raised while asleep is held, not shown.
            Assert.AreEqual(HomieStates.Sleeping, HomieStates.From(DeviceState.Sleeping, true));

            // 'init' and 'disconnected' are about the connection, not about health, so an
            // alert cannot displace them either.
            Assert.AreEqual(HomieStates.Init, HomieStates.From(DeviceState.Connecting, true));
            Assert.AreEqual(HomieStates.Disconnected, HomieStates.From(DeviceState.Disconnecting, true));
        }

        [TestMethod]
        public void HomieTopics_Name_Every_Level_By_Walking_The_Parent_Chain()
        {
            // The model deliberately has no GetTopic(): one convention's root and one
            // convention's grammar in every entity is what made a second adapter
            // impossible. The chain is walked here instead, and only here.
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("temperature", "Temperature", 0.0)
                    .BuildProperty(out FloatProperty property)
                .BuildNode(out Node node)
                .BuildDevice();

            Assert.AreEqual("homie/super-car", HomieTopics.Of(device));
            Assert.AreEqual("homie/super-car/engine", HomieTopics.Of(node));
            Assert.AreEqual("homie/super-car/engine/temperature", HomieTopics.Of(property));
        }

        [TestMethod]
        public void HomieTopics_Name_An_Attribute_And_A_Command_Topic()
        {
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("intensity", "Intensity", 0.0)
                        .WithSettable(true)
                        .WithUnit(Units.Percent)
                    .BuildProperty(out FloatProperty property)
                .BuildNode(out Node node)
                .BuildDevice();

            Assert.AreEqual("homie/super-car/$state", HomieTopics.Attribute(device, Constants.StateAttributeTopicId));
            Assert.AreEqual("homie/super-car/engine/$type", HomieTopics.Attribute(node, Constants.TypeAttributeTopicId));
            Assert.AreEqual("homie/super-car/engine/intensity/$unit", HomieTopics.Attribute(property, Constants.UnitAttributeTopicId));

            // The spec puts commands on homie/[device]/[node]/[property]/set. Keying the
            // subscription by the property topic instead -- as this code did until
            // 2026-08-21 -- meant controller commands were never received, and the device
            // subscribed to its own retained value topic and set itself from it.
            Assert.AreEqual("homie/super-car/engine/intensity/set", HomieTopics.Command(property));
        }

        [TestMethod]
        public void HomieClient_Refuses_A_Datatype_v4_Does_Not_Define()
        {
            // datetime, duration and json exist in the model because a later convention
            // has them. v4 does not, and there is no token to invent: a $datatype a
            // controller does not know is worse than a device that refused to start,
            // because the refusal is visible and the bad token is not.
            var mqttClient = new MockMqttClient();

            var withDateTime = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddDateTimeProperty("measured-at", "Measured at")
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            Assert.ThrowsException(typeof(ArgumentException), () => new HomieClient(withDateTime, mqttClient));

            var withDuration = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddDurationProperty("uptime", "Uptime")
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            Assert.ThrowsException(typeof(ArgumentException), () => new HomieClient(withDuration, mqttClient));

            var withJson = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddJsonProperty("reading", "Reading", "{}")
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            Assert.ThrowsException(typeof(ArgumentException), () => new HomieClient(withJson, mqttClient));

            // Nothing was published on the way to any of those refusals: the walk happens
            // in the constructor, before a session exists.
            Assert.AreEqual(0, mqttClient.PublishCount);
        }

        [TestMethod]
        public void HomieClient_Refuses_A_Range_v4_Cannot_Express()
        {
            // v4's integer and float $format is 'from:to' with both ends present. An
            // open-ended range and a step have no spelling at all, and publishing the
            // closed part of an open range would advertise a bound the property does not
            // enforce -- the exact disagreement between declaration and enforcement the
            // structured formats exist to end.
            var mqttClient = new MockMqttClient();

            Assert.ThrowsException(typeof(ArgumentException),
                () => new HomieClient(BuildFloatWithRange(NumericRange.AtLeast(0)), mqttClient));

            Assert.ThrowsException(typeof(ArgumentException),
                () => new HomieClient(BuildFloatWithRange(NumericRange.AtMost(100)), mqttClient));

            Assert.ThrowsException(typeof(ArgumentException),
                () => new HomieClient(BuildFloatWithRange(NumericRange.Between(0, 10).WithStep(2)), mqttClient));

            Assert.ThrowsException(typeof(ArgumentException),
                () => new HomieClient(BuildIntegerWithRange(NumericRange.AtLeast(0)), mqttClient));

            Assert.ThrowsException(typeof(ArgumentException),
                () => new HomieClient(BuildIntegerWithRange(NumericRange.Between(0, 10).WithStep(2)), mqttClient));

            // A closed range without a step is the shape v4 can carry, so it builds.
            Assert.IsNotNull(new HomieClient(BuildFloatWithRange(NumericRange.Between(0, 100)), mqttClient));
        }

        [TestMethod]
        public void HomieClient_Refuses_An_Integer_Bound_That_Is_Not_Integral()
        {
            // v4's integer $format is 'from:to' of integers. A bound of 0.5 would be
            // rendered by rounding or truncation, and either one advertises a range the
            // property does not enforce.
            var mqttClient = new MockMqttClient();

            Assert.ThrowsException(typeof(ArgumentException),
                () => new HomieClient(BuildIntegerWithRange(NumericRange.Between(0.5, 100)), mqttClient));

            // A bound outside int's range is NOT refused: it renders exactly, and the old
            // code published it. The refusal is about integrality, not about magnitude --
            // up to the point where the integral rendering itself stops being safe.
            Assert.IsNotNull(new HomieClient(BuildIntegerWithRange(NumericRange.Between(0, 1e10)), mqttClient));

            // 2^63 exactly, which is where the magnitude guard is load-bearing rather
            // than merely cautious: the runtime's double-to-long conversion is an
            // unchecked C cast, and on a target that saturates it, an unguarded
            // (long)2^63 comes back as long.MaxValue -- which compares EQUAL to the bound
            // as a double, so the integrality test would pass and 9223372036854775807
            // would be published as a bound the property never had.
            Assert.ThrowsException(typeof(ArgumentException),
                () => new HomieClient(BuildIntegerWithRange(NumericRange.Between(0, 9223372036854775808.0)), mqttClient));
        }

        [TestMethod]
        public void HomieClient_Refuses_A_Float_Bound_It_Cannot_Render_Exactly()
        {
            // A bound is rendered at the property's own precision, so a bound with more
            // places than the property publishes cannot be stated: 0.005 at two decimals
            // is either 0.00 or 0.01, and both are a different range from the one the
            // property enforces.
            var mqttClient = new MockMqttClient();

            Assert.ThrowsException(typeof(ArgumentException),
                () => new HomieClient(BuildFloatWithRange(NumericRange.Between(0, 0.005), decimals: 2), mqttClient));

            // ... and one whose magnitude the property could not publish as a value
            // either: its fixed-decimal form does not fit the runtime's format buffer,
            // where a value too wide is silently truncated rather than refused.
            Assert.ThrowsException(typeof(ArgumentException),
                () => new HomieClient(BuildFloatWithRange(NumericRange.Between(0, FloatProperty.MaxPublishableMagnitude)), mqttClient));

            // The same bound at a precision that can state it is fine.
            Assert.IsNotNull(new HomieClient(BuildFloatWithRange(NumericRange.Between(0, 0.005), decimals: 3), mqttClient));

            // Large bounds are decided by the same question, and that is the point: the
            // check round-trips the rendered string rather than comparing against a
            // tolerance, so it does not go blind as the magnitude grows. A tolerance
            // wide enough to absorb the scaling multiplication's own error exceeds the
            // worst real rounding error above roughly 5e11 at two decimals, and from
            // there up everything passed -- including this bound, whose two-decimal
            // rendering 1000000000000.01 is a number the property would then refuse.
            Assert.ThrowsException(typeof(ArgumentException),
                () => new HomieClient(BuildFloatWithRange(NumericRange.Between(0, 1000000000000.005), decimals: 2), mqttClient));

            // ... while a bound of the same magnitude that IS exactly renderable still
            // builds, so the guard discriminates up there rather than refusing
            // everything large.
            Assert.IsNotNull(new HomieClient(BuildFloatWithRange(NumericRange.Between(0, 1000000000000.25), decimals: 2), mqttClient));

            // 1234567890.12 is the case a fixed tolerance had to be widened for: scaled
            // by 100 it lands ~1.5e-5 from a whole number even though it renders
            // exactly. The round trip reads it as what it is.
            Assert.IsNotNull(new HomieClient(BuildFloatWithRange(NumericRange.Between(0, 1234567890.12), decimals: 2), mqttClient));

            // Past long's range, where the whole-number shortcut cannot run: the bound
            // goes through the property's own fixed-decimal encoding instead, which
            // states it exactly.
            var wide = new HomieClient(BuildFloatWithRange(NumericRange.Between(0, 1e19), decimals: 2), mqttClient);
            wide.Connect();
            Assert.AreEqual("0:10000000000000000000.00", Single(mqttClient, $"homie/{_deviceId}/{_nodeId}/temperature/$format"));
        }

        [TestMethod]
        public void HomieClient_Renders_Format_From_The_Parsed_Declaration()
        {
            // $format is rendered from the structured value the model parsed, not echoed
            // from the text a device author wrote -- the adapter cannot see that text at
            // all. Identical output for everything in this tree; the visible consequence
            // is that a malformed declaration, which parses to no format, now renders as
            // an empty $format rather than being republished verbatim.
            var mqttClient = new MockMqttClient();

            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddIntegerProperty("integer-value", "Integer", 0)
                        .WithFormat("0:100")
                    .BuildProperty(out IntegerProperty integer)
                    .AddFloatProperty("float-value", "Float", 0.0)
                        .WithFormat("0:100")
                    .BuildProperty(out FloatProperty realNumber)
                    .AddFloatProperty("bounded", "Bounded", 0.0)
                        .WithRange(NumericRange.Between(-10.5, 40.25))
                        .WithDecimals(2)
                    .BuildProperty(out FloatProperty bounded)
                    .AddFloatProperty("mixed", "Mixed", 0.0)
                        .WithRange(NumericRange.Between(-10.5, 40))
                        .WithDecimals(2)
                    .BuildProperty(out FloatProperty mixed)
                    .AddFloatProperty("unbounded", "Unbounded", 0.0)
                    .BuildProperty(out FloatProperty unbounded)
                    .AddEnumProperty("enum-value", "Enum", "low")
                        .WithFormat("low, medium, high")
                    .BuildProperty(out EnumProperty choice)
                    .AddColorProperty("color-value", "Colour", default)
                        .WithFormats(new string[] { ColorFormats.Rgb, ColorFormats.Hsv })
                    .BuildProperty(out ColorProperty colour)
                    .AddBooleanProperty("boolean-value", "Boolean", false)
                        .WithLabels("closed", "open")
                    .BuildProperty(out BooleanProperty flag)
                    .AddStringProperty("string-value", "String", "initial")
                    .BuildProperty(out StringProperty text)
                .BuildNode()
                .BuildDevice();

            new HomieClient(device, mqttClient).Connect();

            // An integral bound renders as an integer whatever the datatype, which is
            // what keeps a declared "0:100" byte-identical on both.
            Assert.AreEqual("0:100", FormatOf(mqttClient, integer));
            Assert.AreEqual("0:100", FormatOf(mqttClient, realNumber));

            // A bound that is not integral is rendered by the property's own encoding --
            // the same fixed-decimal rendering its values go out as, because a range
            // rendered any other way would advertise bounds the property's own payloads
            // could never equal.
            Assert.AreEqual("-10.50:40.25", FormatOf(mqttClient, bounded));
            Assert.AreEqual("-10.50:40", FormatOf(mqttClient, mixed));

            // All six property attributes are published even when they carry nothing, so
            // "no range" is an empty $format rather than a missing topic.
            Assert.AreEqual(string.Empty, FormatOf(mqttClient, unbounded));

            // The options were trimmed once, when they were declared.
            Assert.AreEqual("low,medium,high", FormatOf(mqttClient, choice));

            // v4 declares exactly one colour encoding, so only the preferred one goes out
            // -- a comma-separated list there would read as an rgb triple.
            Assert.AreEqual(ColorFormats.Rgb, FormatOf(mqttClient, colour));

            // v4 has no boolean format and no string format: the labels the model carries
            // for a human have nowhere to go, and are dropped rather than invented into
            // one.
            Assert.AreEqual(string.Empty, FormatOf(mqttClient, flag));
            Assert.AreEqual(string.Empty, FormatOf(mqttClient, text));
        }

        [TestMethod]
        public void HomieClient_Publishes_The_Device_Attributes_The_Model_Does_Not_Carry()
        {
            // $extensions and $implementation are v4's own, so they live in the adapter's
            // settings rather than on the neutral device. $extensions is mandatory --
            // "MUST be sent, even if it is just an empty string" -- and $implementation is
            // optional, so an empty one is left unsaid rather than published empty.
            var mqttClient = new MockMqttClient();
            var device = BuildSinglePropertyDevice();

            var settings = new HomieDeviceSettings
            {
                Extensions = new string[] { "org.homie.legacy-firmware:0.1.1:[4.x]", "org.homie.legacy-stats:0.1.1:[4.x]" },
                Implementation = "smarthome-nanoframework",
            };

            new HomieClient(device, mqttClient, null, null, null, settings).Connect();

            Assert.AreEqual(
                "org.homie.legacy-firmware:0.1.1:[4.x],org.homie.legacy-stats:0.1.1:[4.x]",
                Single(mqttClient, HomieTopics.Attribute(device, Constants.ExtensionAttributeTopicId)));

            Assert.AreEqual(
                "smarthome-nanoframework",
                Single(mqttClient, HomieTopics.Attribute(device, Constants.ImplementationAttributeTopicId)));

            // 16 is the announcement of a single-property device; $implementation is the
            // seventeenth publish, and the only difference from the default settings.
            Assert.AreEqual(17, mqttClient.PublishCount);
        }

        [TestMethod]
        public void HomieClient_Publishes_An_Empty_Extensions_And_No_Implementation_By_Default()
        {
            var mqttClient = new MockMqttClient();
            var device = BuildSinglePropertyDevice();

            new HomieClient(device, mqttClient).Connect();

            // Published, and empty. An empty retained payload does not survive in the
            // broker's store -- MQTT defines a zero-length retained message as deleting
            // the retained one -- so this can only ever be asserted on the live stream,
            // which is what the conformance suite does on hardware and what the mock is
            // standing in for here.
            Assert.AreEqual(string.Empty, Single(mqttClient, HomieTopics.Attribute(device, Constants.ExtensionAttributeTopicId)));

            Assert.AreEqual(
                0,
                mqttClient.PayloadsFor(HomieTopics.Attribute(device, Constants.ImplementationAttributeTopicId)).Length,
                "an unset $implementation must not be published at all, not published empty");
        }

        [TestMethod]
        public void HomieClient_Publishes_Alert_Once_However_Many_Alerts_Are_Raised()
        {
            // v4 carries a single 'alert', so the second alert, and a new message on one
            // already raised, change nothing on the wire. Republishing the same token
            // every time a device re-raises from its measurement loop would be noise, and
            // retained noise at that.
            var mqttClient = new MockMqttClient();
            var device = BuildSinglePropertyDevice();
            var homieClient = new HomieClient(device, mqttClient);
            var stateTopic = HomieTopics.Attribute(device, Constants.StateAttributeTopicId);

            homieClient.Connect();

            homieClient.RaiseAlert("battery", "Battery is low, at 8%");
            homieClient.RaiseAlert("sensor", "The sensor did not answer");
            homieClient.RaiseAlert("battery", "Battery is low, at 5%");

            // Clearing one of two leaves the device alerting, so nothing changes here
            // either.
            homieClient.ClearAlert("battery");

            var states = mqttClient.PayloadsFor(stateTopic);
            Assert.AreEqual(3, states.Length, $"unexpected $state sequence: {StringUtils.Join(", ", states)}");
            Assert.AreEqual(HomieStates.Alert, states[2]);

            // Clearing the last one is the change that shows.
            homieClient.ClearAlert("sensor");

            states = mqttClient.PayloadsFor(stateTopic);
            Assert.AreEqual(4, states.Length, $"unexpected $state sequence: {StringUtils.Join(", ", states)}");
            Assert.AreEqual(HomieStates.Ready, states[3]);
            Assert.AreEqual(HomieStates.Ready, homieClient.HomieState);
        }

        [TestMethod]
        public void HomieClient_Does_Not_Show_An_Alert_While_Sleeping()
        {
            // The mapping is lossy in one direction only: the alert is held in the model
            // and shows the moment the device is awake again, which is why Ready() from
            // sleeping publishes 'alert' rather than 'ready'. Ready() does not clear
            // alerts -- they are keyed, and only ClearAlert clears one.
            var mqttClient = new MockMqttClient();
            var device = BuildSinglePropertyDevice();
            var homieClient = new HomieClient(device, mqttClient);
            var stateTopic = HomieTopics.Attribute(device, Constants.StateAttributeTopicId);

            homieClient.Connect();
            homieClient.Sleep();

            homieClient.RaiseAlert("battery", "Battery is low, at 8%");

            var states = mqttClient.PayloadsFor(stateTopic);
            Assert.AreEqual(3, states.Length, $"an alert reached the wire while sleeping: {StringUtils.Join(", ", states)}");
            Assert.AreEqual(HomieStates.Sleeping, homieClient.HomieState);

            homieClient.Ready();

            states = mqttClient.PayloadsFor(stateTopic);
            Assert.AreEqual(4, states.Length, $"unexpected $state sequence: {StringUtils.Join(", ", states)}");
            Assert.AreEqual(HomieStates.Alert, states[3], "waking up cleared an alert nobody resolved");
            Assert.AreEqual(HomieStates.Alert, homieClient.HomieState);
        }

        private static string FormatOf(MockMqttClient mqttClient, PropertyBase property)
            => Single(mqttClient, HomieTopics.Attribute(property, Constants.FormatAttributeTopicId));

        private static string Single(MockMqttClient mqttClient, string topic)
        {
            var payloads = mqttClient.PayloadsFor(topic);
            Assert.AreEqual(1, payloads.Length, $"expected exactly one publish on '{topic}'");
            return payloads[0];
        }

        private static Device BuildSinglePropertyDevice()
            => new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("temperature", "Temperature", 0.0)
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

        private static Device BuildFloatWithRange(NumericRange range, int decimals = FloatProperty.DefaultDecimals)
            => new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("temperature", "Temperature", 0.0)
                        .WithRange(range)
                        .WithDecimals(decimals)
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

        private static Device BuildIntegerWithRange(NumericRange range)
            => new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddIntegerProperty("speed", "Speed", 1)
                        .WithRange(range)
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();
    }
}
