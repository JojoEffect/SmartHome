using SmartHome.Mqtt;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using nanoFramework.M2Mqtt;
using nanoFramework.M2Mqtt.Messages;
using nanoFramework.TestFramework;
using System;
using System.Text;
using System.Threading;

namespace SmartHome.UnitTests
{
    // The auto-reconnect wrapper, driven through a fake transport.
    //
    // Until IMqttTransport existed these tests could not: the wrapper had to hold a
    // concrete MqttClient, because the two events it reacts to most --
    // ConnectionClosedRequest and ConnectionOpened -- are declared on that class and not
    // on the IMqttClient interface it implements. So every claim below stood on one
    // integration test (MqttReconnectCheck) that needs a device, a network and a broker,
    // and is therefore never run by CI.
    //
    // What the fake buys beyond running in CI is the failures a broker cannot be asked
    // for. A real outage can be arranged; a SUBSCRIBE that throws while the connection is
    // healthy cannot, and that is precisely the shape of the bug the reconnect loop's
    // guard exists for -- a throw out of the resubscribe used to leave a live connection
    // with nothing subscribed, so publishing kept working and every inbound command was
    // silently dropped until the device was rebooted.
    //
    // These are the first tests in this suite to start a thread: the wrapper reconnects on
    // one, so nothing here can be asserted synchronously. Each waits on an observable
    // count with a timeout rather than sleeping a guessed interval, and each tears its
    // wrapper down so no reconnect thread outlives the test that made it.
    [TestClass]
    public class ReconnectingMqttClientTests
    {
        private const string _clientId = "super-car";

        // 25ms is well under the cost of the work being waited for and well over the
        // scheduler's granularity on the virtual device.
        private const int _pollIntervalMs = 25;

        // Generous on purpose: ReconnectingMqttClient.ReconnectDelayMs is a private const
        // 5s, so any test that makes an attempt fail pays one of those before the next
        // attempt. Injecting that delay would buy ~5s per test at the price of a
        // production knob that exists only for tests, which is the wrong trade for a
        // suite this size.
        private const int _reconnectTimeoutMs = 20_000;

        // Long enough for a reconnect thread to reach CONNECT if one was ever going to
        // start -- ReconnectHandler creates and starts it inline, before returning.
        private const int _settleMs = 500;

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
        public void A_Null_Transport_Is_Refused_By_The_Constructor()
        {
            // The field is only dereferenced once a caller does something -- IsConnected,
            // Connect, an event subscription -- so without this guard a null arrives as a
            // NullReferenceException thrown from inside the wrapper, at a call site that
            // looks unrelated to the constructor that caused it.
            Assert.ThrowsException(
                typeof(ArgumentNullException),
                () => new ReconnectingMqttClient((IMqttTransport)null!));
        }

        [TestMethod]
        public void Connect_Without_Parameters_Keeps_The_Wrapped_Clients_Own_KeepAlive_Default()
        {
            // Arrange
            var transport = new MockMqttClient();
            var client = new ReconnectingMqttClient(transport);

            // Act
            client.Connect(_clientId);

            // Assert
            //
            // 60s, matching MqttMsgConnect.KEEP_ALIVE_PERIOD_DEFAULT in the wrapped
            // client. This overload mirrors MqttClient.Connect(string), so it has to
            // mirror its default too: it once passed 5, which had the keep-alive thread
            // ping every 5s and allow the broker 5s to answer before declaring the
            // connection dead -- a disconnect no broker ever saw, on the one test that
            // measures reconnects.
            Assert.AreEqual(60, (int)transport.KeepAlivePeriod);
            Assert.AreEqual(_clientId, transport.ConnectedClientId);
            Assert.IsTrue(transport.CleanSession);
            Assert.IsFalse(transport.WillFlag);

            client.Disconnect();
        }

        [TestMethod]
        public void A_Reconnect_Replays_Every_Parameter_Of_The_Original_Connect()
        {
            // Arrange
            var transport = new MockMqttClient();
            var client = new ReconnectingMqttClient(transport);

            client.Connect(
                _clientId,
                "super-user",
                "super-secret",
                willRetain: true,
                MqttQoSLevel.AtLeastOnce,
                willFlag: true,
                "will/topic",
                "lost",
                cleanSession: false,
                keepAlivePeriod: 42);

            Assert.AreEqual(1, transport.ConnectCallCount);

            // Act
            transport.RaiseConnectionClosed();

            // Assert
            Assert.IsTrue(
                WaitForConnectCount(transport, 2, _reconnectTimeoutMs),
                "the wrapper never reconnected");

            // A session is not restored by reconnecting alone -- these are the parameters
            // that make it the *same* session. The client id above all: a reconnect under
            // a fresh id leaves the dead session's last will to fire against a device that
            // has already announced itself healthy again.
            Assert.AreEqual(_clientId, transport.ConnectedClientId);
            Assert.AreEqual("super-user", transport.ConnectedUsername);
            Assert.AreEqual("super-secret", transport.ConnectedPassword);
            Assert.IsTrue(transport.WillFlag);
            Assert.AreEqual("will/topic", transport.WillTopic);
            Assert.AreEqual("lost", transport.WillMessage);
            Assert.IsTrue(transport.WillRetain);
            Assert.AreEqual((int)MqttQoSLevel.AtLeastOnce, (int)transport.WillQosLevel);
            Assert.IsFalse(transport.CleanSession);
            Assert.AreEqual(42, (int)transport.KeepAlivePeriod);

            client.Disconnect();
        }

        [TestMethod]
        public void Connect_Twice_Attaches_The_Reconnect_Handler_Once()
        {
            // Arrange
            var transport = new MockMqttClient();
            var client = new ReconnectingMqttClient(transport);

            // Act
            client.Connect(_clientId);
            client.Connect(_clientId);

            // Assert
            //
            // Asserted on the handler count rather than on the number of reconnects,
            // because the number of reconnects cannot show this: ReconnectHandler refuses
            // to start a second reconnect while one is already running, so a duplicated
            // handler produces a duplicated reconnect only when the first has already
            // finished. That is a race, and a test built on it would pass for the wrong
            // reason most of the time.
            Assert.AreEqual(1, transport.ConnectionClosedHandlerCount);
            Assert.AreEqual(1, transport.ConnectionClosedRequestHandlerCount);

            client.Disconnect();
        }

        [TestMethod]
        public void A_Peer_Initiated_Close_Also_Triggers_A_Reconnect()
        {
            // Arrange
            var transport = new MockMqttClient();
            var client = new ReconnectingMqttClient(transport);

            client.Connect(_clientId);

            // Act
            //
            // ConnectionClosedRequest, not ConnectionClosed: M2Mqtt raises this one when
            // the broker sends DISCONNECT rather than the socket simply dying. It is one
            // of the two events absent from IMqttClient, so this path is the reason the
            // wrapper could not be given an interface to hold in the first place -- and
            // it had no coverage of any kind, including on hardware, because the
            // integration test takes the broker away rather than asking it to say
            // goodbye.
            transport.RaiseConnectionClosedRequest();

            // Assert
            Assert.IsTrue(
                WaitForConnectCount(transport, 2, _reconnectTimeoutMs),
                "a peer-initiated close did not start a reconnect");
            Assert.IsTrue(transport.IsConnected);

            client.Disconnect();
        }

        [TestMethod]
        public void A_Reconnect_Replays_Every_Subscription_With_Its_Own_QoS()
        {
            // Arrange
            var transport = new MockMqttClient();
            var client = new ReconnectingMqttClient(transport);

            client.Connect(_clientId);
            client.Subscribe(
                new string[] { "homie/super-car/engine/intensity/set", "homie/super-car/engine/lifecycle/set" },
                new MqttQoSLevel[] { MqttQoSLevel.AtLeastOnce, MqttQoSLevel.AtMostOnce });

            Assert.AreEqual(1, transport.SubscribeCallCount);

            // Act
            transport.RaiseConnectionClosed();

            // Assert
            Assert.IsTrue(
                WaitForSubscribeCount(transport, 2, _reconnectTimeoutMs),
                "the wrapper reconnected without replaying its subscriptions");

            // Per topic, not by position: the cache is a Hashtable, so replay order is
            // unspecified and asserting on it would be asserting on an implementation
            // detail the wrapper never promised.
            AssertSubscribedAt(transport, "homie/super-car/engine/intensity/set", MqttQoSLevel.AtLeastOnce);
            AssertSubscribedAt(transport, "homie/super-car/engine/lifecycle/set", MqttQoSLevel.AtMostOnce);

            // The QoS matters as much as the topic. A /set topic replayed at QoS 0 leaves
            // the broker free to drop a controller's command, which looks exactly like a
            // device ignoring it.
            Assert.AreEqual(2, transport.SubscribedTopics.Length);

            client.Disconnect();
        }

        [TestMethod]
        public void An_Unsubscribed_Topic_Is_Not_Replayed()
        {
            // Arrange
            var transport = new MockMqttClient();
            var client = new ReconnectingMqttClient(transport);

            client.Connect(_clientId);
            client.Subscribe(
                new string[] { "kept/set", "dropped/set" },
                new MqttQoSLevel[] { MqttQoSLevel.AtLeastOnce, MqttQoSLevel.AtLeastOnce });
            client.Unsubscribe(new string[] { "dropped/set" });

            // Act
            transport.RaiseConnectionClosed();

            // Assert
            Assert.IsTrue(
                WaitForSubscribeCount(transport, 2, _reconnectTimeoutMs),
                "the wrapper reconnected without replaying its subscriptions");

            Assert.AreEqual(1, transport.SubscribedTopics.Length);
            AssertSubscribedAt(transport, "kept/set", MqttQoSLevel.AtLeastOnce);

            client.Disconnect();
        }

        [TestMethod]
        public void A_Reconnect_With_Nothing_Subscribed_Sends_No_Subscribe()
        {
            // Arrange
            var transport = new MockMqttClient();
            var client = new ReconnectingMqttClient(transport);

            client.Connect(_clientId);

            // Act
            transport.RaiseConnectionClosed();

            // Assert
            Assert.IsTrue(
                WaitForConnectCount(transport, 2, _reconnectTimeoutMs),
                "the wrapper never reconnected");

            Thread.Sleep(_settleMs);

            // An empty SUBSCRIBE is not a harmless no-op: MqttMsgSubscribe with no topics
            // is a protocol error, and the wrapped client would build and queue it
            // without complaint.
            Assert.AreEqual(0, transport.SubscribeCallCount);

            client.Disconnect();
        }

        [TestMethod]
        public void A_Throw_Out_Of_The_Resubscribe_Is_Retried_Rather_Than_Read_As_Success()
        {
            // Arrange
            var transport = new MockMqttClient();
            var client = new ReconnectingMqttClient(transport);

            client.Connect(_clientId);
            client.Subscribe(new string[] { "kept/set" }, new MqttQoSLevel[] { MqttQoSLevel.AtLeastOnce });

            // The failure no broker can be asked for, and the reason the reconnect loop's
            // condition is `_autoReconnectEnabled` rather than `!IsConnected`. With the
            // older guard, the throw below was caught, slept on, and then re-read as
            // "connected, nothing left to do" -- so the thread exited leaving a live
            // connection with no subscriptions. Publishing carried on working, every
            // inbound command was dropped, and the only evidence anywhere was a single
            // LogWarning.
            transport.FailNextSubscribe = true;

            // Act
            transport.RaiseConnectionClosed();

            // Assert
            Assert.IsTrue(
                WaitForSubscribeCount(transport, 2, _reconnectTimeoutMs),
                "a failed resubscribe was never retried");

            AssertSubscribedAt(transport, "kept/set", MqttQoSLevel.AtLeastOnce);
            Assert.IsTrue(transport.IsConnected);

            client.Disconnect();
        }

        [TestMethod]
        public void A_Failed_Connect_Is_Retried_Until_It_Succeeds()
        {
            // Arrange
            var transport = new MockMqttClient();
            var client = new ReconnectingMqttClient(transport);

            client.Connect(_clientId);
            transport.FailNextConnect = true;

            // Act
            transport.RaiseConnectionClosed();

            // Assert
            //
            // Three CONNECTs: the caller's, the attempt that fails, and the retry that
            // takes. A wrapper that gave up after one failure leaves a device that looks
            // alive and reaches no broker until it is rebooted, which is indistinguishable
            // from a network fault from the outside.
            Assert.IsTrue(
                WaitForConnectCount(transport, 3, _reconnectTimeoutMs),
                "a failed reconnect attempt was not retried");
            Assert.IsTrue(transport.IsConnected);

            client.Disconnect();
        }

        [TestMethod]
        public void Disconnect_Disarms_AutoReconnect()
        {
            // Arrange
            var transport = new MockMqttClient();
            var client = new ReconnectingMqttClient(transport);

            client.Connect(_clientId);

            // Act
            client.Disconnect();

            // Assert
            //
            // The fake raises ConnectionClosed from Disconnect(), as the real client does
            // by way of OnConnectionClosing(). So this is not a hypothetical: without the
            // wrapper detaching its handler first, a caller's own Disconnect() would
            // immediately arrange its own reconnect.
            Assert.AreEqual(0, transport.ConnectionClosedHandlerCount);
            Assert.AreEqual(0, transport.ConnectionClosedRequestHandlerCount);
            Assert.AreEqual(1, transport.DisconnectCallCount);

            // And a later close, from whatever source, still starts nothing.
            transport.RaiseConnectionClosed();
            Thread.Sleep(_settleMs);

            Assert.AreEqual(1, transport.ConnectCallCount);
            Assert.IsFalse(transport.IsConnected);
        }

        [TestMethod]
        public void Close_Disarms_AutoReconnect()
        {
            // Arrange
            var transport = new MockMqttClient();
            var client = new ReconnectingMqttClient(transport);

            client.Connect(_clientId);

            // Act
            client.Close();

            // Assert
            //
            // Close() needs the same teardown as Disconnect() and for the same reason:
            // MqttClient.Close() drops the channel, and its receive thread reports that as
            // ConnectionClosed -- so with the handler still attached, the wrapper would
            // reopen the very session the caller just closed.
            Assert.AreEqual(1, transport.CloseCallCount);
            Assert.AreEqual(0, transport.ConnectionClosedHandlerCount);
            Assert.AreEqual(0, transport.ConnectionClosedRequestHandlerCount);

            transport.RaiseConnectionClosed();
            Thread.Sleep(_settleMs);

            Assert.AreEqual(1, transport.ConnectCallCount);
        }

        [TestMethod]
        public void Disconnect_Clears_The_Replay_Cache()
        {
            // Arrange
            var transport = new MockMqttClient();
            var client = new ReconnectingMqttClient(transport);

            client.Connect(_clientId);
            client.Subscribe(new string[] { "stale/set" }, new MqttQoSLevel[] { MqttQoSLevel.AtLeastOnce });
            client.Disconnect();

            // A fresh session on the same wrapper. Its subscriptions are the new caller's
            // to declare -- replaying the previous session's would have the device
            // receiving commands for topics nobody asked for this time round, and would
            // do it on a connection the caller believes it has just set up from scratch.
            client.Connect(_clientId);

            var subscribesBefore = transport.SubscribeCallCount;

            // Act
            transport.RaiseConnectionClosed();

            // Assert
            Assert.IsTrue(
                WaitForConnectCount(transport, 3, _reconnectTimeoutMs),
                "the wrapper never reconnected after the second Connect");

            Thread.Sleep(_settleMs);

            Assert.AreEqual(subscribesBefore, transport.SubscribeCallCount);

            client.Disconnect();
        }

        [TestMethod]
        public void A_Disconnect_Racing_A_Reconnect_Replays_Nothing()
        {
            // Arrange
            var transport = new MockMqttClient();
            var client = new ReconnectingMqttClient(transport);

            client.Connect(_clientId);
            client.Subscribe(new string[] { "kept/set" }, new MqttQoSLevel[] { MqttQoSLevel.AtLeastOnce });

            var subscribesBefore = transport.SubscribeCallCount;

            // Park the reconnect inside CONNECT, so Disconnect() below runs while a
            // reconnect is genuinely in flight rather than before or after one.
            transport.BlockConnect();

            // Act
            transport.RaiseConnectionClosed();

            Assert.IsTrue(
                transport.WaitForConnectEntered(_reconnectTimeoutMs),
                "the reconnect never reached CONNECT");

            client.Disconnect();
            transport.ReleaseConnect();

            // Assert
            //
            // Two mechanisms can stop the parked reconnect from finishing its work:
            // Teardown() aborts the thread, and the loop re-checks _autoReconnectEnabled
            // after the connect returns and closes the session it just opened. Which of
            // them gets there first is a race and is deliberately not asserted. What is
            // asserted is the invariant both exist to hold -- a caller that has
            // disconnected does not end up with a session carrying replayed
            // subscriptions, which for a session with a last will means a will firing
            // against a device the app believes it shut down.
            Thread.Sleep(_settleMs);

            Assert.AreEqual(subscribesBefore, transport.SubscribeCallCount);
            Assert.IsFalse(transport.IsConnected);
        }

        [TestMethod]
        public void Init_Publish_And_IsConnected_Reach_The_Transport_Unchanged()
        {
            // Arrange
            var transport = new MockMqttClient();
            var client = new ReconnectingMqttClient(transport);

            var payload = Encoding.UTF8.GetBytes("21.5");

            // Act
            client.Init("broker.invalid", 1883, false, null, null, MqttSslProtocols.None);

            Assert.IsFalse(client.IsConnected);

            client.Connect(_clientId);

            client.Publish("homie/super-car/engine/temperature", payload);
            client.Publish("homie/super-car/engine/temperature", payload, null);
            client.Publish("homie/super-car/engine/temperature", payload, null, null, MqttQoSLevel.AtLeastOnce, retain: true);

            // Assert
            //
            // The wrapper adds nothing to these -- it caches connect parameters and
            // subscriptions, and everything else is pass-through. Worth pinning anyway:
            // all three Publish overloads have to arrive, because a publish that silently
            // took a different overload would change the retain flag and QoS on the wire
            // while every count in every other test stayed the same.
            Assert.AreEqual("broker.invalid", transport.InitBrokerHostName);
            Assert.AreEqual(3, transport.PublishCount);
            Assert.IsTrue(client.IsConnected);

            client.Disconnect();

            Assert.IsFalse(client.IsConnected);
        }

        /// <summary>
        /// Waits until the transport has seen at least <paramref name="minimum"/> CONNECTs.
        /// </summary>
        private static bool WaitForConnectCount(MockMqttClient transport, int minimum, int timeoutMs)
        {
            var waited = 0;

            while (transport.ConnectCallCount < minimum)
            {
                if (waited >= timeoutMs)
                {
                    return false;
                }

                Thread.Sleep(_pollIntervalMs);
                waited += _pollIntervalMs;
            }

            return true;
        }

        /// <summary>
        /// Waits until the transport has seen at least <paramref name="minimum"/> SUBSCRIBEs.
        /// </summary>
        private static bool WaitForSubscribeCount(MockMqttClient transport, int minimum, int timeoutMs)
        {
            var waited = 0;

            while (transport.SubscribeCallCount < minimum)
            {
                if (waited >= timeoutMs)
                {
                    return false;
                }

                Thread.Sleep(_pollIntervalMs);
                waited += _pollIntervalMs;
            }

            return true;
        }

        /// <summary>
        /// Asserts the last SUBSCRIBE carried <paramref name="topic"/> at
        /// <paramref name="qosLevel"/>, wherever in the packet it landed.
        /// </summary>
        private static void AssertSubscribedAt(MockMqttClient transport, string topic, MqttQoSLevel qosLevel)
        {
            var topics = transport.SubscribedTopics;
            var found = -1;

            for (int i = 0; i < topics.Length; i++)
            {
                if (topics[i] == topic)
                {
                    found = i;
                    break;
                }
            }

            Assert.IsTrue(found >= 0, $"{topic} was not in the SUBSCRIBE");
            Assert.AreEqual(
                (int)qosLevel,
                (int)transport.SubscribedQosLevels[found],
                $"{topic} was replayed at the wrong QoS");
        }
    }
}
