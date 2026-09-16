using SmartHome.DeviceModel;
using SmartHome.DeviceModel.Builder;
using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.Properties;
using SmartHome.Homie.V4;
using SmartHome.Homie.V4.Settings;
using SmartHome.Text;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using nanoFramework.M2Mqtt.Messages;
using nanoFramework.TestFramework;
using System.Text;

namespace SmartHome.UnitTests
{
    // The adapter driving a session: what it announces, what it subscribes to, how it
    // survives a retry and a reconnect, and how a controller's command travels through
    // it. The device tree underneath is the protocol-neutral model, so everything Homie
    // about these tests -- topics, $state tokens, the last will -- comes from the adapter
    // and nowhere else.
    [TestClass]
    public class HomieClientTests
    {
        private const string _testDeviceId = "super-car";
        private const string _testDeviceName = "Super car";

        // Only what the tests actually use. Fourteen further constants (wheels, lights,
        // angle, speed, direction, colour) were declaration-only, describing a fixture
        // no test in this file builds -- the first thing a reader had to disprove.
        private const string _testNodeEngineId = "engine";
        private const string _testNodeEngineName = "Engine";
        private const string _testNodeEngineType = "V8";

        private const string _testPropertyTemperatureId = "temperature";
        private const string _testPropertyTemperatureName = "Temperature";

        private const string _testPropertyIntensityId = "intensity";
        private const string _testPropertyIntensityName = "Intensity";

        private const string _testPropertyLifecycleId = "lifecycle";
        private const string _testPropertyLifecycleName = "Lifecycle control";

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
            int expectedPublishCount = 17;   // 16 for the announcement, +1 for the update
            int expectedSubscriptionCount = 0;

            var mqttClient = new MockMqttClient();

            var device = BuildSinglePropertyDevice(out FloatProperty property, settable: false);

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
            int expectedPublishCount = 17;   // 16 for the announcement, +1 for the reflection
            int expectedSubscriptionCount = 1;
            double initialValue = 0.0;
            double expectedValue = 25;

            var mqttClient = new MockMqttClient();

            var device = BuildSinglePropertyDevice(out FloatProperty property, settable: true);

            // Assert
            Assert.AreEqual(initialValue, property.Value);

            // Act
            var homieClient = new HomieClient(device, mqttClient);

            homieClient.Connect();
            SendCommand(mqttClient, property, expectedValue.ToString());

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

            var device = BuildSinglePropertyDevice();

            var homieClient = new HomieClient(device, mqttClient);

            // Act
            var connected = homieClient.Connect();

            // Assert
            Assert.IsTrue(connected);
            Assert.IsTrue(mqttClient.WillFlag);
            Assert.AreEqual(HomieTopics.Attribute(device, Constants.StateAttributeTopicId), mqttClient.WillTopic);
            Assert.AreEqual(HomieStates.Lost, mqttClient.WillMessage);
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

            var device = BuildSinglePropertyDevice();

            var homieClient = new HomieClient(device, mqttClient);

            // Act
            var connected = homieClient.Connect();

            // Assert
            Assert.IsTrue(connected);
            Assert.IsTrue(mqttClient.WillFlag);
            Assert.AreEqual(HomieStates.Lost, mqttClient.WillMessage);
        }

        [TestMethod]
        public void HomieClient_Lifecycle_States_Are_Reachable()
        {
            // All six $state values are part of the convention, and the adapter is the
            // only thing that can reach them: the model has no alert state at all -- an
            // alert there is a keyed message, and 'alert' is what this adapter
            // synthesises from a non-empty alert set -- and the model's transition table
            // knows nothing of v4's own rules. So a device app drives its lifecycle
            // through IDeviceProtocol and never through Device.TryChangeState directly.

            // Arrange
            var mqttClient = new MockMqttClient();
            var device = BuildSinglePropertyDevice();
            var homieClient = new HomieClient(device, mqttClient);
            var stateTopic = HomieTopics.Attribute(device, Constants.StateAttributeTopicId);

            homieClient.Connect();

            // Assert -- Connect leaves the device ready
            Assert.AreEqual(HomieStates.Ready, homieClient.HomieState);

            // Act + Assert -- ready -> sleeping -> ready
            homieClient.Sleep();
            Assert.AreEqual(HomieStates.Sleeping, homieClient.HomieState);
            homieClient.Ready();
            Assert.AreEqual(HomieStates.Ready, homieClient.HomieState);

            // Act + Assert -- ready -> alert -> ready, synthesised from the alert set
            homieClient.RaiseAlert("battery", "Battery is low, at 8%");
            Assert.AreEqual(HomieStates.Alert, homieClient.HomieState);
            homieClient.ClearAlert("battery");
            Assert.AreEqual(HomieStates.Ready, homieClient.HomieState);

            // ... and every one of them reached the wire, in order and exactly once.
            var states = mqttClient.PayloadsFor(stateTopic);
            Assert.AreEqual(6, states.Length, $"unexpected $state sequence: {StringUtils.Join(", ", states)}");
            Assert.AreEqual(HomieStates.Init, states[0]);
            Assert.AreEqual(HomieStates.Ready, states[1]);
            Assert.AreEqual(HomieStates.Sleeping, states[2]);
            Assert.AreEqual(HomieStates.Ready, states[3]);
            Assert.AreEqual(HomieStates.Alert, states[4]);
            Assert.AreEqual(HomieStates.Ready, states[5]);
        }

        [TestMethod]
        public void HomieClient_Refuses_An_Illegal_State_Transition()
        {
            // Arrange
            var mqttClient = new MockMqttClient();
            var device = BuildSinglePropertyDevice();
            var homieClient = new HomieClient(device, mqttClient);
            homieClient.Connect();
            homieClient.RaiseAlert("battery", "Battery is low, at 8%");

            var statesBefore = mqttClient.PayloadsFor(HomieTopics.Attribute(device, Constants.StateAttributeTopicId)).Length;

            // Act -- alert may go to ready or disconnected, never straight to sleeping.
            // The rule is the adapter's own: the model's transition table has no alert
            // row to carry it, which is exactly why Sleep() has to be asked rather than
            // Device.TryChangeState.
            homieClient.Sleep();

            // Assert
            Assert.AreEqual(HomieStates.Alert, homieClient.HomieState);
            Assert.AreEqual(
                statesBefore,
                mqttClient.PayloadsFor(HomieTopics.Attribute(device, Constants.StateAttributeTopicId)).Length,
                "a refused transition published something");
        }

        [TestMethod]
        public void HomieClient_Raises_OnCommand_For_A_Controller_Set()
        {
            // property.OnUpdate fires both when a controller sets a value and when the
            // device updates its own, so an actuator cannot act on it. OnCommand fires
            // only for the former.

            // Arrange
            var mqttClient = new MockMqttClient();

            var device = new DeviceBuilder(_testDeviceId, _testDeviceName)
                .AddNode(_testNodeEngineId, _testNodeEngineName, _testNodeEngineType)
                    .AddFloatProperty(_testPropertyIntensityId, _testPropertyIntensityName, 0.0)
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
            SendCommand(mqttClient, property, "73");

            // Assert
            Assert.AreEqual(1, commandCount);
            Assert.IsNotNull(commandedProperty);
            Assert.AreEqual(73.0, property.Value);
        }

        [TestMethod]
        public void HomieClient_Reflects_The_Command_Before_The_Handler_Can_Correct_It()
        {
            // The rule every actuator has to follow, pinned where CI can run it.
            //
            // HomieClient publishes a /set payload onto its property BEFORE OnCommand
            // runs, so a property whose value is a *request* -- one the device can turn
            // down -- is reflected optimistically, and retained. Only the app knows
            // whether the request was honoured, so only the app can correct it, and the
            // correction has to land AFTER that reflection: the library's publish is
            // already out by the time the handler is called, so a device that published
            // its real value first would only have it overwritten.
            //
            // Deliberately the same shape as HomieClientCheck's 'lifecycle' property,
            // which the conformance suite measures on the wire. This is the copy that
            // needs no hardware, so an actuator has a worked example in CI -- see
            // "reflect the outcome, not the command" in CLAUDE.md, and issue #33 for why
            // the library does not take this over.

            // Arrange -- a lifecycle property whose value mirrors $state
            var mqttClient = new MockMqttClient();

            var device = new DeviceBuilder(_testDeviceId, _testDeviceName)
                .AddNode(_testNodeEngineId, _testNodeEngineName, _testNodeEngineType)
                    .AddEnumProperty(_testPropertyLifecycleId, _testPropertyLifecycleName, HomieStates.Ready)
                        .WithSettable(true)
                        .WithOptions(new string[] { HomieStates.Ready, HomieStates.Alert, HomieStates.Sleeping })
                    .BuildProperty(out EnumProperty lifecycle)
                .BuildNode()
                .BuildDevice();

            var homieClient = new HomieClient(device, mqttClient);
            homieClient.Connect();

            homieClient.OnCommand += (args) =>
            {
                // OnCommand is raised for every settable property, so a handler has to
                // select the one it owns before correcting anything. Redundant on this
                // one-property device and kept anyway: this test is cited as the worked
                // example for #11 and #12, and copied without the guard it would publish
                // $state onto whichever property a controller happened to write to.
                if (args.Property.Id != _testPropertyLifecycleId)
                {
                    return;
                }

                var payload = Encoding.UTF8.GetString(args.Payload, 0, args.Payload.Length);
                if (payload == HomieStates.Sleeping)
                {
                    homieClient.Sleep();
                }

                // Applied or refused, this is what the device actually is.
                lifecycle.Update(homieClient.HomieState);
            };

            // alert may only return to ready or disconnect, so the command below is refused
            homieClient.RaiseAlert(_testPropertyLifecycleId, "raised through the lifecycle property");
            Assert.AreEqual(HomieStates.Alert, homieClient.HomieState);

            var propertyTopic = HomieTopics.Of(lifecycle);
            var before = mqttClient.PayloadsFor(propertyTopic).Length;

            // Act -- a controller asks for the one transition the convention forbids
            SendCommand(mqttClient, lifecycle, HomieStates.Sleeping);

            // Assert -- the transition did not happen ...
            Assert.AreEqual(HomieStates.Alert, homieClient.HomieState);

            // ... and the property carries the reflection first, the correction over it.
            // Both, in that order: the reflection proves the command was seen, the
            // correction is what stops the retained store advertising 'sleeping' beside
            // $state='alert'.
            var payloads = mqttClient.PayloadsFor(propertyTopic);
            Assert.AreEqual(before + 2, payloads.Length, $"expected the reflection and the correction over it, saw: {StringUtils.Join(", ", payloads)}");
            Assert.AreEqual(HomieStates.Sleeping, payloads[before], "the library did not reflect the command");
            Assert.AreEqual(HomieStates.Alert, payloads[before + 1], "the device did not publish its real state over the reflection");

            // The last payload on a retained topic is what every later controller reads.
            Assert.AreEqual(HomieStates.Alert, lifecycle.Value);
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
            Assert.AreEqual(HomieStates.Ready, homieClient.HomieState);

            // Act -- the transport dropped and came back underneath us
            mqttClient.RaiseConnectionClosed();
            mqttClient.RaiseConnectionOpened();

            // Assert -- the whole announcement went out again, ending at ready
            Assert.IsTrue(mqttClient.PublishCount > publishCountAfterConnect);
            Assert.AreEqual(publishCountAfterConnect * 2, mqttClient.PublishCount);
            Assert.AreEqual(HomieStates.Ready, homieClient.HomieState);
        }

        [TestMethod]
        public void HomieClient_Publishes_Once_Per_Update_After_A_Reconnect()
        {
            // The reconnect path re-registers the property update handlers, so a
            // careless += would publish every later update twice.

            // Arrange
            var mqttClient = new MockMqttClient();

            var device = BuildSinglePropertyDevice(out FloatProperty property, settable: false);

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
            // the second attempt fired the state-change handler twice, the Connecting
            // branch ran a second time against an already-'ready' device, TryChangeState
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
            Assert.AreEqual(HomieStates.Ready, homieClient.HomieState);

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

            var device = new DeviceBuilder(_testDeviceId, _testDeviceName)
                .AddNode(_testNodeEngineId, _testNodeEngineName, _testNodeEngineType)
                    .AddFloatProperty(_testPropertyIntensityId, _testPropertyIntensityName, 0.0)
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
            SendCommand(mqttClient, property, "55");

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
            Assert.AreEqual(HomieStates.Ready, homieClient.HomieState);
        }

        [TestMethod]
        public void HomieClient_Retried_Connect_Does_Not_Publish_State_For_An_Alert_Between_Attempts()
        {
            // An alert raised while the device is off the broker changes the effective
            // token, and the obvious implementation publishes $state from the alert
            // handler as soon as it does. Nothing may go out then: the session is not up,
            // and on the successful attempt the alert is carried by the announcement's
            // own post-init $state instead. The count is the assertion -- 16 is a single
            // -property device's announcement, and a stray $state would make it 17.

            // Arrange
            var mqttClient = new MockMqttClient { FailNextConnect = true };
            var homieClient = new HomieClient(BuildSinglePropertyDevice(), mqttClient);

            // Act -- before any connect at all, and again between the two attempts
            homieClient.RaiseAlert("battery", "Battery is low, at 8%");
            homieClient.ClearAlert("battery");

            Assert.IsFalse(homieClient.Connect());

            homieClient.RaiseAlert("battery", "Battery is low, at 8%");

            Assert.IsTrue(homieClient.Connect());

            // Assert
            Assert.AreEqual(16, mqttClient.PublishCount, "an alert off the wire published a $state of its own");
            Assert.AreEqual(HomieStates.Alert, homieClient.HomieState);
        }

        [TestMethod]
        public void HomieClient_Connect_Ends_In_Alert_When_The_Device_Is_Already_Alerting()
        {
            // Connect() always asks for Ready as its post-init state -- only a
            // re-announce preserves Sleeping -- so with alerts keyed and separate from
            // the lifecycle, a device that raised one before it ever connected announces
            // itself and then lands on 'alert' rather than 'ready'. The announcement is
            // the same 16 publishes either way; it is the last one that differs.

            // Arrange
            var mqttClient = new MockMqttClient();
            var device = BuildSinglePropertyDevice();
            var homieClient = new HomieClient(device, mqttClient);

            homieClient.RaiseAlert("sensor", "The sensor did not answer");

            // Act
            Assert.IsTrue(homieClient.Connect());

            // Assert
            var states = mqttClient.PayloadsFor(HomieTopics.Attribute(device, Constants.StateAttributeTopicId));
            Assert.AreEqual(2, states.Length, $"unexpected $state sequence: {StringUtils.Join(", ", states)}");
            Assert.AreEqual(HomieStates.Init, states[0]);
            Assert.AreEqual(HomieStates.Alert, states[1]);
            Assert.AreEqual(16, mqttClient.PublishCount);
        }

        [TestMethod]
        public void HomieClient_Publishes_No_Standalone_Init_For_An_Alert_Raised_While_Announcing()
        {
            // 'init' goes out inside the device info block and nowhere else. An app that
            // raises an alert from its own OnDeviceStateChange handler reaches the
            // adapter's alert handler while the model is still Connecting, where the
            // effective token *is* 'init' -- and publishing it there would put a second
            // $state=init on the wire, between the device info and the node blocks.

            // Arrange
            var mqttClient = new MockMqttClient();
            var device = BuildSinglePropertyDevice();
            var homieClient = new HomieClient(device, mqttClient);

            device.OnDeviceStateChange += (args) =>
            {
                if (args.CurrentState == DeviceState.Connecting)
                {
                    homieClient.RaiseAlert("sensor", "The sensor did not answer");
                }
            };

            // Act
            Assert.IsTrue(homieClient.Connect());

            // Assert -- one 'init', and the announcement still ends on the alert
            var states = mqttClient.PayloadsFor(HomieTopics.Attribute(device, Constants.StateAttributeTopicId));
            Assert.AreEqual(2, states.Length, $"unexpected $state sequence: {StringUtils.Join(", ", states)}");
            Assert.AreEqual(HomieStates.Init, states[0]);
            Assert.AreEqual(HomieStates.Alert, states[1]);
            Assert.AreEqual(16, mqttClient.PublishCount);
        }

        [TestMethod]
        public void HomieClient_ReAnnounces_From_Alert_And_Returns_To_Alert()
        {
            // A broker restart while the device is alerting has to re-announce like any
            // other -- Device.CanChangeState used to forbid Alert -> Init, so the device
            // stayed invisible to the fresh broker -- and it must come back to 'alert'
            // rather than being quietly cleared to 'ready'. Now that alerts are keyed and
            // the lifecycle state underneath is plain Ready, nothing in the re-announce
            // touches the alert set, and the post-init token is recomputed from it.

            // Arrange
            var mqttClient = new MockMqttClient();
            var homieClient = new HomieClient(BuildSinglePropertyDevice(), mqttClient);
            homieClient.Connect();

            homieClient.RaiseAlert("battery", "Battery is low, at 8%");
            Assert.AreEqual(HomieStates.Alert, homieClient.HomieState);

            var beforeReconnect = mqttClient.PublishCount;

            // Act -- the broker goes away and comes back
            mqttClient.RaiseConnectionClosed();
            mqttClient.RaiseConnectionOpened();

            // Assert
            Assert.IsTrue(mqttClient.PublishCount > beforeReconnect, "the device did not re-announce from 'alert'");
            Assert.AreEqual(HomieStates.Alert, homieClient.HomieState, "the re-announce cleared the alert");
        }

        [TestMethod]
        public void HomieClient_ReAnnounces_From_Sleeping_And_Returns_To_Sleeping()
        {
            // The other half of the same rule, and the one Connect() deliberately does
            // not share: a re-announce preserves Sleeping, a fresh Connect() always
            // targets Ready. A broker restart is not a reason to wake a sleeping device.

            // Arrange
            var mqttClient = new MockMqttClient();
            var device = BuildSinglePropertyDevice();
            var homieClient = new HomieClient(device, mqttClient);
            homieClient.Connect();
            homieClient.Sleep();

            var stateTopic = HomieTopics.Attribute(device, Constants.StateAttributeTopicId);
            var before = mqttClient.PayloadsFor(stateTopic).Length;

            // Act
            mqttClient.RaiseConnectionClosed();
            mqttClient.RaiseConnectionOpened();

            // Assert
            var states = mqttClient.PayloadsFor(stateTopic);
            Assert.AreEqual(before + 2, states.Length, $"unexpected $state sequence: {StringUtils.Join(", ", states)}");
            Assert.AreEqual(HomieStates.Init, states[before]);
            Assert.AreEqual(HomieStates.Sleeping, states[before + 1]);
            Assert.AreEqual(HomieStates.Sleeping, homieClient.HomieState);
        }

        [TestMethod]
        public void HomieClient_Disconnect_Closes_The_Session_When_The_Transition_Is_Refused()
        {
            // Disconnecting -> Disconnecting is not a legal transition, so a second
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

            var publishesBefore = mqttClient.PublishCount;

            // Act -- the device is already 'disconnected', so the transition is refused
            homieClient.Disconnect();

            // Assert -- the teardown is unconditional, the publish is not: $state comes
            // from the state-change handler of an accepted transition, so a refused one
            // must leave nothing on the wire.
            Assert.IsFalse(mqttClient.IsConnected, "Disconnect() left the MQTT session open");
            Assert.AreEqual(publishesBefore, mqttClient.PublishCount, "a refused transition published $state");
        }

        [TestMethod]
        public void HomieClient_Disconnect_Publishes_Nothing_When_It_Never_Connected()
        {
            // A device that has never connected is already 'disconnecting' in the model,
            // so this is the refused path again -- reached without a single publish
            // having happened, which is the case where a stray $state=disconnected would
            // be the only thing a controller ever heard from this device.

            // Arrange
            var mqttClient = new MockMqttClient();
            var homieClient = new HomieClient(BuildSinglePropertyDevice(), mqttClient);

            // A session someone else opened: the teardown still has to close it.
            mqttClient.Connect("someone-else");

            // Act
            homieClient.Disconnect();

            // Assert
            Assert.AreEqual(0, mqttClient.PublishCount, "a device that never announced published $state");
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
            var settings = new HomieClientSettings { KeepAlivePeriod = 30 };
            var homieClient = new HomieClient(BuildSinglePropertyDevice(), mqttClient, settings);

            // Act
            homieClient.Connect();

            // Assert
            Assert.AreEqual(_testDeviceId, mqttClient.ConnectedClientId);
            Assert.AreEqual((ushort)30, mqttClient.KeepAlivePeriod, "the caller's own setting was discarded");
        }

        [TestMethod]
        public void HomieClient_Publishes_Nothing_For_A_Rejected_Payload()
        {
            // The refusal has to leave no trace at the broker. A rejected payload that
            // was still reflected would be worse than accepting it: the retained store
            // would advertise a value the device does not hold, to every controller that
            // connects afterwards.
            var mqttClient = new MockMqttClient();
            var device = new DeviceBuilder(_testDeviceId, _testDeviceName)
                .AddNode(_testNodeEngineId, _testNodeEngineName, _testNodeEngineType)
                    .AddIntegerProperty("integer-value", "Integer", 7)
                        .WithSettable(true)
                        .WithFormat("0:100")
                    .BuildProperty(out IntegerProperty property)
                .BuildNode()
                .BuildDevice();

            new HomieClient(device, mqttClient).Connect();

            var publishesBefore = mqttClient.PublishCount;
            var payloadsBefore = mqttClient.PayloadsFor(HomieTopics.Of(property)).Length;

            SendCommand(mqttClient, property, "9000");

            Assert.AreEqual(publishesBefore, mqttClient.PublishCount, "a rejected payload published something");
            Assert.AreEqual(payloadsBefore, mqttClient.PayloadsFor(HomieTopics.Of(property)).Length);
        }

        [TestMethod]
        public void HomieClient_Does_Not_Raise_OnCommand_For_A_Rejected_Payload()
        {
            // The decision recorded here: a payload the library refused is not handed to
            // the app. HandleIncomingMessage's standing rule -- "a command that failed to
            // apply is still a command the app should hear about" -- is about a command
            // that threw while being applied, typically from the reflection publish on a
            // flaky link. That command did reach the device. A payload that violates the
            // property's own datatype or declared format never did, and raising it would
            // leave every actuator re-checking the format its property already declares.
            var mqttClient = new MockMqttClient();
            var device = new DeviceBuilder(_testDeviceId, _testDeviceName)
                .AddNode(_testNodeEngineId, _testNodeEngineName, _testNodeEngineType)
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
        public void HomieClient_Connect_Disconnect()
        {
            // Arrange
            int expectedPublishCountAfterConnect = 23;
            int expectedPublishCountAfterDisconnect = 24;   // +1: $state=disconnected
            int expectedSubscriptionCountConnected = 2;
            int expectedSubscriptionCountDisconnected = 0;

            var mqttClient = new MockMqttClient();

            var device = new DeviceBuilder(_testDeviceId, _testDeviceName)
                .AddNode(_testNodeEngineId, _testNodeEngineName, _testNodeEngineType)
                    .AddFloatProperty(_testPropertyTemperatureId, _testPropertyTemperatureName, 0.0)
                        .WithSettable(true)
                    .BuildProperty()
                    .AddFloatProperty(_testPropertyIntensityId, _testPropertyIntensityName, 100.0)
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
            Assert.AreEqual(HomieStates.Disconnected, mqttClient.PayloadsFor(HomieTopics.Attribute(device, Constants.StateAttributeTopicId))[2]);
        }

        private static Device BuildSinglePropertyDevice()
            => BuildSinglePropertyDevice(out FloatProperty _, settable: false);

        private static Device BuildSinglePropertyDevice(out FloatProperty property, bool settable)
        {
            return new DeviceBuilder(_testDeviceId, _testDeviceName)
                .AddNode(_testNodeEngineId, _testNodeEngineName, _testNodeEngineType)
                    .AddFloatProperty(_testPropertyTemperatureId, _testPropertyTemperatureName, 0.0)
                        .WithSettable(settable)
                    .BuildProperty(out property)
                .BuildNode()
                .BuildDevice();
        }

        // A command travels the same path it does in production: the /set topic the
        // adapter subscribed, HandleIncomingMessage, then the property.
        private static void SendCommand(MockMqttClient mqttClient, PropertyBase property, string payload)
        {
            mqttClient.RaisePublishReceived(
                new MqttMsgPublishEventArgs(
                    HomieTopics.Command(property),
                    Encoding.UTF8.GetBytes(payload),
                    false,
                    MqttQoSLevel.AtLeastOnce,
                    false));
        }
    }
}
