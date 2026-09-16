using SmartHome.DeviceModel;
using SmartHome.DeviceModel.Builder;
using SmartHome.DeviceModel.Formats;
using SmartHome.DeviceModel.Properties;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using nanoFramework.TestFramework;
using System;
using System.Text;

namespace SmartHome.UnitTests
{
    // The guards a code review of the model turned up missing, one case each. They have
    // a theme: every one of them is a rule the model already enforced on one path and
    // not on its sibling -- Set validated where SetTarget did not, Device refused a
    // duplicate id where Node did not, TryParse refused an inverted range where the
    // typed factories did not. So these are less about the individual inputs below than
    // about the pairs staying in step.
    //
    // Two of the eleven fixes have no case here, deliberately: collapsing the nine
    // Update() methods' eager PropertyUpdateEventArgs into the ?.Invoke argument, and
    // replacing two quadratic string joins with a StringBuilder, are both allocation
    // changes with identical observable behaviour. The join's *output* is pinned below
    // anyway, since that is what an adapter publishes.
    [TestClass]
    public class DeviceModelGuardTests
    {
        private const string _deviceId = "super-car";
        private const string _deviceName = "Super car";
        private const string _nodeId = "engine";
        private const string _nodeName = "Car engine";
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
        public void DurationProperty_Refuses_A_Duration_It_Cannot_Represent()
        {
            // The parser used to cap each component's digits at 1e9 and call that an
            // overflow guard. It bounds the accumulation, not the result: 1e9 hours is
            // 3.6e12 seconds, and TimeSpan.FromSeconds multiplies that by TicksPerSecond
            // unchecked, wrapping Int64 to a negative Ticks. Validate() saw a successful
            // parse and accepted the payload; SetInternal then threw out of Set() on the
            // transport's dispatch thread, where a throw takes down every later delivery.
            var property = BuildDuration();

            Assert.IsFalse(property.Set(Bytes("PT1000000000H")), "a duration past TimeSpan's range has no representation, so it is refused rather than wrapped");
            Assert.IsFalse(property.Set(Bytes("PT999999999999S")));
            Assert.AreEqual("PT0S", Payload(property), "a refused payload must not move the value");

            // The bound is on what TimeSpan can carry, not on what looks big.
            Assert.IsTrue(property.Set(Bytes("PT100000H")));
        }

        [TestMethod]
        public void FloatProperty_Refuses_A_Magnitude_It_Cannot_Render()
        {
            // nf-interpreter's Format_F prints into a fixed 128-byte buffer, and its
            // printf fork returns what it wrote rather than what it needed -- so a value
            // too wide to render is not an error, it is silently cut off mid-digit and
            // published as a different number.
            var property = BuildFloat();

            Assert.IsFalse(property.Set(Bytes(new string('9', 120))), "a magnitude whose fixed-decimal form does not fit the format buffer would publish truncated");
            Assert.AreEqual("0.00", Payload(property));

            Assert.ThrowsException(typeof(ArgumentException), () => property.Update(1e200));
        }

        [TestMethod]
        public void Property_Set_Refuses_A_Null_Payload_Instead_Of_Throwing()
        {
            // Set() dereferenced value.Length before any guard. It runs on the dispatch
            // thread, so this has to be a refusal like any other, not a throw.
            var property = BuildFloat();
            byte[]? missing = null;

            Assert.IsFalse(property.Set(missing!));
            Assert.AreEqual("0.00", Payload(property));
        }

        [TestMethod]
        public void Property_Refuses_An_Initial_Value_Its_Own_Declaration_Forbids()
        {
            // A constructor argument reaches the wire the same way a controller's payload
            // does: GetPayload() announces it into the retained store before anything has
            // been Set. Holding it to the same rule is what stops a property advertising
            // a value its own Set() refuses.
            var integer = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddIntegerProperty("speed", "Speed", 500)
                        .WithRange(NumericRange.Between(0, 100));

            Assert.ThrowsException(typeof(ArgumentException), () => integer.BuildProperty());

            var real = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("temperature", "Temperature", 500.0)
                        .WithRange(NumericRange.Between(0, 100));

            Assert.ThrowsException(typeof(ArgumentException), () => real.BuildProperty());

            var choice = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddEnumProperty("mode", "Mode", "banana")
                        .WithFormat("low,medium,high");

            Assert.ThrowsException(typeof(ArgumentException), () => choice.BuildProperty());

            var json = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddJsonProperty("reading", "Reading", "42");

            Assert.ThrowsException(typeof(ArgumentException), () => json.BuildProperty());
        }

        [TestMethod]
        public void SetTarget_Refuses_What_Set_Would_Refuse()
        {
            // $target says what the property is heading for, so a target it would refuse
            // as a value is a promise it cannot keep -- and an adapter publishing from
            // here would advertise, to every controller, a number Set() rejects.
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddIntegerProperty("speed", "Speed", 0)
                        .WithSettable(true)
                        .WithFormat("0:100")
                    .BuildProperty(out IntegerProperty property)
                .BuildNode()
                .BuildDevice();

            property.SetTarget(50);
            Assert.AreEqual("50", property.Target);

            Assert.ThrowsException(typeof(ArgumentException), () => property.SetTarget(9000));
            Assert.AreEqual("50", property.Target, "a refused target must not replace the one already declared");
        }

        [TestMethod]
        public void NumericRange_Factories_Refuse_What_TryParse_Refuses()
        {
            // The text route rejected an inverted range and a non-finite bound; the typed
            // route built them happily. Between(30, 5) contains nothing at all, so a
            // property declaring it refuses every payload for the life of the device --
            // and a NaN bound is worse, because every comparison against NaN is false, so
            // it reads as a declared bound while enforcing nothing.
            Assert.ThrowsException(typeof(ArgumentException), () => NumericRange.Between(30, 5));
            Assert.ThrowsException(typeof(ArgumentException), () => NumericRange.Between(double.NaN, 100));
            Assert.ThrowsException(typeof(ArgumentException), () => NumericRange.Between(0, double.NaN));
            Assert.ThrowsException(typeof(ArgumentException), () => NumericRange.AtLeast(double.PositiveInfinity));
            Assert.ThrowsException(typeof(ArgumentException), () => NumericRange.AtMost(double.NaN));

            // The text route still answers the same way it always did: false, not a throw.
            Assert.IsFalse(NumericRange.TryParse("30:5", out _));

            var range = NumericRange.Between(0, 100);
            Assert.IsTrue(range.Contains(0));
            Assert.IsTrue(range.Contains(100));
        }

        [TestMethod]
        public void Node_Refuses_Two_Properties_With_The_Same_Id()
        {
            // Device.AddNodes holds this guard one level up. Without it here, two
            // entities share an id: an adapter announces it twice, subscribes one command
            // topic twice, and a single /set lands on both while only one is published
            // from.
            var nodeBuilder = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType);

            nodeBuilder.AddFloatProperty("temperature", "Temperature", 21.5).BuildProperty();
            nodeBuilder.AddFloatProperty("temperature", "Temperature again", 0).BuildProperty();

            Assert.ThrowsException(typeof(ArgumentException), () => nodeBuilder.BuildNode());
        }

        [TestMethod]
        public void Builder_Refuses_To_Build_Twice()
        {
            // The children are handed to the entity rather than copied into it, and
            // AddNodes/AddProperties re-parent each one. A second build would attach the
            // same instances to a second entity and silently repoint the first one's
            // children at it -- and since the model deliberately has no GetTopic(), an
            // adapter names everything by walking Parent, so the first device's whole
            // tree would go out under the second device's id.
            var deviceBuilder = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("temperature", "Temperature", 21.5)
                    .BuildProperty()
                .BuildNode();

            var device = deviceBuilder.BuildDevice();
            Assert.IsNotNull(device);
            Assert.ThrowsException(typeof(InvalidOperationException), () => deviceBuilder.BuildDevice());

            var nodeBuilder = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType);

            nodeBuilder.AddFloatProperty("temperature", "Temperature", 21.5).BuildProperty();
            nodeBuilder.BuildNode();

            Assert.ThrowsException(typeof(InvalidOperationException), () => nodeBuilder.BuildNode());
        }

        [TestMethod]
        public void Node_Hands_Out_A_Copy_Of_Its_Properties()
        {
            // Device.Nodes, Device.Alerts, EnumOptions.Values and ColorFormats.Values all
            // copy on read so a caller cannot rewrite a tree the device may already have
            // announced. Node.Properties was the one place that handed out the live array.
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("temperature", "Temperature", 21.5)
                    .BuildProperty()
                .BuildNode(out Node node)
                .BuildDevice();

            var properties = node.Properties;
            Assert.AreEqual(1, properties.Length);

            properties[0] = null!;

            Assert.IsNotNull(node.Properties[0], "the node's own array must not be reachable through the one it handed out");
        }

        [TestMethod]
        public void Formats_Render_Their_Declaration_Comma_Separated()
        {
            // The rendering an adapter publishes as the declared format. Pinned because the joins
            // behind it were rewritten from string accumulation to a StringBuilder, and
            // that had to be output-identical.
            Assert.AreEqual("low,medium,high", new EnumOptions(new string[] { "low", "medium", "high" }).ToString());
            Assert.AreEqual("low", new EnumOptions(new string[] { "low" }).ToString());
            Assert.AreEqual("rgb,hsv", new ColorFormats(new string[] { "rgb", "hsv" }).ToString());
            Assert.AreEqual("rgb", new ColorFormats(new string[] { "rgb" }).ToString());
        }

        private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

        private static string Payload(PropertyBase property)
        {
            var bytes = property.GetPayload();
            return Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }

        private static FloatProperty BuildFloat()
        {
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("temperature", "Temperature", 0)
                        .WithSettable(true)
                    .BuildProperty(out FloatProperty property)
                .BuildNode()
                .BuildDevice();

            return property;
        }

        private static DurationProperty BuildDuration()
        {
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddDurationProperty("uptime", "Uptime")
                        .WithSettable(true)
                    .BuildProperty(out DurationProperty property)
                .BuildNode()
                .BuildDevice();

            return property;
        }
    }
}
