using SmartHome.DeviceModel;
using SmartHome.DeviceModel.Builder;
using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.Formats;
using SmartHome.DeviceModel.Properties;
using SmartHome.HomeAssistant;
using SmartHome.Protocol;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using nanoFramework.M2Mqtt.Messages;
using nanoFramework.TestFramework;
using System;
using System.Text;

namespace SmartHome.UnitTests
{
    /// <summary>
    /// The Home Assistant adapter driving a session: what it announces, what it declares
    /// in CONNECT, what it subscribes to, how it survives a reconnect, and how a
    /// controller's command travels through it.
    /// </summary>
    /// <remarks>
    /// The device tree underneath is the protocol-neutral model, so everything Home
    /// Assistant about these tests -- the topics, the availability payloads, the last
    /// will -- comes from the adapter and nowhere else. Nothing here references
    /// SmartHome.Homie, which is the whole point of the slice: the two adapters are peers
    /// and a device speaks one of them.
    /// </remarks>
    [TestClass]
    public class HomeAssistantClientTests
    {
        private const string _deviceId = "super-car";
        private const string _deviceName = "Super car";
        private const string _nodeId = "engine";
        private const string _nodeName = "Engine";
        private const string _nodeType = "V8";

        private const string _stateTopic = "smarthome/super-car/engine/temperature";
        private const string _commandTopic = "smarthome/super-car/engine/temperature/set";
        private const string _availabilityTopic = "smarthome/super-car/status";
        private const string _configTopic = "homeassistant/sensor/super-car_engine_temperature/config";
        private const string _numberConfigTopic = "homeassistant/number/super-car_engine_temperature/config";

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
        public void The_Client_Is_Driven_Entirely_Through_The_Protocol_Seam()
        {
            // A device app builds a neutral Device, constructs one adapter, and then talks
            // only to IDeviceProtocol -- so which convention it speaks is a choice about
            // the line below and nowhere else.
            var mqttClient = new MockMqttClient();
            IDeviceProtocol protocol = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            Assert.IsTrue(protocol.Connect());

            Assert.AreEqual(_deviceId, protocol.DeviceId);
            Assert.AreEqual((int)DeviceState.Ready, (int)protocol.State);
            Assert.IsTrue(protocol.IsConnected);

            protocol.Disconnect();
            Assert.IsFalse(protocol.IsConnected);
        }

        [TestMethod]
        public void Connect_Declares_This_Adapters_Own_Last_Will()
        {
            // The reason this adapter owns its session. Availability is a plain topic,
            // and the 'offline' that matters -- the one a device that dropped off the
            // network can no longer publish -- can only come from a will, which can only
            // be declared in CONNECT.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            client.Connect();

            Assert.IsTrue(mqttClient.WillFlag, "a will was declared");
            Assert.AreEqual(_availabilityTopic, mqttClient.WillTopic);
            Assert.AreEqual("offline", mqttClient.WillMessage);
            Assert.IsTrue(mqttClient.WillRetain, "retained, so a controller connecting later still learns the device is gone");
            Assert.AreEqual((int)MqttQoSLevel.AtLeastOnce, (int)mqttClient.WillQosLevel);

            // The device's own id, not a fresh Guid: with a per-boot random client id the
            // broker keeps the dead session alive until its keep-alive expires, so the old
            // session's 'offline' will arrives after the rebooted device has already said
            // it is online.
            Assert.AreEqual(_deviceId, mqttClient.ConnectedClientId);
        }

        [TestMethod]
        public void A_Session_Somebody_Else_Opened_Is_Replaced_Rather_Than_Reused()
        {
            var mqttClient = new MockMqttClient();
            mqttClient.Connect("an-app-that-connected-first");

            Assert.IsFalse(mqttClient.WillFlag, "the foreign session carries no will");

            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            Assert.IsTrue(client.Connect());

            Assert.AreEqual(1, mqttClient.DisconnectCallCount, "the foreign session was closed");
            Assert.IsTrue(mqttClient.WillFlag, "the replacement carries the will");
            Assert.AreEqual(_deviceId, mqttClient.ConnectedClientId);
        }

        [TestMethod]
        public void An_Announce_Publishes_Configs_Then_Values_Then_Online()
        {
            // The order is a courtesy rather than a requirement -- everything here is
            // retained, so Home Assistant is served whenever it subscribes -- but it is
            // the order that makes a broker log readable, and 'online' last is what makes
            // the device available only once it is actually describable.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            client.Connect();

            var publishes = mqttClient.Publishes;
            Assert.AreEqual(3, publishes.Length, "one config, one value, one availability");

            Assert.AreEqual(_configTopic, publishes[0].Topic);
            Assert.IsTrue(publishes[0].Retain, "a discovery config is retained or nobody who boots later sees it");

            Assert.AreEqual(_stateTopic, publishes[1].Topic);
            Assert.AreEqual("0.00", publishes[1].Payload);
            Assert.IsTrue(publishes[1].Retain);

            Assert.AreEqual(_availabilityTopic, publishes[2].Topic);
            Assert.AreEqual("online", publishes[2].Payload);
            Assert.IsTrue(publishes[2].Retain);
            Assert.AreEqual("online", client.Availability);
        }

        [TestMethod]
        public void A_Value_Is_Published_On_This_Adapters_Own_Topic()
        {
            // Not pointed at another convention's topic: this adapter publishes every
            // value itself, which is what makes it a peer rather than a passenger.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildDevice(out FloatProperty temperature), mqttClient);

            client.Connect();
            temperature.Update(21.5);

            var publishes = mqttClient.Publishes;
            var last = publishes[publishes.Length - 1];

            Assert.AreEqual(_stateTopic, last.Topic);
            // The property's own encoding, fixed-decimal -- never double.ToString(), which
            // renders 21.5 as 21.499999999999999 on this runtime.
            Assert.AreEqual("21.50", last.Payload);
            Assert.IsTrue(last.Retain);
        }

        [TestMethod]
        public void A_Property_That_Declares_Itself_Momentary_Is_Published_Unretained()
        {
            // A momentary event must not be left in the store, or every controller that
            // connects afterwards is told a button was just pressed.
            var mqttClient = new MockMqttClient();
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddStringProperty("event", "Event", "")
                        .WithRetained(false)
                    .BuildProperty(out StringProperty property)
                .BuildNode()
                .BuildDevice();

            var client = new HomeAssistantClient(device, mqttClient);
            client.Connect();
            property.Update("pressed");

            var publishes = mqttClient.Publishes;

            Assert.IsTrue(publishes[0].Retain, "the config is retained whatever the property says");
            Assert.IsFalse(publishes[1].Retain, "the announce carries the property's own flag");
            Assert.IsFalse(publishes[publishes.Length - 1].Retain, "and so does the update");
        }

        [TestMethod]
        public void Commands_Are_Subscribed_On_The_Set_Topic_At_Least_Once()
        {
            // A separate topic from the value: a device subscribed to its own value topic
            // would never receive a command, and would re-consume its own retained
            // publishes as if a controller had sent them.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildSettableDevice(out _), mqttClient);

            client.Connect();

            Assert.AreEqual(1, mqttClient.SubscriptionCount);
            Assert.AreEqual(_commandTopic, mqttClient.SubscribedTopics[0]);
            // At most once would let the broker drop a controller's command silently,
            // which is the one thing neither end can notice.
            Assert.AreEqual((int)MqttQoSLevel.AtLeastOnce, (int)mqttClient.SubscribedQosLevels[0]);
        }

        [TestMethod]
        public void A_Command_Is_Applied_Reflected_And_Only_Then_Raised()
        {
            // The rule every actuator has to follow, pinned where CI can run it. The
            // adapter reflects the payload onto the property -- and therefore onto the
            // state topic -- BEFORE OnCommand runs, so a property whose value is a
            // *request* the device can turn down is reflected optimistically and
            // retained. Only the app knows whether the request was honoured, so only the
            // app can correct it, and the correction has to land AFTER the reflection:
            // the library's publish is already out by the time the handler is called.
            //
            // Identical to the Homie adapter's behaviour, deliberately: validation,
            // reflection and OnCommand are the model's and the seam's, not a convention's.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildSettableDevice(out FloatProperty setpoint), mqttClient);

            client.Connect();

            var commands = 0;
            client.OnCommand += (args) =>
            {
                commands++;
                Assert.AreEqual("25", Encoding.UTF8.GetString(args.Payload, 0, args.Payload.Length), "the raw payload, before parsing");
                Assert.AreEqual(setpoint.Id, args.Property.Id);

                // The device turning the request down, and saying so on the wire.
                setpoint.Update(10.0);
            };

            var publishesBefore = mqttClient.Publishes.Length;
            SendCommand(mqttClient, setpoint, "25");

            Assert.AreEqual(1, commands, "the handler saw the command");
            Assert.AreEqual(10.0, setpoint.Value, "the correction stood");

            var publishes = mqttClient.Publishes;
            Assert.AreEqual(publishesBefore + 2, publishes.Length, "the reflection and the correction");
            Assert.AreEqual("25.00", publishes[publishesBefore].Payload, "the reflection went first");
            Assert.AreEqual("10.00", publishes[publishesBefore + 1].Payload, "and the device's real value over it");
        }

        [TestMethod]
        public void A_Payload_The_Property_Refuses_Never_Reaches_A_Handler()
        {
            // A rejected payload violates the property's own datatype or declared format:
            // the value does not move and nothing is published, so raising OnCommand for
            // it would hand every actuator a payload the library has already refused.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildSettableDevice(out FloatProperty setpoint), mqttClient);

            client.Connect();

            var commands = 0;
            client.OnCommand += (args) => commands++;

            var publishesBefore = mqttClient.Publishes.Length;

            SendCommand(mqttClient, setpoint, "hot");
            SendCommand(mqttClient, setpoint, "250");

            Assert.AreEqual(0, commands, "neither a non-number nor a value outside the declared range");
            Assert.AreEqual(1.0, setpoint.Value, "the value did not move");
            Assert.AreEqual(publishesBefore, mqttClient.Publishes.Length, "and nothing was published");
        }

        [TestMethod]
        public void Traffic_On_Another_Topic_Is_Ignored()
        {
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildSettableDevice(out _), mqttClient);

            client.Connect();
            var publishesBefore = mqttClient.Publishes.Length;

            mqttClient.RaisePublishReceived(new MqttMsgPublishEventArgs(
                "smarthome/another-device/engine/temperature/set",
                Encoding.UTF8.GetBytes("25"),
                false,
                MqttQoSLevel.AtLeastOnce,
                false));

            Assert.AreEqual(publishesBefore, mqttClient.Publishes.Length);
        }

        [TestMethod]
        public void A_Sleeping_Device_Stays_Available()
        {
            // Home Assistant has no lifecycle attribute, and a battery device that sleeps
            // between readings is working as intended -- greying its entities out on every
            // wakeup would be wrong every time. What makes a *stopped* device visible is
            // expire_after, which is about the value going stale.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            client.Connect();
            var publishesAfterConnect = mqttClient.Publishes.Length;

            client.Sleep();
            Assert.AreEqual((int)DeviceState.Sleeping, (int)client.State);
            Assert.AreEqual("online", client.Availability);

            client.Ready();
            Assert.AreEqual((int)DeviceState.Ready, (int)client.State);

            // Neither transition put anything on the wire: the payload has not changed,
            // and republishing 'online' over 'online' is noise in every retained store.
            Assert.AreEqual(publishesAfterConnect, mqttClient.Publishes.Length);
        }

        [TestMethod]
        public void An_Alert_Does_Not_Make_The_Device_Unavailable()
        {
            // A device that has raised an alert is still reachable and still publishing,
            // and its other properties may be perfectly good. Availability says whether
            // the device is there, not whether it is happy. What the alert set becomes on
            // this wire is a diagnostic entity, which is the next slice.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            client.Connect();
            var publishesAfterConnect = mqttClient.Publishes.Length;

            client.RaiseAlert("sensor", "BMP280 reading invalid.");

            Assert.AreEqual("online", client.Availability);
            Assert.AreEqual((int)DeviceState.Ready, (int)client.State);
            Assert.AreEqual(publishesAfterConnect, mqttClient.Publishes.Length);

            client.ClearAlert("sensor");
            Assert.AreEqual("online", client.Availability);
        }

        [TestMethod]
        public void Disconnect_Says_Offline_Before_It_Closes()
        {
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            client.Connect();
            client.Disconnect();

            var publishes = mqttClient.Publishes;
            var last = publishes[publishes.Length - 1];

            Assert.AreEqual(_availabilityTopic, last.Topic);
            Assert.AreEqual("offline", last.Payload);
            Assert.IsTrue(last.Retain);
            Assert.IsFalse(mqttClient.IsConnected);
            Assert.AreEqual(0, mqttClient.SubscriptionCount, "the command topics were released");
        }

        [TestMethod]
        public void A_Reconnect_Announces_Everything_Again()
        {
            // A broker that restarted has an empty retained store, so the discovery
            // configs, the values and the availability are all gone even though the client
            // reconnected cleanly. Without this the device publishes into a broker that
            // has never heard of it, and Home Assistant keeps the entities it already had
            // while showing every one of them as unavailable.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            client.Connect();
            var publishesAfterConnect = mqttClient.Publishes.Length;

            mqttClient.RaiseConnectionClosed();
            mqttClient.RaiseConnectionOpened();

            var publishes = mqttClient.Publishes;
            Assert.AreEqual(publishesAfterConnect * 2, publishes.Length, "the whole announcement went out again");
            Assert.AreEqual(_configTopic, publishes[publishesAfterConnect].Topic);
            Assert.AreEqual(_stateTopic, publishes[publishesAfterConnect + 1].Topic);
            Assert.AreEqual(_availabilityTopic, publishes[publishesAfterConnect + 2].Topic);
            Assert.AreEqual("online", publishes[publishesAfterConnect + 2].Payload);
        }

        [TestMethod]
        public void A_Device_That_Was_Asleep_Comes_Back_Asleep()
        {
            // A broker restart is not a reason to wake a sleeping device, and the
            // re-announce has to return it to the state it was in rather than to Ready.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            client.Connect();
            client.Sleep();

            mqttClient.RaiseConnectionClosed();
            mqttClient.RaiseConnectionOpened();

            Assert.AreEqual((int)DeviceState.Sleeping, (int)client.State);
            Assert.AreEqual("online", client.Availability);
        }

        [TestMethod]
        public void A_Property_Update_Is_Published_Once_After_A_Reconnect()
        {
            // The handler registration runs again on every reconnect, so a missing
            // unsubscribe would publish every reading twice -- which reads at the broker
            // exactly like a device in a loop.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildDevice(out FloatProperty temperature), mqttClient);

            client.Connect();
            mqttClient.RaiseConnectionClosed();
            mqttClient.RaiseConnectionOpened();

            var publishesBefore = mqttClient.Publishes.Length;
            temperature.Update(18.0);

            Assert.AreEqual(publishesBefore + 1, mqttClient.Publishes.Length);
        }

        [TestMethod]
        public void A_Failed_Connect_Reports_Itself_And_Publishes_Nothing()
        {
            var mqttClient = new MockMqttClient { FailNextConnect = true };
            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            Assert.IsFalse(client.Connect());
            Assert.AreEqual(0, mqttClient.PublishCount, "nothing was announced into a session that does not exist");
        }

        [TestMethod]
        public void A_Half_Published_Announcement_Does_Not_Report_The_Device_Available()
        {
            // The worst of the three outcomes is the one this prevents: Home Assistant
            // showing whichever entities did arrive as working, with the ones that did not
            // simply absent and the reason in a log on the device. So a publish that fails
            // -- a QoS 1 publish raises whenever the link is momentarily down -- stops the
            // device short of Ready, nothing says 'online', and Connect() says so.
            var mqttClient = new MockMqttClient { FailNextPublish = true };
            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            Assert.IsFalse(client.Connect(), "the connect reported the incomplete announcement");
            Assert.AreNotEqual("online", client.Availability, "nothing said the device was available");
            Assert.AreEqual((int)DeviceState.Disconnecting, (int)client.State, "and it did not reach Ready");

            // Retrying is what a device app does with a false from Connect(), and the
            // session that failed must not count as announced -- otherwise the
            // configurations that never went out would never be retried on it.
            Assert.IsTrue(client.Connect(), "the retry took");
            Assert.AreEqual("online", client.Availability);
            Assert.AreEqual(1, mqttClient.PayloadsFor(_configTopic).Length, "and the configuration went out exactly once, on the attempt that worked");
        }

        [TestMethod]
        public void A_Retried_Connect_Announces_Once()
        {
            // Connect() is called in a retry loop by every device app, so a handler left
            // attached by a failed attempt would make the next success announce twice.
            var mqttClient = new MockMqttClient { FailNextConnect = true };
            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            Assert.IsTrue(client.ConnectWithRetry(maxAttempts: 2, retryDelayMs: 1));

            Assert.AreEqual(2, mqttClient.ConnectCallCount, "one failed attempt, one that took");
            Assert.AreEqual(3, mqttClient.Publishes.Length, "one config, one value, one availability -- once");
        }

        [TestMethod]
        public void A_Device_Home_Assistant_Cannot_Carry_Is_Refused_When_It_Is_Built()
        {
            // The adapter walks the tree in its constructor, before a session exists, so
            // a device that cannot be published never opens one. What is refused, and
            // why, is HomeAssistantDiscoveryTests' subject.
            var mqttClient = new MockMqttClient();

            var withDuration = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddDurationProperty("uptime", "Uptime")
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            Assert.ThrowsException(typeof(ArgumentException), () => new HomeAssistantClient(withDuration, mqttClient));

            Assert.AreEqual(0, mqttClient.ConnectCallCount, "nothing connected on the way to the refusal");
            Assert.AreEqual(0, mqttClient.PublishCount);
        }

        [TestMethod]
        public void A_Settable_Property_Announces_As_A_Number_With_Its_Command_Topic()
        {
            // The two halves of the same slice meeting: the mapping says a settable
            // numeric property is a number entity with a command topic, and the client
            // subscribes exactly that topic.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildSettableDevice(out _), mqttClient);

            client.Connect();

            var publishes = mqttClient.Publishes;
            Assert.AreEqual(_numberConfigTopic, publishes[0].Topic);
            Assert.IsTrue(publishes[0].Payload.IndexOf($"\"cmd_t\":\"{_commandTopic}\"") >= 0, $"the command topic is in '{publishes[0].Payload}'");
            Assert.AreEqual(_commandTopic, mqttClient.SubscribedTopics[0]);
        }

        private static void SendCommand(MockMqttClient mqttClient, PropertyBase property, string payload)
        {
            mqttClient.RaisePublishReceived(
                new MqttMsgPublishEventArgs(
                    HomeAssistantTopics.Command(property),
                    Encoding.UTF8.GetBytes(payload),
                    false,
                    MqttQoSLevel.AtLeastOnce,
                    false));
        }

        /// <summary>One read-only float, which announces as a sensor.</summary>
        private static Device BuildDevice(out FloatProperty temperature) =>
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("temperature", "Temperature", 0.0)
                        .WithUnit(Units.DegreeCelsius)
                        .WithQuantityKind(QuantityKind.Temperature)
                    .BuildProperty(out temperature)
                .BuildNode()
                .BuildDevice();

        /// <summary>
        /// The same property, settable, which announces as a number.
        /// </summary>
        /// <remarks>
        /// It declares a range because this adapter requires one: Home Assistant's number
        /// entity always has a minimum and a maximum and drops any state outside them, so
        /// a property that declares none would have its own readings refused by the
        /// controller meant to display them.
        /// </remarks>
        private static Device BuildSettableDevice(out FloatProperty setpoint) =>
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("temperature", "Temperature", 1.0)
                        .WithUnit(Units.DegreeCelsius)
                        .WithQuantityKind(QuantityKind.Temperature)
                        .WithSettable(true)
                        .WithRange(NumericRange.Between(0, 30))
                    .BuildProperty(out setpoint)
                .BuildNode()
                .BuildDevice();
    }
}
