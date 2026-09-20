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
        private const string _problemConfigTopic = "homeassistant/binary_sensor/super-car_problem/config";
        private const string _problemTopic = "smarthome/super-car/problem";
        private const string _alertsTopic = "smarthome/super-car/alerts";
        private const string _stringStateTopic = "smarthome/super-car/engine/event";

        // What a single-property device's announcement costs: its own config, the
        // diagnostic entity's config, one value, the two alert topics, and availability.
        // Written once because several tests assert that an announcement happened exactly
        // once, and a literal in each of them says that only until the shape changes.
        private const int AnnouncePublishCount = 6;

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
        public void An_Announce_Publishes_Configs_Then_Values_Then_Alerts_Then_Online()
        {
            // The order is a courtesy rather than a requirement -- everything here is
            // retained, so Home Assistant is served whenever it subscribes -- but it is
            // the order that makes a broker log readable, and 'online' last is what makes
            // the device available only once it is actually describable.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            client.Connect();

            var publishes = mqttClient.Publishes;
            Assert.AreEqual(AnnouncePublishCount, publishes.Length, "two configs, one value, two alert topics, one availability");

            Assert.AreEqual(_configTopic, publishes[0].Topic);
            Assert.IsTrue(publishes[0].Retain, "a discovery config is retained or nobody who boots later sees it");

            Assert.AreEqual(_problemConfigTopic, publishes[1].Topic);
            Assert.IsTrue(publishes[1].Retain);

            Assert.AreEqual(_stateTopic, publishes[2].Topic);
            Assert.AreEqual("0.00", publishes[2].Payload);
            Assert.IsTrue(publishes[2].Retain);

            // Nothing is wrong, and the device says so rather than leaving the diagnostic
            // entity unknown until the first thing goes wrong.
            Assert.AreEqual(_problemTopic, publishes[3].Topic);
            Assert.AreEqual("OFF", publishes[3].Payload);
            Assert.IsTrue(publishes[3].Retain);

            Assert.AreEqual(_alertsTopic, publishes[4].Topic);
            Assert.AreEqual("{}", publishes[4].Payload, "no alerts, and therefore no attributes");

            Assert.AreEqual(_availabilityTopic, publishes[5].Topic);
            Assert.AreEqual("online", publishes[5].Payload);
            Assert.IsTrue(publishes[5].Retain);
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
            // Index two: the property's config, then the device's diagnostic one, then
            // the value.
            Assert.AreEqual(_stringStateTopic, publishes[2].Topic);
            Assert.IsFalse(publishes[2].Retain, "the announce carries the property's own flag");
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

            // The command topic first, Home Assistant's birth topic after it: one
            // SUBSCRIBE carries both.
            Assert.AreEqual(2, mqttClient.SubscriptionCount);
            Assert.AreEqual(_commandTopic, mqttClient.SubscribedTopics[0]);
            Assert.AreEqual("homeassistant/status", mqttClient.SubscribedTopics[1]);
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
        public void An_Alert_Turns_The_Diagnostic_Entity_On_And_Carries_Its_Message()
        {
            // The whole of what this convention can say about health: one entity that is
            // on while anything is wrong, and the ids and messages as its attributes --
            // which is more than a convention with a single lifecycle token can carry,
            // and is why the model keeps alerts keyed.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            client.Connect();

            client.RaiseAlert("sensor", "BMP280 reading invalid.");

            Assert.AreEqual("ON", Last(mqttClient, _problemTopic));
            Assert.AreEqual(
                "{\"alerts\":\"sensor\",\"sensor\":\"BMP280 reading invalid.\"}",
                Last(mqttClient, _alertsTopic));

            // A device that has raised an alert is still reachable and still publishing,
            // and its other properties may be perfectly good: availability says whether
            // the device is there, not whether it is happy.
            Assert.AreEqual("online", client.Availability);
            Assert.AreEqual((int)DeviceState.Ready, (int)client.State);

            client.ClearAlert("sensor");

            Assert.AreEqual("OFF", Last(mqttClient, _problemTopic));
            Assert.AreEqual("{}", Last(mqttClient, _alertsTopic));
            Assert.AreEqual("online", client.Availability);
        }

        [TestMethod]
        public void A_Second_Alert_Changes_The_Attributes_And_Not_The_State()
        {
            // The two topics move independently, which is the point of publishing each
            // only when it changed: a device that raises a second alert has nothing new
            // to say about *whether* something is wrong, and republishing 'ON' over 'ON'
            // would be noise in every retained store.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            client.Connect();
            client.RaiseAlert("sensor", "BMP280 reading invalid.");

            var stateBefore = CountFor(mqttClient, _problemTopic);
            var attributesBefore = CountFor(mqttClient, _alertsTopic);

            client.RaiseAlert("configuration", "The configuration file is missing.");

            Assert.AreEqual(stateBefore, CountFor(mqttClient, _problemTopic), "the state did not move");
            Assert.AreEqual(attributesBefore + 1, CountFor(mqttClient, _alertsTopic), "the attributes did");

            // Sorted by id, so the same set of alerts always renders the same payload --
            // the model's set is a hashtable, whose enumeration order is neither stable
            // nor the author's.
            Assert.AreEqual(
                "{\"alerts\":\"configuration, sensor\"" +
                ",\"configuration\":\"The configuration file is missing.\"" +
                ",\"sensor\":\"BMP280 reading invalid.\"}",
                Last(mqttClient, _alertsTopic));

            // Re-raising one with the message it already carries changes nothing at all:
            // the model does not raise the event, so nothing is republished. A device
            // raising an alert from inside its measurement loop depends on that.
            var attributesAfter = CountFor(mqttClient, _alertsTopic);
            client.RaiseAlert("sensor", "BMP280 reading invalid.");
            Assert.AreEqual(attributesAfter, CountFor(mqttClient, _alertsTopic));
        }

        [TestMethod]
        public void An_Alert_Raised_Before_Connect_Is_Part_Of_The_Announcement()
        {
            // How a device that could not read its configuration says so. The shared
            // failure behaviour raises the alert BEFORE connecting, precisely so that the
            // announcement already carries it -- a misconfigured device must not advertise
            // a healthy state, not even for one publish.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            client.RaiseAlert("configuration", "The configuration file is missing.");
            client.Connect();

            Assert.AreEqual("ON", Last(mqttClient, _problemTopic));
            Assert.AreEqual(1, CountFor(mqttClient, _problemTopic), "said once, in the announcement");
            Assert.AreEqual(
                "{\"alerts\":\"configuration\",\"configuration\":\"The configuration file is missing.\"}",
                Last(mqttClient, _alertsTopic));
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
            Assert.AreEqual(_problemConfigTopic, publishes[publishesAfterConnect + 1].Topic);
            Assert.AreEqual(_stateTopic, publishes[publishesAfterConnect + 2].Topic);
            Assert.AreEqual(_problemTopic, publishes[publishesAfterConnect + 3].Topic);
            Assert.AreEqual(_alertsTopic, publishes[publishesAfterConnect + 4].Topic);

            // Availability last, and published again even though it has not changed: the
            // broker this is announcing into may have restarted with an empty store, so
            // "we already said online" is a statement about a session that is gone.
            Assert.AreEqual(_availabilityTopic, publishes[publishesAfterConnect + 5].Topic);
            Assert.AreEqual("online", publishes[publishesAfterConnect + 5].Payload);
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
        public void A_Retried_Connect_Announces_Once()
        {
            // Connect() is called in a retry loop by every device app, so a handler left
            // attached by a failed attempt would make the next success announce twice.
            var mqttClient = new MockMqttClient { FailNextConnect = true };
            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            Assert.IsTrue(client.ConnectWithRetry(maxAttempts: 2, retryDelayMs: 1));

            Assert.AreEqual(2, mqttClient.ConnectCallCount, "one failed attempt, one that took");
            Assert.AreEqual(AnnouncePublishCount, mqttClient.Publishes.Length, "the announcement went out once");
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

        [TestMethod]
        public void Home_Assistant_Coming_Online_Announces_Everything_Again()
        {
            // Retained configurations do not cover a Home Assistant restart on their own:
            // they are replayed only once its MQTT integration has subscribed, and an
            // installation that was reconfigured may not replay them at all -- which is
            // why Home Assistant publishes a birth message precisely so devices can
            // announce themselves again.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            client.Connect();
            var publishesAfterConnect = mqttClient.Publishes.Length;

            RaiseDiscoveryStatus(mqttClient, "online");

            var publishes = mqttClient.Publishes;

            Assert.AreEqual(publishesAfterConnect + 5, publishes.Length, "the configs, the values and the alert topics went out again");
            Assert.AreEqual(_configTopic, publishes[publishesAfterConnect].Topic);
            Assert.AreEqual(_problemConfigTopic, publishes[publishesAfterConnect + 1].Topic);
            Assert.AreEqual(_stateTopic, publishes[publishesAfterConnect + 2].Topic);
            Assert.AreEqual(_problemTopic, publishes[publishesAfterConnect + 3].Topic);
            Assert.AreEqual(_alertsTopic, publishes[publishesAfterConnect + 4].Topic);

            // Availability is not republished: it is retained and unchanged, so the broker
            // hands Home Assistant the 'online' that is already there. The device did not
            // go anywhere, and a consumer restarting is not a lifecycle event for it.
            Assert.AreEqual(1, CountFor(mqttClient, _availabilityTopic));
            Assert.AreEqual((int)DeviceState.Ready, (int)client.State);
        }

        [TestMethod]
        public void Home_Assistant_Going_Offline_Changes_Nothing()
        {
            // 'offline' is Home Assistant's own will. It says the consumer went away, not
            // that this device did, and re-announcing into a broker nobody is reading is
            // just traffic.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            client.Connect();
            var publishesAfterConnect = mqttClient.Publishes.Length;

            RaiseDiscoveryStatus(mqttClient, "offline");

            Assert.AreEqual(publishesAfterConnect, mqttClient.Publishes.Length);
            Assert.AreEqual("online", client.Availability, "and the device is still available");
        }

        [TestMethod]
        public void The_Status_Topic_Is_Subscribed_Even_With_Nothing_Settable()
        {
            // One SUBSCRIBE carrying the command topics and Home Assistant's birth topic.
            // A device with nothing settable still has to hear Home Assistant come back,
            // or it stays discovered only for as long as that installation keeps its
            // retained configurations.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            client.Connect();

            Assert.AreEqual(1, mqttClient.SubscriptionCount, "no settable property, and still one topic");
            Assert.AreEqual("homeassistant/status", mqttClient.SubscribedTopics[0]);
            Assert.AreEqual((int)MqttQoSLevel.AtLeastOnce, (int)mqttClient.SubscribedQosLevels[0]);
        }

        [TestMethod]
        public void Remove_Withdraws_Every_Config_With_An_Empty_Payload()
        {
            // The only way a discovered entity goes away. A discovery configuration
            // outlives the device that published it -- a reflash, a rename, a broker
            // restart -- so a property that is renamed leaves a working-looking entity
            // behind, wired to a topic nothing publishes to any more. An empty retained
            // payload is how MQTT deletes a retained message and how Home Assistant
            // deletes the entity: the two are the same act.
            var mqttClient = new MockMqttClient();
            var client = new HomeAssistantClient(BuildDevice(out _), mqttClient);

            client.Connect();
            var publishesAfterConnect = mqttClient.Publishes.Length;

            Assert.IsTrue(client.Remove());

            var publishes = mqttClient.Publishes;
            Assert.AreEqual(publishesAfterConnect + 2, publishes.Length, "one withdrawal per config, the diagnostic entity included");

            Assert.AreEqual(_configTopic, publishes[publishesAfterConnect].Topic);
            Assert.AreEqual(string.Empty, publishes[publishesAfterConnect].Payload);
            Assert.IsTrue(publishes[publishesAfterConnect].Retain, "retained, or the store keeps the configuration it is meant to delete");

            Assert.AreEqual(_problemConfigTopic, publishes[publishesAfterConnect + 1].Topic);
            Assert.AreEqual(string.Empty, publishes[publishesAfterConnect + 1].Payload);

            // The device's own state is left alone: it stays true whether or not Home
            // Assistant is listening, and clearing it would make a withdrawal
            // indistinguishable from a device that went away.
            Assert.AreEqual("online", Last(mqttClient, _availabilityTopic));
        }

        private static void RaiseDiscoveryStatus(MockMqttClient mqttClient, string payload) =>
            mqttClient.RaisePublishReceived(new MqttMsgPublishEventArgs(
                "homeassistant/status",
                Encoding.UTF8.GetBytes(payload),
                false,
                MqttQoSLevel.AtLeastOnce,
                false));

        /// <summary>The last payload published to a topic.</summary>
        private static string Last(MockMqttClient mqttClient, string topic)
        {
            var payloads = mqttClient.PayloadsFor(topic);

            Assert.IsTrue(payloads.Length > 0, $"nothing was published to '{topic}'");

            return payloads[payloads.Length - 1];
        }

        /// <summary>How many times a topic was published to.</summary>
        private static int CountFor(MockMqttClient mqttClient, string topic) => mqttClient.PayloadsFor(topic).Length;

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
