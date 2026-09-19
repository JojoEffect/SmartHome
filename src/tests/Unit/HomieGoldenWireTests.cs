using SmartHome.DeviceModel;
using SmartHome.DeviceModel.Builder;
using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.Formats;
using SmartHome.Homie.V4;
using SmartHome.Text;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using nanoFramework.M2Mqtt.Messages;
using nanoFramework.TestFramework;

namespace SmartHome.UnitTests
{
    // The wire, pinned publish by publish.
    //
    // #110 rewrote SmartHome.Homie from a library that owned its own device model into an
    // adapter over the protocol-neutral one, under one contract: byte-identical output for
    // the devices in this tree. The evidence for that was a pair of hardware captures on
    // the commit before the rewrite -- HomieClientCheck and RoomSensor, both announcing
    // into a real Mosquitto -- and those captures are transcribed below, in order.
    //
    // Everything else in this suite asserts one property at a time and would stay green
    // through a reordering, a dropped attribute or a changed retain flag. This is the test
    // that would not, and it is the only one in CI that measures the whole announcement:
    // the real conformance run needs an ESP32, a network and a broker.
    //
    // The retain column is what the adapter passed, not what a broker would replay -- a
    // live MQTT stream carries the flag clear on every message, so the captures cannot
    // show it. Attributes go out retained at QoS 1; a property value goes out at QoS 1
    // with the retain flag the property itself declares, which is why 'counter' below is
    // the one line marked otherwise.
    [TestClass]
    public class HomieGoldenWireTests
    {
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
        public void HomieClientCheck_Announces_Exactly_What_The_Hardware_Capture_Shows()
        {
            // The conformance device: one property of every v4 datatype, settable and
            // not, retained and not. Deliberately not a real device -- it exists so the
            // announcement covers the whole convention rather than the corners RoomSensor
            // happens to use.
            var mqttClient = new MockMqttClient();

            new HomieClient(BuildHomieClientCheckDevice(), mqttClient).Connect();

            AssertWire(mqttClient, new Expected[]
            {
                Attr("homie/homie-client-check/$homie", "4"),
                Attr("homie/homie-client-check/$name", "Homie client check"),
                Attr("homie/homie-client-check/$nodes", "matrix"),
                Attr("homie/homie-client-check/$extensions", ""),
                Attr("homie/homie-client-check/$state", "init"),
                Attr("homie/homie-client-check/matrix/$name", "Datatype matrix"),
                Attr("homie/homie-client-check/matrix/$type", "conformance"),
                Attr("homie/homie-client-check/matrix/$properties", "integer-value,float-value,boolean-value,string-value,enum-value,color-value,counter,lifecycle"),
                Attr("homie/homie-client-check/matrix/integer-value/$name", "Integer"),
                Attr("homie/homie-client-check/matrix/integer-value/$datatype", "integer"),
                Attr("homie/homie-client-check/matrix/integer-value/$format", "0:100"),
                Attr("homie/homie-client-check/matrix/integer-value/$settable", "true"),
                Attr("homie/homie-client-check/matrix/integer-value/$retained", "true"),
                Attr("homie/homie-client-check/matrix/integer-value/$unit", "#"),
                Value("homie/homie-client-check/matrix/integer-value", "0", retained: true),
                Attr("homie/homie-client-check/matrix/float-value/$name", "Float"),
                Attr("homie/homie-client-check/matrix/float-value/$datatype", "float"),
                Attr("homie/homie-client-check/matrix/float-value/$format", ""),
                Attr("homie/homie-client-check/matrix/float-value/$settable", "true"),
                Attr("homie/homie-client-check/matrix/float-value/$retained", "true"),
                // The degree sign is U+00B0, and spelled by codepoint on both sides of
                // this comparison: visually identical characters exist, and a source file
                // that lost its encoding somewhere would otherwise fail here in a way
                // nobody could read.
                Attr("homie/homie-client-check/matrix/float-value/$unit", "\u00B0C"),
                Value("homie/homie-client-check/matrix/float-value", "0.00", retained: true),
                Attr("homie/homie-client-check/matrix/boolean-value/$name", "Boolean"),
                Attr("homie/homie-client-check/matrix/boolean-value/$datatype", "boolean"),
                Attr("homie/homie-client-check/matrix/boolean-value/$format", ""),
                Attr("homie/homie-client-check/matrix/boolean-value/$settable", "true"),
                Attr("homie/homie-client-check/matrix/boolean-value/$retained", "true"),
                Attr("homie/homie-client-check/matrix/boolean-value/$unit", ""),
                Value("homie/homie-client-check/matrix/boolean-value", "false", retained: true),
                Attr("homie/homie-client-check/matrix/string-value/$name", "String"),
                Attr("homie/homie-client-check/matrix/string-value/$datatype", "string"),
                Attr("homie/homie-client-check/matrix/string-value/$format", ""),
                Attr("homie/homie-client-check/matrix/string-value/$settable", "true"),
                Attr("homie/homie-client-check/matrix/string-value/$retained", "true"),
                Attr("homie/homie-client-check/matrix/string-value/$unit", ""),
                Value("homie/homie-client-check/matrix/string-value", "initial", retained: true),
                Attr("homie/homie-client-check/matrix/enum-value/$name", "Enum"),
                Attr("homie/homie-client-check/matrix/enum-value/$datatype", "enum"),
                Attr("homie/homie-client-check/matrix/enum-value/$format", "low,medium,high"),
                Attr("homie/homie-client-check/matrix/enum-value/$settable", "true"),
                Attr("homie/homie-client-check/matrix/enum-value/$retained", "true"),
                Attr("homie/homie-client-check/matrix/enum-value/$unit", ""),
                Value("homie/homie-client-check/matrix/enum-value", "low", retained: true),
                Attr("homie/homie-client-check/matrix/color-value/$name", "Colour"),
                Attr("homie/homie-client-check/matrix/color-value/$datatype", "color"),
                Attr("homie/homie-client-check/matrix/color-value/$format", "rgb"),
                Attr("homie/homie-client-check/matrix/color-value/$settable", "true"),
                Attr("homie/homie-client-check/matrix/color-value/$retained", "true"),
                Attr("homie/homie-client-check/matrix/color-value/$unit", ""),
                Value("homie/homie-client-check/matrix/color-value", "0,0,0", retained: true),
                Attr("homie/homie-client-check/matrix/counter/$name", "Counter"),
                Attr("homie/homie-client-check/matrix/counter/$datatype", "integer"),
                Attr("homie/homie-client-check/matrix/counter/$format", ""),
                Attr("homie/homie-client-check/matrix/counter/$settable", "false"),
                Attr("homie/homie-client-check/matrix/counter/$retained", "false"),
                Attr("homie/homie-client-check/matrix/counter/$unit", ""),
                // The one non-retained publish in either capture, and the reason the
                // property carries the flag rather than the publish settings: it proves
                // $retained is published as declared and honoured on the value beside it.
                Value("homie/homie-client-check/matrix/counter", "0", retained: false),
                Attr("homie/homie-client-check/matrix/lifecycle/$name", "Lifecycle control"),
                Attr("homie/homie-client-check/matrix/lifecycle/$datatype", "enum"),
                Attr("homie/homie-client-check/matrix/lifecycle/$format", "ready,alert,sleeping"),
                Attr("homie/homie-client-check/matrix/lifecycle/$settable", "true"),
                Attr("homie/homie-client-check/matrix/lifecycle/$retained", "true"),
                Attr("homie/homie-client-check/matrix/lifecycle/$unit", ""),
                Value("homie/homie-client-check/matrix/lifecycle", "ready", retained: true),
                Attr("homie/homie-client-check/$state", "ready"),
            });

            // Seven of the eight properties are settable, so seven command topics are
            // subscribed; 'counter' is the one that is not.
            Assert.AreEqual(7, mqttClient.SubscriptionCount);

            // The SUBSCRIBE packet, transcribed from the same hardware run as the
            // publishes above (baseline HomieClientCheck-01-broker.log: "Received
            // SUBSCRIBE from homie-client-check", then one topic per line at QoS 1).
            //
            // The ORDER is the settable table's Hashtable enumeration order, not
            // declaration order and not anything a broker cares about -- it is pinned
            // only so that a diff of a hardware run against that capture stays clean. A
            // failure on the order alone means the SUBSCRIBE block moved, not that
            // commands are broken.
            //
            // The QoS is the half with wire consequences: at QoS 0 the broker may drop a
            // /set, and a controller's command would disappear with nothing to see.
            var subscribed = mqttClient.SubscribedTopics;
            var expectedTopics = new string[]
            {
                "homie/homie-client-check/matrix/boolean-value/set",
                "homie/homie-client-check/matrix/color-value/set",
                "homie/homie-client-check/matrix/float-value/set",
                "homie/homie-client-check/matrix/integer-value/set",
                "homie/homie-client-check/matrix/lifecycle/set",
                "homie/homie-client-check/matrix/enum-value/set",
                "homie/homie-client-check/matrix/string-value/set",
            };

            Assert.AreEqual(expectedTopics.Length, subscribed.Length, $"subscribed to: {StringUtils.Join(", ", subscribed)}");
            for (int i = 0; i < expectedTopics.Length; i++)
            {
                Assert.AreEqual(expectedTopics[i], subscribed[i], $"SUBSCRIBE topic #{i} moved: {StringUtils.Join(", ", subscribed)}");
            }

            var qosLevels = mqttClient.SubscribedQosLevels;
            Assert.AreEqual(subscribed.Length, qosLevels.Length, "one QoS per topic, or M2Mqtt reads past the end of the array");
            for (int i = 0; i < qosLevels.Length; i++)
            {
                Assert.AreEqual((int)MqttQoSLevel.AtLeastOnce, (int)qosLevels[i], $"'{subscribed[i]}' was subscribed below QoS 1, so the broker may drop a command");
            }
        }

        [TestMethod]
        public void RoomSensor_Announces_Exactly_What_The_Hardware_Capture_Shows()
        {
            // The shipped device, which shares almost nothing with the conformance one:
            // three read-only floats, no settable property, no subscription at all. Both
            // shapes are pinned because the announcement differs in more than length --
            // a device with no settable property takes a different path through the
            // subscribe step.
            var mqttClient = new MockMqttClient();

            new HomieClient(BuildRoomSensorDevice(), mqttClient).Connect();

            AssertWire(mqttClient, new Expected[]
            {
                Attr("homie/room-sensor-office/$homie", "4"),
                Attr("homie/room-sensor-office/$name", "Raumsensor Buero"),
                Attr("homie/room-sensor-office/$nodes", "sensor"),
                Attr("homie/room-sensor-office/$extensions", ""),
                Attr("homie/room-sensor-office/$state", "init"),
                Attr("homie/room-sensor-office/sensor/$name", "Sensor"),
                Attr("homie/room-sensor-office/sensor/$type", "BMP280"),
                Attr("homie/room-sensor-office/sensor/$properties", "temperature,humidity,pressure"),
                Attr("homie/room-sensor-office/sensor/temperature/$name", "Temperatur"),
                Attr("homie/room-sensor-office/sensor/temperature/$datatype", "float"),
                Attr("homie/room-sensor-office/sensor/temperature/$format", ""),
                Attr("homie/room-sensor-office/sensor/temperature/$settable", "false"),
                Attr("homie/room-sensor-office/sensor/temperature/$retained", "true"),
                Attr("homie/room-sensor-office/sensor/temperature/$unit", "\u00B0C"),
                Value("homie/room-sensor-office/sensor/temperature", "0.00", retained: true),
                Attr("homie/room-sensor-office/sensor/humidity/$name", "Luftfeuchte"),
                Attr("homie/room-sensor-office/sensor/humidity/$datatype", "float"),
                Attr("homie/room-sensor-office/sensor/humidity/$format", ""),
                Attr("homie/room-sensor-office/sensor/humidity/$settable", "false"),
                Attr("homie/room-sensor-office/sensor/humidity/$retained", "true"),
                Attr("homie/room-sensor-office/sensor/humidity/$unit", "%"),
                Value("homie/room-sensor-office/sensor/humidity", "0.00", retained: true),
                Attr("homie/room-sensor-office/sensor/pressure/$name", "Luftdruck"),
                Attr("homie/room-sensor-office/sensor/pressure/$datatype", "float"),
                Attr("homie/room-sensor-office/sensor/pressure/$format", ""),
                Attr("homie/room-sensor-office/sensor/pressure/$settable", "false"),
                Attr("homie/room-sensor-office/sensor/pressure/$retained", "true"),
                // Pascals, not hectopascals: Pa is the unit the recommended list carries,
                // and $unit should say what the value actually is.
                Attr("homie/room-sensor-office/sensor/pressure/$unit", "Pa"),
                Value("homie/room-sensor-office/sensor/pressure", "0.00", retained: true),
                Attr("homie/room-sensor-office/$state", "ready"),
            });

            Assert.AreEqual(0, mqttClient.SubscriptionCount, "a device with no settable property must not subscribe");
        }

        // The shape of src\integrationTests\HomieClientCheck\Program.cs, rebuilt here.
        // Deliberately a copy rather than a reference: this suite must not depend on a
        // device app, and the point of the test is that THIS description produces THAT
        // capture -- a shared builder would let both move together and stay green.
        private static Device BuildHomieClientCheckDevice()
            => new DeviceBuilder("homie-client-check", "Homie client check")
                .AddNode("matrix", "Datatype matrix", "conformance")
                    .AddIntegerProperty("integer-value", "Integer", 0)
                        .WithSettable(true)
                        .WithFormat("0:100")
                        .WithUnit(Units.CountOrAmount)
                    .BuildProperty()
                    .AddFloatProperty("float-value", "Float", 0.0)
                        .WithSettable(true)
                        .WithUnit(Units.DegreeCelsius)
                    .BuildProperty()
                    .AddBooleanProperty("boolean-value", "Boolean", false)
                        .WithSettable(true)
                    .BuildProperty()
                    .AddStringProperty("string-value", "String", "initial")
                        .WithSettable(true)
                    .BuildProperty()
                    .AddEnumProperty("enum-value", "Enum", "low")
                        .WithSettable(true)
                        .WithFormat("low,medium,high")
                    .BuildProperty()
                    .AddColorProperty("color-value", "Colour", default)
                        .WithSettable(true)
                        .WithFormat(ColorFormats.Rgb)
                    .BuildProperty()
                    // Not settable and not retained: both defaults are the opposite, so
                    // this is the property that proves $settable and $retained are
                    // published as declared rather than assumed.
                    .AddIntegerProperty("counter", "Counter", 0)
                        .WithRetained(false)
                    .BuildProperty()
                    .AddEnumProperty("lifecycle", "Lifecycle control", HomieStates.Ready)
                        .WithSettable(true)
                        .WithOptions(new string[] { HomieStates.Ready, HomieStates.Alert, HomieStates.Sleeping })
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

        // The shape of src\devices\RoomSensor\Program.cs, rebuilt here for the same
        // reason.
        //
        // The quantity kinds are declared because that app declares them, and the
        // capture above was taken from a tree that did not. That is the assertion: v4
        // has nowhere to carry what a number means, so a description that states it
        // must go out byte for byte as one that does not.
        private static Device BuildRoomSensorDevice()
            => new DeviceBuilder("room-sensor-office", "Raumsensor Buero")
                .AddNode("sensor", "Sensor", "BMP280")
                    .AddFloatProperty("temperature", "Temperatur", 0.0)
                        .WithUnit(Units.DegreeCelsius)
                        .WithQuantityKind(QuantityKind.Temperature)
                    .BuildProperty()
                    .AddFloatProperty("humidity", "Luftfeuchte", 0.0)
                        .WithUnit(Units.Percent)
                        .WithQuantityKind(QuantityKind.Humidity)
                    .BuildProperty()
                    .AddFloatProperty("pressure", "Luftdruck", 0.0)
                        .WithUnit(Units.Pascal)
                        .WithQuantityKind(QuantityKind.Pressure)
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

        private static void AssertWire(MockMqttClient mqttClient, Expected[] expected)
        {
            var actual = mqttClient.Publishes;

            // Compared position by position before the lengths, so a diff names the first
            // publish that moved rather than only the count that changed.
            var shared = expected.Length < actual.Length ? expected.Length : actual.Length;
            for (int i = 0; i < shared; i++)
            {
                Assert.AreEqual(expected[i].Topic, actual[i].Topic, $"publish #{i}: wrong topic");
                Assert.AreEqual(expected[i].Payload, actual[i].Payload, $"publish #{i} ('{expected[i].Topic}'): wrong payload");
                Assert.AreEqual(expected[i].Retain, actual[i].Retain, $"publish #{i} ('{expected[i].Topic}'): wrong retain flag");

                // Every announce publish is QoS 1. QoS 0 would let an attribute go
                // missing on a lossy link with nothing to say so, which for $nodes or
                // $state is the difference between a device a controller can read and one
                // it cannot.
                Assert.AreEqual(
                    (int)MqttQoSLevel.AtLeastOnce,
                    (int)actual[i].QosLevel,
                    $"publish #{i} ('{expected[i].Topic}'): wrong QoS");
            }

            Assert.AreEqual(expected.Length, actual.Length, "the announcement is not the length the capture shows");
        }

        private static Expected Attr(string topic, string payload) => new Expected(topic, payload, true);

        private static Expected Value(string topic, string payload, bool retained) => new Expected(topic, payload, retained);

        private class Expected
        {
            internal Expected(string topic, string payload, bool retain)
            {
                Topic = topic;
                Payload = payload;
                Retain = retain;
            }

            public string Topic { get; }

            public string Payload { get; }

            public bool Retain { get; }
        }
    }
}
