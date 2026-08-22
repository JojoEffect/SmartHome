using SmartHome.Homie.V4;
using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.Builder;
using SmartHome.Homie.V4.Properties;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using nanoFramework.M2Mqtt.Messages;
using nanoFramework.TestFramework;
using System;
using System.Text;

namespace SmartHome.UnitTests
{
    [TestClass]
    public class HomieClientTests
    {
        private const string _testDeviceTopicId = "super-car";
        private const string _testDeviceName = "Super car";

        // Only what the tests actually use. Fourteen further constants (wheels, lights,
        // angle, speed, direction, colour) were declaration-only, describing a fixture
        // no test in this file builds -- the first thing a reader had to disprove.
        private const string _testNodeEngineTopicId = "engine";
        private const string _testNodeEngineName = "Engine";
        private const string _testNodeEngineType = "V8";

        private const string _testPropertyTemperatureTopicId = "temperature";
        private const string _testPropertyTemperatureName = "Temperature";

        private const string _testPropertyIntensityTopicId = "intensity";
        private const string _testPropertyIntensityName = "Intensity";

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
        public void HomieClient_Publish_On_Property_Update()
        {
            // Arrange
            int expectedPublishCount = 17;   // +1: $extensions is published now
            int expectedSubscriptionCount = 0;

            var mqttClient = new MockMqttClient();

            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var device = builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyTemperatureTopicId, _testPropertyTemperatureName, 0.0)
                        .BuildProperty(out FloatProperty property)
                    .BuildNode()
                .BuildDevice();


            // Act
            var homieClient = new HomieClient(device, mqttClient);

            homieClient.Connect();

            property.Update(10);

            // Assert
            Assert.AreEqual(expectedPublishCount, mqttClient.PublishCount);
            Assert.AreEqual(expectedSubscriptionCount, mqttClient.SubscriptionCount);
        }

        [TestMethod]
        public void HomieClient_Property_Is_Set_On_Property_Set_Message()
        {
            // Arrange
            int expectedPublishCount = 17;   // +1: $extensions is published now
            int expectedSubscriptionCount = 1;
            double initialValue = 0.0;
            double expectedValue = 25;

            var mqttClient = new MockMqttClient();

            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var device = builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyTemperatureTopicId, _testPropertyTemperatureName, initialValue)
                            .WithSettable(true)
                        .BuildProperty(out FloatProperty property)
                    .BuildNode()
                .BuildDevice();

            // Assert
            Assert.AreEqual(initialValue, property.Value);

            // Act
            var homieClient = new HomieClient(device, mqttClient);

            homieClient.Connect();
            var commandTopic = $"{property.GetTopic()}{Constants.TopicSeparator}{Constants.SetPropertyTopicId}";
            mqttClient.RaisePublishReceived(new MqttMsgPublishEventArgs(commandTopic, Encoding.UTF8.GetBytes(expectedValue.ToString()), false, MqttQoSLevel.AtLeastOnce, false));

            // Assert
            Assert.AreEqual(expectedValue, property.Value);
            Assert.AreEqual(expectedPublishCount, mqttClient.PublishCount);
            Assert.AreEqual(expectedSubscriptionCount, mqttClient.SubscriptionCount);
        }

        [TestMethod]
        public void HomieClient_Connect_Declares_HomieLastWill()
        {
            // Homie v4 requires the connection to carry a last will setting
            // homie/[device-id]/$state to 'lost', retained. A will can only be declared
            // in CONNECT, so the Homie client has to own the session -- RoomSensor used
            // to connect the transport itself first, which silently produced a session
            // with no will at all.

            // Arrange
            var mqttClient = new MockMqttClient();

            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var device = builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyTemperatureTopicId, _testPropertyTemperatureName, 0.0)
                        .BuildProperty()
                    .BuildNode()
                .BuildDevice();

            var homieClient = new HomieClient(device, mqttClient);

            // Act
            var connected = homieClient.Connect();

            // Assert
            Assert.IsTrue(connected);
            Assert.IsTrue(mqttClient.WillFlag);
            Assert.AreEqual($"{Constants.RootTopicId}{Constants.TopicSeparator}{_testDeviceTopicId}{Constants.TopicSeparator}{Constants.StateAttributeTopicId}", mqttClient.WillTopic);
            Assert.AreEqual("lost", mqttClient.WillMessage);
            Assert.IsTrue(mqttClient.WillRetain);
        }

        [TestMethod]
        public void HomieClient_Connect_Replaces_A_Foreign_Session()
        {
            // A session opened by someone else cannot carry the Homie will, so the
            // client must replace it rather than continue on it.

            // Arrange
            var mqttClient = new MockMqttClient();
            mqttClient.Connect("someone-else");

            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var device = builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyTemperatureTopicId, _testPropertyTemperatureName, 0.0)
                        .BuildProperty()
                    .BuildNode()
                .BuildDevice();

            var homieClient = new HomieClient(device, mqttClient);

            // Act
            var connected = homieClient.Connect();

            // Assert
            Assert.IsTrue(connected);
            Assert.IsTrue(mqttClient.WillFlag);
            Assert.AreEqual("lost", mqttClient.WillMessage);
        }

        [TestMethod]
        public void HomieClient_Lifecycle_States_Are_Reachable()
        {
            // All six $state values are part of the convention, but alert and sleeping
            // used to be unreachable from outside the library: Device.TryChangeState is
            // internal, and the client exposed no way to ask for them.

            // Arrange
            var mqttClient = new MockMqttClient();
            var homieClient = new HomieClient(BuildSinglePropertyDevice(), mqttClient);
            homieClient.Connect();

            // Assert -- Connect leaves the device ready
            Assert.AreEqual((int)State.Ready, (int)homieClient.State);

            // Act + Assert -- ready -> sleeping -> ready
            Assert.IsTrue(homieClient.Sleep());
            Assert.AreEqual((int)State.Sleeping, (int)homieClient.State);
            Assert.IsTrue(homieClient.Ready());
            Assert.AreEqual((int)State.Ready, (int)homieClient.State);

            // Act + Assert -- ready -> alert -> ready
            Assert.IsTrue(homieClient.Alert());
            Assert.AreEqual((int)State.Alert, (int)homieClient.State);
            Assert.IsTrue(homieClient.Ready());
            Assert.AreEqual((int)State.Ready, (int)homieClient.State);
        }

        [TestMethod]
        public void HomieClient_Refuses_An_Illegal_State_Transition()
        {
            // Arrange
            var mqttClient = new MockMqttClient();
            var homieClient = new HomieClient(BuildSinglePropertyDevice(), mqttClient);
            homieClient.Connect();
            homieClient.Alert();

            // Act -- alert may go to ready or disconnected, never straight to sleeping
            var slept = homieClient.Sleep();

            // Assert
            Assert.IsFalse(slept);
            Assert.AreEqual((int)State.Alert, (int)homieClient.State);
        }

        [TestMethod]
        public void HomieClient_Raises_OnCommand_For_A_Controller_Set()
        {
            // property.OnUpdate fires both when a controller sets a value and when the
            // device updates its own, so an actuator cannot act on it. OnCommand fires
            // only for the former.

            // Arrange
            var mqttClient = new MockMqttClient();

            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var device = builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyIntensityTopicId, _testPropertyIntensityName, 0.0)
                            .WithSettable(true)
                        .BuildProperty(out FloatProperty property)
                    .BuildNode()
                .BuildDevice();

            var homieClient = new HomieClient(device, mqttClient);
            homieClient.Connect();

            var commandCount = 0;
            PropertyBase commandedProperty = null;
            homieClient.OnCommand += (args) =>
            {
                commandCount++;
                commandedProperty = args.Property;
            };

            // Act -- the device updating itself is not a command
            property.Update(42.0);

            // Assert
            Assert.AreEqual(0, commandCount);

            // Act -- a controller writing to /set is
            var setTopic = $"{property.GetTopic()}{Constants.TopicSeparator}{Constants.SetPropertyTopicId}";
            mqttClient.RaisePublishReceived(new MqttMsgPublishEventArgs(setTopic, Encoding.UTF8.GetBytes("73"), false, MqttQoSLevel.AtLeastOnce, false));

            // Assert
            Assert.AreEqual(1, commandCount);
            Assert.IsNotNull(commandedProperty);
            Assert.AreEqual(73.0, property.Value);
        }

        [TestMethod]
        public void HomieClient_ReAnnounces_When_The_Connection_Reopens()
        {
            // A restarted broker has an empty retained store, so a device that merely
            // resumes publishing values is invisible to a controller: no $homie, no
            // $nodes, no $state. Reopening the connection must republish everything.

            // Arrange
            var mqttClient = new MockMqttClient();
            var homieClient = new HomieClient(BuildSinglePropertyDevice(), mqttClient);
            homieClient.Connect();

            var publishCountAfterConnect = mqttClient.PublishCount;
            Assert.AreEqual((int)State.Ready, (int)homieClient.State);

            // Act -- the transport dropped and came back underneath us
            mqttClient.RaiseConnectionClosed();
            mqttClient.RaiseConnectionOpened();

            // Assert -- the whole announcement went out again, ending at ready
            Assert.IsTrue(mqttClient.PublishCount > publishCountAfterConnect);
            Assert.AreEqual(publishCountAfterConnect * 2, mqttClient.PublishCount);
            Assert.AreEqual((int)State.Ready, (int)homieClient.State);
        }

        [TestMethod]
        public void HomieClient_Publishes_Once_Per_Update_After_A_Reconnect()
        {
            // The reconnect path re-registers the property update handlers, so a
            // careless += would publish every later update twice.

            // Arrange
            var mqttClient = new MockMqttClient();

            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var device = builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyTemperatureTopicId, _testPropertyTemperatureName, 0.0)
                        .BuildProperty(out FloatProperty property)
                    .BuildNode()
                .BuildDevice();

            var homieClient = new HomieClient(device, mqttClient);
            homieClient.Connect();

            mqttClient.RaiseConnectionClosed();
            mqttClient.RaiseConnectionOpened();

            var before = mqttClient.PublishCount;

            // Act
            property.Update(21.5);

            // Assert
            Assert.AreEqual(before + 1, mqttClient.PublishCount);
        }

        [TestMethod]
        public void HomieClient_Retried_Connect_Announces_Once_And_Stays_Connected()
        {
            // Both device apps call Connect() in a retry loop, so a failed attempt
            // followed by a successful one must behave exactly like a single successful
            // one. It did not: every attempt attached another set of event handlers, so
            // the second attempt fired the state-change handler twice, the Init branch
            // ran a second time against an already-'ready' device, TryChangeState
            // refused, and the failure path disconnected the client that had just
            // connected -- with auto-reconnect switched off, so it never came back.

            // Arrange -- first attempt fails at the transport
            var mqttClient = new MockMqttClient { FailNextConnect = true };
            var homieClient = new HomieClient(BuildSinglePropertyDevice(), mqttClient);

            // Act
            var firstAttempt = homieClient.Connect();
            var secondAttempt = homieClient.Connect();

            // Assert
            Assert.IsFalse(firstAttempt);
            Assert.IsTrue(secondAttempt);
            Assert.IsTrue(mqttClient.IsConnected);
            Assert.AreEqual((int)State.Ready, (int)homieClient.State);

            // The announcement must not be duplicated either.
            var publishesAfterRetriedConnect = mqttClient.PublishCount;

            var fresh = new MockMqttClient();
            new HomieClient(BuildSinglePropertyDevice(), fresh).Connect();

            Assert.AreEqual(fresh.PublishCount, publishesAfterRetriedConnect);
        }

        [TestMethod]
        public void HomieClient_Retried_Connect_Handles_A_Command_Once()
        {
            // Same leak seen from the /set side: a duplicated MqttMsgPublishReceived
            // registration made one controller command run the handler twice, which for
            // an actuator means acting on it twice.

            // Arrange
            var mqttClient = new MockMqttClient { FailNextConnect = true };

            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var device = builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyIntensityTopicId, _testPropertyIntensityName, 0.0)
                            .WithSettable(true)
                        .BuildProperty(out FloatProperty property)
                    .BuildNode()
                .BuildDevice();

            var homieClient = new HomieClient(device, mqttClient);
            homieClient.Connect();
            homieClient.Connect();

            var commandCount = 0;
            homieClient.OnCommand += (args) => { commandCount++; };

            // Act
            var commandTopic = $"{property.GetTopic()}{Constants.TopicSeparator}{Constants.SetPropertyTopicId}";
            mqttClient.RaisePublishReceived(new MqttMsgPublishEventArgs(commandTopic, Encoding.UTF8.GetBytes("55"), false, MqttQoSLevel.AtLeastOnce, false));

            // Assert
            Assert.AreEqual(1, commandCount);
            Assert.AreEqual(55.0, property.Value);
        }

        [TestMethod]
        public void HomieClient_Retried_Connect_ReAnnounces_Once_Per_Reconnect()
        {
            // And from the connection-change side: duplicated handlers meant one
            // reconnect re-announced the whole device twice.

            // Arrange
            var mqttClient = new MockMqttClient { FailNextConnect = true };
            var homieClient = new HomieClient(BuildSinglePropertyDevice(), mqttClient);
            homieClient.Connect();
            homieClient.Connect();

            var afterConnect = mqttClient.PublishCount;

            // Act
            mqttClient.RaiseConnectionClosed();
            mqttClient.RaiseConnectionOpened();

            // Assert -- exactly one announcement's worth of publishes
            Assert.AreEqual(afterConnect * 2, mqttClient.PublishCount);
        }

        [TestMethod]
        public void HomieClient_Announces_Once_When_Retry_Follows_A_Post_Handshake_Failure()
        {
            // The gap the FailNextConnect tests cannot reach. A first attempt that fails
            // at the *transport* never registers the connection-change handlers, so the
            // retry is clean. A first attempt that fails AFTER them -- here, at SUBSCRIBE
            // -- leaves them attached, and the real client raises ConnectionOpened
            // synchronously from CONNACK. The retry's ConnectInternal() would then
            // announce from HandleConnectionOpen and Connect() would announce again.

            // Arrange
            var mqttClient = new MockMqttClient { FailNextSubscribe = true };
            var device = BuildSinglePropertyDevice(out FloatProperty _, settable: true);
            var homieClient = new HomieClient(device, mqttClient);

            Assert.IsFalse(homieClient.Connect(), "the first attempt should fail at SUBSCRIBE");

            var afterFailedAttempt = mqttClient.PublishCount;

            // Act
            Assert.IsTrue(homieClient.Connect(), "the retry should succeed");

            // Assert -- exactly one announcement, and the device is usable.
            // 16 is a single-property device's announcement: the same count
            // HomieClient_Publish_On_Property_Update expects as 17, which is this plus
            // one property update. A double announce would be 32.
            var announcement = mqttClient.PublishCount - afterFailedAttempt;
            Assert.AreEqual(16, announcement, "the retry did not announce the device exactly once");
            Assert.IsTrue(mqttClient.IsConnected);
            Assert.AreEqual(State.Ready, homieClient.State);
        }

        [TestMethod]
        public void HomieClient_ReAnnounces_From_Alert_And_Returns_To_Alert()
        {
            // A broker restart while the device is alerting has to re-announce like any
            // other -- Device.CanChangeState used to forbid Alert -> Init, so the device
            // stayed invisible to the fresh broker -- and it must come back to 'alert'
            // rather than being quietly cleared to 'ready'.

            // Arrange
            var mqttClient = new MockMqttClient();
            var homieClient = new HomieClient(BuildSinglePropertyDevice(), mqttClient);
            homieClient.Connect();

            Assert.IsTrue(homieClient.Alert());
            Assert.AreEqual(State.Alert, homieClient.State);

            var beforeReconnect = mqttClient.PublishCount;

            // Act -- the broker goes away and comes back
            mqttClient.RaiseConnectionClosed();
            mqttClient.RaiseConnectionOpened();

            // Assert
            Assert.IsTrue(mqttClient.PublishCount > beforeReconnect, "the device did not re-announce from 'alert'");
            Assert.AreEqual(State.Alert, homieClient.State, "the re-announce cleared the alert");
        }

        [TestMethod]
        public void HomieClient_Disconnect_Closes_The_Session_When_The_Transition_Is_Refused()
        {
            // Disconnected -> Disconnected is not a legal transition, so a second
            // Disconnect() takes the refused path. That path used to log "Disconnected
            // MQTT client anyways" and leave the session, the subscriptions and the
            // handlers fully live.

            // Arrange
            var mqttClient = new MockMqttClient();
            var device = BuildSinglePropertyDevice(out FloatProperty _, settable: true);
            var homieClient = new HomieClient(device, mqttClient);
            homieClient.Connect();
            homieClient.Disconnect();

            // Re-open a session behind the client's back, so the second Disconnect() has
            // something real to close.
            mqttClient.Connect("someone-else");
            Assert.IsTrue(mqttClient.IsConnected);

            // Act -- the device is already 'disconnected', so the transition is refused
            homieClient.Disconnect();

            // Assert
            Assert.IsFalse(mqttClient.IsConnected, "Disconnect() left the MQTT session open");
        }

        [TestMethod]
        public void HomieClient_Uses_The_Device_Topic_Id_When_Settings_Carry_No_ClientId()
        {
            // The stable client id must not be opt-out-by-accident: passing settings to
            // set a keep-alive used to also hand you a fresh Guid per boot, which is what
            // leaves a dead session's 'lost' will to fire after the reboot announced
            // 'ready'.

            // Arrange
            var mqttClient = new MockMqttClient();
            var settings = new SmartHome.Homie.V4.Settings.HomieClientSettings { KeepAlivePeriod = 30 };
            var homieClient = new HomieClient(BuildSinglePropertyDevice(), mqttClient, settings);

            // Act
            homieClient.Connect();

            // Assert
            Assert.AreEqual(_testDeviceTopicId, mqttClient.ConnectedClientId);
            Assert.AreEqual((ushort)30, mqttClient.KeepAlivePeriod, "the caller's own setting was discarded");
        }

        [TestMethod]
        public void BooleanProperty_Announces_A_Spec_Legal_Payload()
        {
            // bool.ToString() returns "True"/"False", which Homie v4 does not permit.
            // Update() always emitted lowercase; only the announce path was wrong, so the
            // retained value was invalid until the first update overwrote it.

            // Arrange
            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddBooleanProperty("running", "Running", false)
                        .BuildProperty(out BooleanProperty property)
                    .BuildNode()
                .BuildDevice();

            // Act & Assert
            Assert.AreEqual("false", Encoding.UTF8.GetString(property.GetPayload(), 0, property.GetPayload().Length));

            property.Update(true);
            Assert.AreEqual("true", Encoding.UTF8.GetString(property.GetPayload(), 0, property.GetPayload().Length));
        }

        [TestMethod]
        public void HomieColor_Round_Trips_The_Spec_Format()
        {
            // Homie v4's rgb payload is "<r>,<g>,<b>" in decimal. This used to emit and
            // accept 6-digit hex only, so a conforming controller's command was dropped
            // silently -- and the property could not read back its own payload.

            // Act & Assert -- parse the spec format
            Assert.IsTrue(HomieColor.TryParse("255,128,0", out var parsed));
            Assert.AreEqual((byte)255, parsed.R);
            Assert.AreEqual((byte)128, parsed.G);
            Assert.AreEqual((byte)0, parsed.B);

            // ... and emit it
            Assert.AreEqual("255,128,0", parsed.ToString());

            // ... and round-trip its own output
            Assert.IsTrue(HomieColor.TryParse(parsed.ToString(), out var reparsed));
            Assert.AreEqual(parsed.ToString(), reparsed.ToString());

            // Rejections
            Assert.IsFalse(HomieColor.TryParse("FF8000", out _), "hex is not the v4 format");
            Assert.IsFalse(HomieColor.TryParse("255,128", out _), "too few components");
            Assert.IsFalse(HomieColor.TryParse("256,0,0", out _), "component out of range");
            Assert.IsFalse(HomieColor.TryParse(null, out _));
        }

        [TestMethod]
        public void FloatProperty_Publishes_A_Value_A_Controller_Can_Read_Back()
        {
            // The bug this guards: double.ToString() on nanoFramework uses "G" and renders
            // 21.5 as "21.499999999999999". A controller writing 21.5 and reading that back
            // is the complaint; the device is also unable to parse its own retained payload
            // back to the value it meant.
            //
            // Value-dependent, which is why it survived: 0.1 renders correctly.

            // Arrange
            var device = BuildSinglePropertyDevice(out FloatProperty property, settable: true);
            Assert.IsNotNull(device);

            // Act
            property.Update(21.5);
            var payload = Encoding.UTF8.GetString(property.GetPayload(), 0, property.GetPayload().Length);

            // Assert
            Assert.AreEqual("21.50", payload);
            Assert.IsFalse(payload.IndexOf("21.4999") >= 0, "the payload fell back to G formatting");

            // ... and the payload parses back to the value it represents
            Assert.IsTrue(double.TryParse(payload, out var parsed));
            Assert.AreEqual(21.5, parsed);
        }

        [TestMethod]
        public void FloatProperty_Uses_A_Dot_As_The_Decimal_Separator()
        {
            // Homie requires a dot. nanoFramework's Double has no
            // ToString(format, IFormatProvider) overload, so this cannot be pinned at the
            // call site -- the formatter reads NumberFormatInfo.CurrentInfo, which is
            // invariant only until something references nanoFramework.System.Globalization.
            var device = BuildSinglePropertyDevice(out FloatProperty property, settable: false);
            Assert.IsNotNull(device);

            property.Update(-3.25);
            var payload = Encoding.UTF8.GetString(property.GetPayload(), 0, property.GetPayload().Length);

            Assert.AreEqual("-3.25", payload);
            Assert.IsFalse(payload.IndexOf(',') >= 0, "a comma reached the wire");
        }

        [TestMethod]
        public void FloatProperty_Honours_The_Precision_It_Was_Given()
        {
            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty("whole", "Whole", 0.0)
                            .WithDecimals(0)
                        .BuildProperty(out FloatProperty whole)
                        .AddFloatProperty("precise", "Precise", 0.0)
                            .WithDecimals(4)
                        .BuildProperty(out FloatProperty precise)
                    .BuildNode()
                .BuildDevice();

            whole.Update(1234.5678);
            precise.Update(1234.5678);

            Assert.AreEqual("1235", Encoding.UTF8.GetString(whole.GetPayload(), 0, whole.GetPayload().Length));
            Assert.AreEqual("1234.5678", Encoding.UTF8.GetString(precise.GetPayload(), 0, precise.GetPayload().Length));

            // No group separator at any precision -- "N" would produce "1,234.57", which
            // no controller could parse.
            Assert.AreEqual(0, Encoding.UTF8.GetString(precise.GetPayload(), 0, precise.GetPayload().Length).Split(',').Length - 1);
        }

        [TestMethod]
        public void FloatProperty_Rejects_A_Precision_It_Cannot_Deliver()
        {
            Assert.ThrowsException(typeof(ArgumentException),
                () => new FloatProperty("temperature", "Temperature", decimals: -1));

            Assert.ThrowsException(typeof(ArgumentException),
                () => new FloatProperty("temperature", "Temperature", decimals: 16));
        }

        private Device BuildSinglePropertyDevice()
            => BuildSinglePropertyDevice(out FloatProperty _, settable: false);

        private Device BuildSinglePropertyDevice(out FloatProperty property, bool settable)
        {
            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            return builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyTemperatureTopicId, _testPropertyTemperatureName, 0.0)
                            .WithSettable(settable)
                        .BuildProperty(out property)
                    .BuildNode()
                .BuildDevice();
        }

        [TestMethod]
        public void HomieClient_Connect_Disconnect()
        {
            // Arrange
            int expectedPublishCountAfterConnect = 23;   // +1: $extensions is published now
            int expectedPublishCountAfterDisconnect = 24;
            int expectedSubscriptionCountConnected = 2;
            int expectedSubscriptionCountDisconnected = 0;

            var mqttClient = new MockMqttClient();

            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var device = builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyTemperatureTopicId, _testPropertyTemperatureName, 0.0)
                            .WithSettable(true)
                        .BuildProperty()
                        .AddFloatProperty(_testPropertyIntensityTopicId, _testPropertyIntensityName, 100.0)
                            .WithSettable(true)
                        .BuildProperty()
                    .BuildNode()
                .BuildDevice();

            var homieClient = new HomieClient(device, mqttClient);

            // Act
            homieClient.Connect();

            // Assert
            Assert.AreEqual(expectedSubscriptionCountConnected, mqttClient.SubscriptionCount);
            Assert.AreEqual(expectedPublishCountAfterConnect, mqttClient.PublishCount);

            // Act
            homieClient.Disconnect();

            // Assert
            Assert.AreEqual(expectedSubscriptionCountDisconnected, mqttClient.SubscriptionCount);
            Assert.AreEqual(expectedPublishCountAfterDisconnect, mqttClient.PublishCount);
        }
    }
}
