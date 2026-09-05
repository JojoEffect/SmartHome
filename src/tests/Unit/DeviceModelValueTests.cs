using SmartHome.DeviceModel;
using SmartHome.DeviceModel.Builder;
using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.Formats;
using SmartHome.DeviceModel.Properties;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using nanoFramework.TestFramework;
using System;
using System.Text;

namespace SmartHome.UnitTests
{
    // Every case here asserts one of two things: what a property publishes, and that a
    // payload its own datatype or format forbids does not move it. The second is the
    // whole contract of Set() -- there is no "command refused" channel in any of these
    // conventions, so a value that did not change is all a controller gets.
    [TestClass]
    public class DeviceModelValueTests
    {
        private const string _deviceId = "super-car";
        private const string _deviceName = "Super car";
        private const string _nodeId = "matrix";
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
        public void FloatProperty_Publishes_Fixed_Decimals_Not_ToString()
        {
            // nanoFramework's double.ToString() uses "G" and renders 21.5 as
            // 21.499999999999999. This is the reason the type formats explicitly, and it
            // is value-dependent -- 0.1 comes out as "0.1" -- which is what let the
            // defect survive so long.
            var property = BuildFloat(21.5);

            Assert.AreEqual("21.50", Payload(property));

            property.Update(0.1);
            Assert.AreEqual("0.10", Payload(property));

            property.Update(-3.456);
            Assert.AreEqual("-3.46", Payload(property));
        }

        [TestMethod]
        public void FloatProperty_Publishes_The_Decimals_It_Was_Given()
        {
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("humidity", "Humidity", 41.6666)
                        .WithDecimals(0)
                        .WithUnit(Units.Percent)
                        .WithQuantityKind(QuantityKind.Humidity)
                    .BuildProperty(out FloatProperty property)
                .BuildNode()
                .BuildDevice();

            Assert.IsNotNull(device);
            Assert.AreEqual("42", Payload(property));
            Assert.AreEqual(Units.Percent, property.Unit);
            Assert.AreEqual((int)QuantityKind.Humidity, (int)property.QuantityKind);
        }

        [TestMethod]
        public void FloatProperty_Refuses_A_Value_With_No_Representation()
        {
            var property = BuildFloat(0);

            // Rejected at the boundary rather than published: double.ToString returns
            // "NaN" before it consults a format string, and no controller -- nor this
            // device's own parser -- can read that back.
            Assert.ThrowsException(typeof(ArgumentException), () => property.Update(double.NaN));
            Assert.ThrowsException(typeof(ArgumentException), () => property.Update(double.PositiveInfinity));
        }

        [TestMethod]
        public void FloatProperty_Refuses_A_Payload_Its_Range_Forbids()
        {
            var property = BuildFloat(0, "-10:10.5");

            Assert.IsTrue(property.Set(Bytes("10.5")), "the upper bound is a value, not a violation");
            Assert.AreEqual(10.5, property.Value);

            Assert.IsFalse(property.Set(Bytes("10.6")));
            Assert.AreEqual(10.5, property.Value, "a refused payload moved the property");

            Assert.IsFalse(property.Set(Bytes("warm")));
            Assert.AreEqual(10.5, property.Value);

            // A controller must not be able to reach Update()'s throw with a payload:
            // Set() runs on the transport's dispatch thread.
            Assert.IsFalse(property.Set(Bytes("NaN")));
            Assert.AreEqual(10.5, property.Value);
        }

        [TestMethod]
        public void IntegerProperty_Refuses_A_Payload_Outside_Its_Declared_Range()
        {
            var property = BuildInteger(7, "0:100");

            // Both ends are inclusive, so the bounds themselves are commands, not
            // violations -- a range check that is off by one at the edge is the usual way
            // this kind of validation goes wrong.
            Assert.IsTrue(property.Set(Bytes("0")));
            Assert.AreEqual(0, property.Value);

            Assert.IsTrue(property.Set(Bytes("100")));
            Assert.AreEqual(100, property.Value);

            Assert.IsFalse(property.Set(Bytes("101")));
            Assert.AreEqual(100, property.Value, "a value above the declared maximum moved the property");

            Assert.IsFalse(property.Set(Bytes("-1")));
            Assert.AreEqual(100, property.Value, "a value below the declared minimum moved the property");

            Assert.IsFalse(property.Set(Bytes("twelve")));
            Assert.AreEqual(100, property.Value);
        }

        [TestMethod]
        public void IntegerProperty_With_A_Malformed_Format_Enforces_Nothing()
        {
            // A device author's malformed declaration is not a reason to start refusing a
            // controller's otherwise valid payloads.
            var property = BuildInteger(0, "30:5");

            Assert.IsNull(property.Range);
            Assert.IsTrue(property.Set(Bytes("9000")));
            Assert.AreEqual(9000, property.Value);
        }

        [TestMethod]
        public void BooleanProperty_Publishes_And_Accepts_Only_The_Two_Literals()
        {
            var property = BuildBoolean(false);

            // Announced as "false", not bool.ToString()'s "False", which is a payload the
            // datatype does not permit -- and which used to be published retained.
            Assert.AreEqual("false", Payload(property));

            Assert.IsTrue(property.Set(Bytes("true")));
            Assert.IsTrue(property.Value);
            Assert.AreEqual("true", Payload(property));

            // "1", "True" and "on" used to be accepted or -- worse -- turned into false,
            // which at the broker is indistinguishable from a controller having
            // deliberately asked for false.
            Assert.IsFalse(property.Set(Bytes("True")));
            Assert.IsFalse(property.Set(Bytes("1")));
            Assert.IsFalse(property.Set(Bytes("on")));
            Assert.IsTrue(property.Value, "a refused payload moved the property");
        }

        [TestMethod]
        public void BooleanProperty_Labels_Describe_The_Values_Without_Defining_Them()
        {
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddBooleanProperty("valve", "Valve", false)
                        .WithSettable(true)
                        .WithLabels("closed", "open")
                    .BuildProperty(out BooleanProperty property)
                .BuildNode()
                .BuildDevice();

            Assert.IsNotNull(device);
            Assert.IsNotNull(property.Labels);
            Assert.AreEqual("open", property.Labels!.True);

            // The labels name the values for a human; they are not the payloads.
            Assert.IsFalse(property.Set(Bytes("open")));
            Assert.IsTrue(property.Set(Bytes("true")));
            Assert.AreEqual("true", Payload(property));
        }

        [TestMethod]
        public void EnumProperty_Refuses_A_Value_Its_Options_Do_Not_Declare()
        {
            var property = BuildEnum("low", "low, medium, high");

            Assert.IsTrue(property.Set(Bytes("medium")));
            Assert.AreEqual("medium", property.Value);

            Assert.IsFalse(property.Set(Bytes("purple")));
            Assert.AreEqual("medium", property.Value);

            // The options were trimmed once, when they were declared; the payload is not
            // trimmed, because leading whitespace makes it a different value.
            Assert.IsFalse(property.Set(Bytes(" high")));
            Assert.AreEqual("medium", property.Value);
        }

        [TestMethod]
        public void EnumProperty_Without_Options_Accepts_Anything()
        {
            var property = BuildEnum("low");

            Assert.IsNull(property.Options);
            Assert.IsTrue(property.Set(Bytes("purple")));
            Assert.AreEqual("purple", property.Value);
        }

        [TestMethod]
        public void StringProperty_Accepts_Any_Payload_Including_An_Empty_One()
        {
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddStringProperty("message", "Message", "hello")
                        .WithSettable(true)
                    .BuildProperty(out StringProperty property)
                .BuildNode()
                .BuildDevice();

            Assert.IsNotNull(device);
            Assert.IsTrue(property.Set(Bytes("anything at all, 42, {}")));
            Assert.AreEqual("anything at all, 42, {}", property.Value);

            Assert.IsTrue(property.Set(Bytes(string.Empty)));
            Assert.AreEqual(string.Empty, property.Value);
        }

        [TestMethod]
        public void ColorProperty_Publishes_And_Reads_A_Decimal_Triple()
        {
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddColorProperty("tint", "Tint", default)
                        .WithSettable(true)
                        .WithFormat(ColorFormats.Rgb)
                    .BuildProperty(out ColorProperty property)
                .BuildNode()
                .BuildDevice();

            Assert.IsNotNull(device);
            Assert.AreEqual("0,0,0", Payload(property));

            Assert.IsTrue(property.Set(Bytes("255,128,0")));
            Assert.AreEqual(255, (int)property.Value.R);
            Assert.AreEqual(128, (int)property.Value.G);
            Assert.AreEqual("255,128,0", Payload(property));

            Assert.IsFalse(property.Set(Bytes("FF8000")), "hex is not a colour payload");
            Assert.IsFalse(property.Set(Bytes("256,0,0")), "a component above 255");
            Assert.IsFalse(property.Set(Bytes("1,2")), "a triple has three components");
            Assert.AreEqual("255,128,0", Payload(property));
        }

        [TestMethod]
        public void DateTimeProperty_Round_Trips_Iso8601_In_Utc()
        {
            var property = BuildDateTime();

            property.Update(new DateTime(2026, 9, 5, 14, 30, 5));
            Assert.AreEqual("2026-09-05T14:30:05Z", Payload(property), "zero-padded, and always UTC");

            Assert.IsTrue(property.Set(Bytes("2026-01-02T03:04:05Z")));
            Assert.AreEqual(2026, property.Value.Year);
            Assert.AreEqual(1, property.Value.Month);
            Assert.AreEqual(3, property.Value.Hour);

            // A zone is converted rather than dropped: dropping it would be wrong by
            // exactly the offset, silently.
            Assert.IsTrue(property.Set(Bytes("2026-01-02T03:04:05+02:00")));
            Assert.AreEqual(1, property.Value.Hour);

            // Sub-second precision is accepted and truncated, which is the same thing the
            // rendering does.
            Assert.IsTrue(property.Set(Bytes("2026-01-02T03:04:05.250Z")));
            Assert.AreEqual(5, property.Value.Second);
        }

        [TestMethod]
        public void DateTimeProperty_Refuses_Anything_That_Is_Not_An_Instant()
        {
            var property = BuildDateTime();
            property.Update(new DateTime(2026, 9, 5, 14, 30, 5));

            Assert.IsFalse(property.Set(Bytes("2026-09-05")), "a date alone is not an instant here");
            Assert.IsFalse(property.Set(Bytes("05/09/2026 14:30:05")));
            Assert.IsFalse(property.Set(Bytes("2026-13-05T14:30:05Z")), "there is no month 13");
            Assert.IsFalse(property.Set(Bytes("2026-02-30T14:30:05Z")), "February has no 30th");
            Assert.IsFalse(property.Set(Bytes("2026-09-05T25:30:05Z")), "there is no hour 25");
            Assert.IsFalse(property.Set(Bytes("2026-09-05T14:30:05Z ")), "trailing rubbish");
            Assert.IsFalse(property.Set(Bytes(string.Empty)));

            Assert.AreEqual(2026, property.Value.Year);
            Assert.AreEqual(9, property.Value.Month);
            Assert.AreEqual(5, property.Value.Day);
        }

        [TestMethod]
        public void DateTimeProperty_Accepts_A_Leap_Day_In_A_Leap_Year_Only()
        {
            var property = BuildDateTime();

            Assert.IsTrue(property.Set(Bytes("2024-02-29T00:00:00Z")));
            Assert.IsFalse(property.Set(Bytes("2026-02-29T00:00:00Z")));
        }

        [TestMethod]
        public void DurationProperty_Round_Trips_The_Iso8601_Duration_Form()
        {
            var property = BuildDuration();

            Assert.AreEqual("PT0S", Payload(property), "zero has to render as something; a bare PT is not a duration");

            property.Update(TimeSpan.FromSeconds(43546));
            Assert.AreEqual("PT12H5M46S", Payload(property));

            property.Update(TimeSpan.FromSeconds(300));
            Assert.AreEqual("PT5M", Payload(property), "components that are zero are omitted");

            // There is no day component in the form the convention defines, so two days
            // is forty-eight hours.
            property.Update(TimeSpan.FromDays(2));
            Assert.AreEqual("PT48H", Payload(property));

            Assert.IsTrue(property.Set(Bytes("PT12H5M46S")));
            Assert.AreEqual(43546L, property.Value.Ticks / TimeSpan.TicksPerSecond);

            Assert.IsTrue(property.Set(Bytes("PT5M")));
            Assert.AreEqual(300L, property.Value.Ticks / TimeSpan.TicksPerSecond);

            Assert.IsTrue(property.Set(Bytes("PT1H30S")));
            Assert.AreEqual(3630L, property.Value.Ticks / TimeSpan.TicksPerSecond);
        }

        [TestMethod]
        public void DurationProperty_Refuses_Anything_That_Is_Not_That_Form()
        {
            var property = BuildDuration();
            property.Update(TimeSpan.FromSeconds(60));

            Assert.IsFalse(property.Set(Bytes("60")), "a bare number is not a duration");
            Assert.IsFalse(property.Set(Bytes("P1D")), "there is no day component");
            Assert.IsFalse(property.Set(Bytes("PT")), "no component at all");
            Assert.IsFalse(property.Set(Bytes("PT5X")));
            Assert.IsFalse(property.Set(Bytes("PT1M1H")), "components must be in order");
            Assert.IsFalse(property.Set(Bytes("PT1.5S")), "the rendering never produces a fraction, so reading one back would change it");
            Assert.IsFalse(property.Set(Bytes("PT1H1H")), "a repeated component");

            Assert.AreEqual(60L, property.Value.Ticks / TimeSpan.TicksPerSecond, "a refused payload moved the property");
        }

        [TestMethod]
        public void DurationProperty_Refuses_A_Negative_Duration()
        {
            // The PTxHxMxS form has no way to say "negative", so a negative TimeSpan has
            // no representation at all.
            var property = BuildDuration();

            Assert.ThrowsException(typeof(ArgumentException), () => property.Update(TimeSpan.FromSeconds(-1)));
        }

        [TestMethod]
        public void JsonProperty_Accepts_An_Object_Or_An_Array_And_Nothing_Else()
        {
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddJsonProperty("reading", "Reading", "{}")
                        .WithSettable(true)
                        .WithSchema("{\"type\":\"object\"}")
                    .BuildProperty(out JsonProperty property)
                .BuildNode()
                .BuildDevice();

            Assert.IsNotNull(device);
            Assert.AreEqual("{\"type\":\"object\"}", property.Schema!);

            Assert.IsTrue(property.Set(Bytes("{\"t\":21.5}")));
            Assert.IsTrue(property.Set(Bytes(" [1, 2, 3] ")), "surrounding whitespace is not part of the value");

            // The convention is specific that the payload must be an array or an object;
            // a scalar is a case for one of the ordinary datatypes.
            Assert.IsFalse(property.Set(Bytes("42")));
            Assert.IsFalse(property.Set(Bytes("\"text\"")));
            Assert.IsFalse(property.Set(Bytes("true")));
            Assert.IsFalse(property.Set(Bytes("{")));
            Assert.IsFalse(property.Set(Bytes(string.Empty)));
        }

        [TestMethod]
        public void Property_Set_Reports_Whether_The_Payload_Was_Applied()
        {
            // Set() is public, so a caller can tell a refusal from a success without
            // inspecting the value it was trying to write.
            var property = BuildInteger(7, "0:100");

            Assert.IsTrue(property.Set(Bytes("42")));
            Assert.AreEqual(42, property.Value);

            Assert.IsFalse(property.Set(Bytes("9000")));
            Assert.AreEqual(42, property.Value);
        }

        [TestMethod]
        public void Property_Set_Refuses_To_Touch_A_Property_That_Is_Not_Settable()
        {
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddIntegerProperty("speed", "Speed", 7)
                    .BuildProperty(out IntegerProperty property)
                .BuildNode()
                .BuildDevice();

            Assert.IsNotNull(device);
            Assert.ThrowsException(typeof(InvalidOperationException), () => property.Set(Bytes("42")));
        }

        [TestMethod]
        public void Property_Update_Announces_The_Payload_It_Will_Publish()
        {
            // An adapter publishes from these bytes rather than re-deriving them from the
            // typed value, so that two publishers of the same reading cannot disagree
            // about how it is rendered.
            var property = BuildFloat(0);

            string? announced = null;
            property.OnUpdate += (args) => announced = Encoding.UTF8.GetString(args.Value, 0, args.Value.Length);

            property.Update(21.5);

            Assert.AreEqual("21.50", announced);
        }

        [TestMethod]
        public void Property_Target_Is_Declared_And_Cleared_In_The_Values_Own_Encoding()
        {
            var property = BuildFloat(0);

            Assert.IsNull(property.Target, "nothing is in flight until the device says so");

            string? announced = null;
            var announcements = 0;
            property.OnTargetUpdate += (args) =>
            {
                announcements++;
                announced = Encoding.UTF8.GetString(args.Value, 0, args.Value.Length);
            };

            property.SetTarget(21.5);
            Assert.AreEqual("21.50", property.Target, "a target is rendered exactly as the value would be");
            Assert.AreEqual("21.50", announced);
            Assert.AreEqual(0.0, property.Value, "declaring a target must not move the value");

            // A zero-length payload is how a retained topic is deleted, which is what
            // "there is no target any more" has to mean to a consumer that saw the last one.
            property.ClearTarget();
            Assert.IsNull(property.Target);
            Assert.AreEqual(string.Empty, announced);
            Assert.AreEqual(2, announcements);

            property.ClearTarget();
            Assert.AreEqual(2, announcements, "clearing a target that is not set must not be announced");
        }

        private static byte[] Bytes(string payload) => Encoding.UTF8.GetBytes(payload);

        private static string Payload(PropertyBase property)
        {
            var bytes = property.GetPayload();
            return Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }

        private static FloatProperty BuildFloat(double initialValue, string format = "")
        {
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("temperature", "Temperature", initialValue)
                        .WithSettable(true)
                        .WithFormat(format)
                    .BuildProperty(out FloatProperty property)
                .BuildNode()
                .BuildDevice();

            return property;
        }

        private static IntegerProperty BuildInteger(int initialValue, string format = "")
        {
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddIntegerProperty("speed", "Speed", initialValue)
                        .WithSettable(true)
                        .WithFormat(format)
                    .BuildProperty(out IntegerProperty property)
                .BuildNode()
                .BuildDevice();

            return property;
        }

        private static BooleanProperty BuildBoolean(bool initialValue)
        {
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddBooleanProperty("running", "Running", initialValue)
                        .WithSettable(true)
                    .BuildProperty(out BooleanProperty property)
                .BuildNode()
                .BuildDevice();

            return property;
        }

        private static EnumProperty BuildEnum(string initialValue, string format = "")
        {
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddEnumProperty("mode", "Mode", initialValue)
                        .WithSettable(true)
                        .WithFormat(format)
                    .BuildProperty(out EnumProperty property)
                .BuildNode()
                .BuildDevice();

            return property;
        }

        private static DateTimeProperty BuildDateTime()
        {
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddDateTimeProperty("measured-at", "Measured at")
                        .WithSettable(true)
                    .BuildProperty(out DateTimeProperty property)
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
