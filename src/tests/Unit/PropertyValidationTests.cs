using SmartHome.Homie.V4;
using SmartHome.Homie.V4.Builder;
using SmartHome.Homie.V4.Extensions;
using SmartHome.Homie.V4.Properties;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using nanoFramework.M2Mqtt.Messages;
using nanoFramework.TestFramework;
using System.Text;

namespace SmartHome.UnitTests
{
    // A settable property used to accept payloads its own $datatype and $format forbid,
    // and the types disagreed about what to do with one: EnumProperty accepted anything,
    // the numeric types ignored a declared range, FloatProperty and ColorProperty dropped
    // an unparseable payload silently, and BooleanProperty turned everything unrecognised
    // into false -- fabricating a value, not merely failing to refuse one.
    //
    // Every case below asserts the same thing: the property did not move. That is the
    // whole contract. Homie has no "command refused" channel, so a value that did not
    // change is all a controller gets, and it is the same signal the convention gives for
    // any other refusal (issue #39, and #33 for why the *app*-level refusal is separate).
    [TestClass]
    public class PropertyValidationTests
    {
        private const string _deviceTopicId = "super-car";
        private const string _deviceName = "Super car";
        private const string _nodeTopicId = "matrix";
        private const string _nodeName = "Datatype matrix";
        private const string _nodeType = "conformance";

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
        public void IntegerProperty_Rejects_A_Payload_Outside_Its_Declared_Range()
        {
            var mqttClient = BuildClient();
            var builder = new HomieDeviceBuilder(_deviceTopicId, _deviceName);
            var device = builder.AddNode(_nodeTopicId, _nodeName, _nodeType)
                        .AddIntegerProperty("integer-value", "Integer", 7)
                            .WithSettable(true)
                            .WithFormat("0:100")
                        .BuildProperty(out IntegerProperty property)
                    .BuildNode()
                .BuildDevice();

            Connect(device, mqttClient);

            // Both ends are inclusive, so the bounds themselves are commands, not
            // violations -- a range check that is off by one at the edge is the usual way
            // this kind of validation goes wrong.
            SendCommand(mqttClient, property, "0");
            Assert.AreEqual(0, property.Value);

            SendCommand(mqttClient, property, "100");
            Assert.AreEqual(100, property.Value);

            SendCommand(mqttClient, property, "101");
            Assert.AreEqual(100, property.Value, "a value above the declared maximum moved the property");

            SendCommand(mqttClient, property, "-1");
            Assert.AreEqual(100, property.Value, "a value below the declared minimum moved the property");
        }

        [TestMethod]
        public void IntegerProperty_Rejects_A_Payload_That_Is_Not_An_Integer()
        {
            var mqttClient = BuildClient();
            var builder = new HomieDeviceBuilder(_deviceTopicId, _deviceName);
            var device = builder.AddNode(_nodeTopicId, _nodeName, _nodeType)
                        .AddIntegerProperty("integer-value", "Integer", 7)
                            .WithSettable(true)
                        .BuildProperty(out IntegerProperty property)
                    .BuildNode()
                .BuildDevice();

            Connect(device, mqttClient);

            SendCommand(mqttClient, property, "twelve");

            Assert.AreEqual(7, property.Value);
        }

        [TestMethod]
        public void FloatProperty_Rejects_A_Payload_Outside_Its_Declared_Range()
        {
            var mqttClient = BuildClient();
            var builder = new HomieDeviceBuilder(_deviceTopicId, _deviceName);
            var device = builder.AddNode(_nodeTopicId, _nodeName, _nodeType)
                        .AddFloatProperty("float-value", "Float", 5.0)
                            .WithSettable(true)
                            .WithFormat("-10:10.5")
                        .BuildProperty(out FloatProperty property)
                    .BuildNode()
                .BuildDevice();

            Connect(device, mqttClient);

            SendCommand(mqttClient, property, "10.5");
            Assert.AreEqual(10.5, property.Value);

            SendCommand(mqttClient, property, "10.6");
            Assert.AreEqual(10.5, property.Value, "a value above the declared maximum moved the property");

            SendCommand(mqttClient, property, "-10.1");
            Assert.AreEqual(10.5, property.Value, "a value below the declared minimum moved the property");
        }

        [TestMethod]
        public void FloatProperty_Rejects_A_Payload_That_Is_Not_A_Number()
        {
            var mqttClient = BuildClient();
            var builder = new HomieDeviceBuilder(_deviceTopicId, _deviceName);
            var device = builder.AddNode(_nodeTopicId, _nodeName, _nodeType)
                        .AddFloatProperty("float-value", "Float", 5.0)
                            .WithSettable(true)
                        .BuildProperty(out FloatProperty property)
                    .BuildNode()
                .BuildDevice();

            Connect(device, mqttClient);

            SendCommand(mqttClient, property, "warm");

            Assert.AreEqual(5.0, property.Value);
        }

        [TestMethod]
        public void FloatProperty_Rejects_A_Payload_With_No_Homie_Representation()
        {
            // NaN and the infinities have no Homie float payload -- EnsurePublishable
            // throws on them, and SetInternal runs on M2Mqtt's dispatch thread, whose
            // catch-all treats an escaping exception as a dead connection. So a
            // controller must not be able to reach that throw with a payload, whether or
            // not this runtime's double.TryParse happens to accept the spelling.
            var mqttClient = BuildClient();
            var builder = new HomieDeviceBuilder(_deviceTopicId, _deviceName);
            var device = builder.AddNode(_nodeTopicId, _nodeName, _nodeType)
                        .AddFloatProperty("float-value", "Float", 5.0)
                            .WithSettable(true)
                        .BuildProperty(out FloatProperty property)
                    .BuildNode()
                .BuildDevice();

            Connect(device, mqttClient);

            SendCommand(mqttClient, property, "NaN");
            SendCommand(mqttClient, property, "Infinity");
            SendCommand(mqttClient, property, "-Infinity");

            Assert.AreEqual(5.0, property.Value);
        }

        [TestMethod]
        public void BooleanProperty_Rejects_A_Payload_That_Is_Not_True_Or_False()
        {
            // The worst of the old behaviours, and the reason this is worth doing before
            // an actuator exists: an unrecognised payload did not fail to apply, it
            // applied 'false'. A relay would have opened, and the broker would have
            // carried a retained 'false' no controller ever asked for.
            var mqttClient = BuildClient();
            var builder = new HomieDeviceBuilder(_deviceTopicId, _deviceName);
            var device = builder.AddNode(_nodeTopicId, _nodeName, _nodeType)
                        .AddBooleanProperty("boolean-value", "Boolean", true)
                            .WithSettable(true)
                        .BuildProperty(out BooleanProperty property)
                    .BuildNode()
                .BuildDevice();

            Connect(device, mqttClient);

            SendCommand(mqttClient, property, "on");
            Assert.IsTrue(property.Value, "'on' was fabricated into false");

            SendCommand(mqttClient, property, "1");
            Assert.IsTrue(property.Value, "'1' was fabricated into false");

            SendCommand(mqttClient, property, "True");
            Assert.IsTrue(property.Value, "'True' was fabricated into false");

            // ... and the two payloads the convention does define still work.
            SendCommand(mqttClient, property, "false");
            Assert.IsFalse(property.Value);

            SendCommand(mqttClient, property, "true");
            Assert.IsTrue(property.Value);
        }

        [TestMethod]
        public void EnumProperty_Rejects_A_Payload_Its_Format_Does_Not_List()
        {
            var mqttClient = BuildClient();
            var builder = new HomieDeviceBuilder(_deviceTopicId, _deviceName);
            var device = builder.AddNode(_nodeTopicId, _nodeName, _nodeType)
                        .AddEnumProperty("enum-value", "Enum", "low")
                            .WithSettable(true)
                            .WithFormat("low,medium,high")
                        .BuildProperty(out EnumProperty property)
                    .BuildNode()
                .BuildDevice();

            Connect(device, mqttClient);

            SendCommand(mqttClient, property, "high");
            Assert.AreEqual("high", property.Value);

            SendCommand(mqttClient, property, "purple");
            Assert.AreEqual("high", property.Value, "a value $format does not list moved the property");

            // A prefix of a listed value is not a listed value.
            SendCommand(mqttClient, property, "med");
            Assert.AreEqual("high", property.Value);
        }

        [TestMethod]
        public void EnumProperty_Accepts_Anything_When_It_Declares_No_Format()
        {
            // Homie requires $format on an enum, but a property that declares none has
            // declared nothing to violate. Refusing everything here would turn a device
            // author's omission into a property no controller can ever set.
            var mqttClient = BuildClient();
            var builder = new HomieDeviceBuilder(_deviceTopicId, _deviceName);
            var device = builder.AddNode(_nodeTopicId, _nodeName, _nodeType)
                        .AddEnumProperty("enum-value", "Enum", "low")
                            .WithSettable(true)
                        .BuildProperty(out EnumProperty property)
                    .BuildNode()
                .BuildDevice();

            Connect(device, mqttClient);

            SendCommand(mqttClient, property, "purple");

            Assert.AreEqual("purple", property.Value);
        }

        [TestMethod]
        public void ColorProperty_Rejects_A_Payload_That_Is_Not_An_Rgb_Triple()
        {
            var mqttClient = BuildClient();
            var builder = new HomieDeviceBuilder(_deviceTopicId, _deviceName);
            var device = builder.AddNode(_nodeTopicId, _nodeName, _nodeType)
                        .AddColorProperty("color-value", "Colour", new HomieColor { R = 1, G = 2, B = 3 })
                            .WithSettable(true)
                            .WithFormat("rgb")
                        .BuildProperty(out ColorProperty property)
                    .BuildNode()
                .BuildDevice();

            Connect(device, mqttClient);

            SendCommand(mqttClient, property, "FF8000");
            SendCommand(mqttClient, property, "255,128");
            SendCommand(mqttClient, property, "255,128,256");

            Assert.AreEqual(1, (int)property.Value.R);
            Assert.AreEqual(2, (int)property.Value.G);
            Assert.AreEqual(3, (int)property.Value.B);

            SendCommand(mqttClient, property, "255,128,0");

            Assert.AreEqual(255, (int)property.Value.R);
            Assert.AreEqual(128, (int)property.Value.G);
            Assert.AreEqual(0, (int)property.Value.B);
        }

        [TestMethod]
        public void StringProperty_Accepts_Any_Payload()
        {
            // Nothing to violate: every payload is a valid Homie string, and $format
            // carries no meaning for this datatype.
            var mqttClient = BuildClient();
            var builder = new HomieDeviceBuilder(_deviceTopicId, _deviceName);
            var device = builder.AddNode(_nodeTopicId, _nodeName, _nodeType)
                        .AddStringProperty("string-value", "String", "initial")
                            .WithSettable(true)
                        .BuildProperty(out StringProperty property)
                    .BuildNode()
                .BuildDevice();

            Connect(device, mqttClient);

            SendCommand(mqttClient, property, "anything at all, 255,128 included");

            Assert.AreEqual("anything at all, 255,128 included", property.Value);
        }

        [TestMethod]
        public void HomieClient_Publishes_Nothing_For_A_Rejected_Payload()
        {
            // The refusal has to leave no trace at the broker. A rejected payload that
            // was still reflected would be worse than accepting it: the retained store
            // would advertise a value the device does not hold, to every controller that
            // connects afterwards.
            var mqttClient = BuildClient();
            var builder = new HomieDeviceBuilder(_deviceTopicId, _deviceName);
            var device = builder.AddNode(_nodeTopicId, _nodeName, _nodeType)
                        .AddIntegerProperty("integer-value", "Integer", 7)
                            .WithSettable(true)
                            .WithFormat("0:100")
                        .BuildProperty(out IntegerProperty property)
                    .BuildNode()
                .BuildDevice();

            Connect(device, mqttClient);

            var publishesBefore = mqttClient.PublishCount;
            var payloadsBefore = mqttClient.PayloadsFor(property.GetTopic()).Length;

            SendCommand(mqttClient, property, "9000");

            Assert.AreEqual(publishesBefore, mqttClient.PublishCount, "a rejected payload published something");
            Assert.AreEqual(payloadsBefore, mqttClient.PayloadsFor(property.GetTopic()).Length);
        }

        [TestMethod]
        public void HomieClient_Does_Not_Raise_OnCommand_For_A_Rejected_Payload()
        {
            // The decision recorded here: a payload the library refused is not handed to
            // the app. HandleIncomingMessage's standing rule -- "a command that failed to
            // apply is still a command the app should hear about" -- is about a command
            // that threw while being applied, typically from the reflection publish on a
            // flaky link. That command did reach the device. A payload that violates the
            // property's own $datatype or $format never did, and raising it would leave
            // every actuator re-checking the format its property already declares.
            var mqttClient = BuildClient();
            var builder = new HomieDeviceBuilder(_deviceTopicId, _deviceName);
            var device = builder.AddNode(_nodeTopicId, _nodeName, _nodeType)
                        .AddEnumProperty("enum-value", "Enum", "low")
                            .WithSettable(true)
                            .WithFormat("low,medium,high")
                        .BuildProperty(out EnumProperty property)
                    .BuildNode()
                .BuildDevice();

            var homieClient = new HomieClient(device, mqttClient);
            homieClient.Connect();

            var commandCount = 0;
            homieClient.OnCommand += (args) => commandCount++;

            SendCommand(mqttClient, property, "purple");
            Assert.AreEqual(0, commandCount, "the app was handed a payload the library refused");

            SendCommand(mqttClient, property, "medium");
            Assert.AreEqual(1, commandCount, "a valid command no longer reaches the app");
        }

        [TestMethod]
        public void Property_Set_Reports_Whether_The_Payload_Was_Applied()
        {
            // Set() is public, so a caller outside HomieClient can tell a refusal from a
            // success without inspecting the value it was trying to write.
            var builder = new HomieDeviceBuilder(_deviceTopicId, _deviceName);
            builder.AddNode(_nodeTopicId, _nodeName, _nodeType)
                        .AddIntegerProperty("integer-value", "Integer", 7)
                            .WithSettable(true)
                            .WithFormat("0:100")
                        .BuildProperty(out IntegerProperty property)
                    .BuildNode()
                .BuildDevice();

            Assert.IsTrue(property.Set(Encoding.UTF8.GetBytes("42")));
            Assert.AreEqual(42, property.Value);

            Assert.IsFalse(property.Set(Encoding.UTF8.GetBytes("9000")));
            Assert.AreEqual(42, property.Value);
        }

        private static MockMqttClient BuildClient() => new MockMqttClient();

        // Announce the device, so a command travels the same path it does in production:
        // the /set subscription, HandleIncomingMessage, then the property.
        private static void Connect(Device device, MockMqttClient mqttClient)
        {
            var homieClient = new HomieClient(device, mqttClient);
            homieClient.Connect();
        }

        private static void SendCommand(MockMqttClient mqttClient, PropertyBase property, string payload)
        {
            var topic = $"{property.GetTopic()}{Constants.TopicSeparator}{Constants.SetPropertyTopicId}";
            mqttClient.RaisePublishReceived(
                new MqttMsgPublishEventArgs(topic, Encoding.UTF8.GetBytes(payload), false, MqttQoSLevel.AtLeastOnce, false));
        }
    }
}
