using SmartHome.Mqtt;
using nanoFramework.M2Mqtt;
using nanoFramework.M2Mqtt.Messages;
using System;
using System.Collections;
using System.Text;
using System.Threading;

namespace SmartHome.UnitTests
{
    // Both seams, on purpose. IReconnectingMqttClient is what a consumer of the wrapper
    // depends on (HomieClient takes one), IMqttTransport is what the wrapper itself wraps
    // -- so one double serves the adapter tests above it and the wrapper tests below it,
    // and the two suites cannot drift onto differently-behaved fakes of the same client.
    internal class MockMqttClient : IReconnectingMqttClient, IMqttTransport
    {
        // Every publish, in order, with everything the adapter handed to the transport:
        // topic, payload, retain flag and QoS. PublishCount cannot express an ordering
        // claim, and "reflect the outcome, not the command" is entirely about order -- a
        // device's correction has to land AFTER the library's optimistic reflection, not
        // instead of it, because the library's publish is already out by the time the
        // OnCommand handler runs.
        //
        // Retain and QoS are recorded because they are part of the wire contract and
        // nothing else can see them: an attribute published non-retained vanishes from
        // the broker's store the moment the device disconnects, and the golden wire tests
        // are the only place in CI where that is checked at all.
        private readonly ArrayList _publishes = new ArrayList();

        private string[] _subscribeTopics = new string[0];
        private MqttQoSLevel[] _subscribeQosLevels = new MqttQoSLevel[0];

        // Signalled every time the full Connect() overload is entered, and waited on
        // inside it while the gate is shut. Together they let a test park a reconnect
        // attempt mid-CONNECT and act while it is in flight -- the only way to reach the
        // wrapper's races from outside, since nothing else can hold a CONNECT open.
        private readonly AutoResetEvent _connectEntered = new AutoResetEvent(false);
        private readonly ManualResetEvent _connectGate = new ManualResetEvent(true);

        public int PublishCount { get; private set; } = 0;
        public int SubscriptionCount { get; private set; } = 0;

        /// <summary>
        /// CONNECT packets, not connections. Distinct from <see cref="IsConnected"/>: a
        /// reconnect retried three times before it takes is three CONNECTs and one
        /// connection, and only this number tells that apart from a wrapper that gave up
        /// after the first attempt.
        /// </summary>
        public int ConnectCallCount { get; private set; } = 0;

        /// <summary>
        /// SUBSCRIBE packets, not topics. <see cref="SubscriptionCount"/> counts topics
        /// and so cannot say whether a replay happened at all -- one SUBSCRIBE carrying
        /// two topics and two carrying one each are the same number there.
        /// </summary>
        public int SubscribeCallCount { get; private set; } = 0;

        /// <summary>DISCONNECT packets.</summary>
        public int DisconnectCallCount { get; private set; } = 0;

        /// <summary>Calls to <see cref="Close"/>.</summary>
        public int CloseCallCount { get; private set; } = 0;

        /// <summary>The broker host name of the last <see cref="Init"/>, or null.</summary>
        public string InitBrokerHostName { get; private set; }

        public bool IsConnected { get; private set; } = false;

        /// <summary>
        /// Makes the next Connect() attempt fail, so tests can exercise the retry path
        /// the device apps actually use.
        /// </summary>
        public bool FailNextConnect { get; set; } = false;

        /// <summary>
        /// Makes the next Subscribe() throw. Fails a connect attempt *after* the
        /// connection-change handlers are attached, which FailNextConnect cannot reach --
        /// that path is the one where a re-entrant HandleConnectionOpen could announce
        /// the device a second time.
        /// </summary>
        public bool FailNextSubscribe { get; set; } = false;

        /// <summary>
        /// Makes the next Publish() throw, the way a QoS 1 publish does whenever the link
        /// is momentarily down.
        /// </summary>
        /// <remarks>
        /// One publish, not the connection: this is the failure an adapter has to survive
        /// *without* the session being torn down, which is the case its own guards exist
        /// for and the only way to reach a half-published announcement from a test.
        /// </remarks>
        public bool FailNextPublish { get; set; } = false;

        // Captured from the last CONNECT so tests can assert on what the Homie client
        // actually declares -- the last will above all, which Homie v4 requires and
        // which is only ever visible in CONNECT.
        public string ConnectedClientId { get; private set; }

        public string ConnectedUsername { get; private set; }

        public string ConnectedPassword { get; private set; }

        public bool CleanSession { get; private set; }

        public MqttQoSLevel WillQosLevel { get; private set; }

        public bool WillFlag { get; private set; }

        public string WillTopic { get; private set; }

        public string WillMessage { get; private set; }

        public bool WillRetain { get; private set; }

        public ushort KeepAlivePeriod { get; private set; }

        /// <summary>
        /// How many handlers are attached to <see cref="ConnectionClosed"/>.
        /// </summary>
        /// <remarks>
        /// The only way to see a double registration at all. A wrapper that attaches its
        /// reconnect handler twice still reconnects once -- it guards on whether a
        /// reconnect thread already exists -- so every observable consequence of the
        /// duplicate is racy, and counting the handlers is the one assertion that is not.
        /// </remarks>
        public int ConnectionClosedHandlerCount
            => ConnectionClosed == null ? 0 : ConnectionClosed.GetInvocationList().Length;

        /// <summary>
        /// How many handlers are attached to <see cref="ConnectionClosedRequest"/>.
        /// </summary>
        public int ConnectionClosedRequestHandlerCount
            => ConnectionClosedRequest == null ? 0 : ConnectionClosedRequest.GetInvocationList().Length;

        public event IMqttClient.MqttMsgPublishEventHandler MqttMsgPublishReceived;
        public event IMqttClient.MqttMsgPublishedEventHandler MqttMsgPublished;
        public event IMqttClient.MqttMsgSubscribedEventHandler MqttMsgSubscribed;
        public event IMqttClient.MqttMsgUnsubscribedEventHandler MqttMsgUnsubscribed;
        public event IMqttClient.ConnectionClosedEventHandler ConnectionClosed;
        public event MqttClient.ConnectionOpenedEventHandler ConnectionOpened;

        // The peer-initiated close. M2Mqtt raises this instead of ConnectionClosed when
        // the broker sends DISCONNECT rather than the socket simply dying, and it is one
        // of the two events missing from IMqttClient -- so until IMqttTransport existed,
        // the wrapper's handling of it could not be reached by a test at all.
        public event MqttClient.ConnectionClosedRequestEventHandler ConnectionClosedRequest;

        public void RaiseConnectionClosed()
        {
            IsConnected = false;
            ConnectionClosed?.Invoke(this, System.EventArgs.Empty);
        }

        public void RaiseConnectionOpened()
        {
            IsConnected = true;
            ConnectionOpened?.Invoke(this, null);
        }

        public void RaiseConnectionClosedRequest()
        {
            IsConnected = false;
            ConnectionClosedRequest?.Invoke(this, null);
        }

        /// <summary>
        /// Shuts the gate, so the next full Connect() blocks inside the call until
        /// <see cref="ReleaseConnect"/> opens it again.
        /// </summary>
        public void BlockConnect() => _connectGate.Reset();

        /// <summary>Opens the gate shut by <see cref="BlockConnect"/>.</summary>
        public void ReleaseConnect() => _connectGate.Set();

        /// <summary>Blocks until a Connect() has been entered, or the timeout elapses.</summary>
        public bool WaitForConnectEntered(int timeoutMs) => _connectEntered.WaitOne(timeoutMs, false);

        public void RaisePublishReceived(MqttMsgPublishEventArgs eventArgs)
        {
            MqttMsgPublishReceived?.Invoke(this, eventArgs);
        }

        // Close() reports a closed connection, the way the real client does: it drops the
        // channel and its receive thread then raises ConnectionClosed (MqttClient.Close in
        // the sibling nanoFramework.m2mqtt checkout). That is the whole reason the wrapper
        // disarms auto-reconnect before calling it, so a double that stayed silent here
        // could not show the difference between doing that and not doing it.
        public void Close()
        {
            CloseCallCount++;

            var wasConnected = IsConnected;
            IsConnected = false;

            if (wasConnected)
            {
                ConnectionClosed?.Invoke(this, System.EventArgs.Empty);
            }
        }

        // Connect() and Disconnect() raise their connection events, the way the real
        // client does. This is not cosmetic fidelity: M2Mqtt handles CONNACK on the
        // receive thread and calls OnMqttMsgConnack *before* releasing the handle that
        // MqttClient.Connect() blocks on, so ConnectionOpened handlers have already run
        // by the time Connect() returns. Likewise Disconnect() goes through
        // OnConnectionClosing() to ConnectionClosed.
        //
        // While the mock stayed silent, HomieClient could be re-entered through those
        // handlers in production in ways no test could express -- a retried Connect()
        // announcing once from HandleConnectionOpen and again from Connect() itself.
        public MqttReasonCode Connect(string clientId)
        {
            ConnectCallCount++;
            ConnectedClientId = clientId;
            WillFlag = false;
            IsConnected = true;
            ConnectionOpened?.Invoke(this, null);
            return MqttReasonCode.Success;
        }

        public MqttReasonCode Connect(string clientId, string username, string password, bool willRetain, MqttQoSLevel willQosLevel, bool willFlag, string willTopic, string willMessage, bool cleanSession, ushort keepAlivePeriod)
        {
            ConnectCallCount++;

            // Announce arrival, then park if a test shut the gate. Both ahead of the
            // FailNextConnect check, so a blocked attempt is still counted and still
            // observable -- a test waiting for a CONNECT it never sees cannot tell a slow
            // reconnect from one that was never started.
            _connectEntered.Set();
            _connectGate.WaitOne();

            if (FailNextConnect)
            {
                FailNextConnect = false;
                IsConnected = false;
                return MqttReasonCode.UnspecifiedError;
            }

            ConnectedClientId = clientId;
            ConnectedUsername = username;
            ConnectedPassword = password;
            CleanSession = cleanSession;
            WillFlag = willFlag;
            WillTopic = willTopic;
            WillMessage = willMessage;
            WillRetain = willRetain;
            WillQosLevel = willQosLevel;
            KeepAlivePeriod = keepAlivePeriod;
            IsConnected = true;
            ConnectionOpened?.Invoke(this, null);
            return MqttReasonCode.Success;
        }

        public void Disconnect()
        {
            DisconnectCallCount++;

            var wasConnected = IsConnected;
            IsConnected = false;

            if (wasConnected)
            {
                ConnectionClosed?.Invoke(this, System.EventArgs.Empty);
            }
        }

        public void Init(string brokerHostName, int brokerPort, bool secure, byte[] caCert, byte[] clientCert, MqttSslProtocols sslProtocol)
        {
            InitBrokerHostName = brokerHostName;
        }

        public ushort Publish(string topic, byte[] message, string contentType, ArrayList userProperties, MqttQoSLevel qosLevel, bool retain)
        {
            if (FailNextPublish)
            {
                FailNextPublish = false;
                throw new Exception("Simulated PUBLISH failure.");
            }

            Record(topic, message, retain, qosLevel);
            return 0;
        }

        // The two short overloads record what M2Mqtt 5.1.221 actually does with them
        // rather than "nothing was said": both forward to the six-argument overload with
        // MqttQoSLevel.AtMostOnce and retain false (MqttClient.Publish in the sibling
        // nanoFramework.m2mqtt checkout). So a publish that takes one of these is a
        // fire-and-forget, non-retained publish, and a test asserting the wire contract
        // has to see it as one -- an attribute sent this way would disappear from the
        // broker's store even though the code never named a retain flag.
        public ushort Publish(string topic, byte[] message, string contentType)
        {
            Record(topic, message, false, MqttQoSLevel.AtMostOnce);
            return 0;
        }

        public ushort Publish(string topic, byte[] message)
        {
            Record(topic, message, false, MqttQoSLevel.AtMostOnce);
            return 0;
        }

        public ushort Subscribe(string[] topics, MqttQoSLevel[] qosLevels)
        {
            if (FailNextSubscribe)
            {
                FailNextSubscribe = false;
                throw new Exception("Simulated SUBSCRIBE failure.");
            }

            // Both arrays kept, not just a count. The SUBSCRIBE packet is part of the
            // wire this library is held to: the topics decide which commands reach the
            // device at all, and the QoS decides whether the broker may drop one --
            // subscribing a /set topic at QoS 0 loses a controller's command silently.
            // A counter can express neither, and until this recorded them the only thing
            // standing behind either was a hardware run.
            _subscribeTopics = topics;
            _subscribeQosLevels = qosLevels;
            SubscribeCallCount++;

            foreach (var _ in topics)
            {
                SubscriptionCount++;
            }
            return 0;
        }

        /// <summary>The topics of the last SUBSCRIBE, in the order they were sent.</summary>
        public string[] SubscribedTopics => _subscribeTopics;

        /// <summary>The QoS levels of the last SUBSCRIBE, in the same order.</summary>
        public MqttQoSLevel[] SubscribedQosLevels => _subscribeQosLevels;

        /// <summary>
        /// The payloads published to one topic, in the order they were published.
        /// </summary>
        public string[] PayloadsFor(string topic)
        {
            var matches = new ArrayList();

            foreach (PublishedMessage published in _publishes)
            {
                if (published.Topic == topic)
                {
                    matches.Add(published.Payload);
                }
            }

            var payloads = new string[matches.Count];
            for (int i = 0; i < matches.Count; i++)
            {
                payloads[i] = (string)matches[i];
            }

            return payloads;
        }

        /// <summary>
        /// Every publish, in order, as the adapter issued it.
        /// </summary>
        /// <remarks>
        /// A copy, so a test cannot rewrite the record it is asserting against.
        /// </remarks>
        internal PublishedMessage[] Publishes
        {
            get => (PublishedMessage[])_publishes.ToArray(typeof(PublishedMessage));
        }

        // Recorded from all three Publish overloads, so a test cannot miss a publish by
        // which overload the code under test happened to take.
        private void Record(string topic, byte[] message, bool retain, MqttQoSLevel qosLevel)
        {
            PublishCount++;
            _publishes.Add(new PublishedMessage(topic, message, retain, qosLevel));
        }

        internal class PublishedMessage
        {
            internal PublishedMessage(string topic, byte[] message, bool retain, MqttQoSLevel qosLevel)
            {
                Topic = topic;
                Payload = message == null ? string.Empty : Encoding.UTF8.GetString(message, 0, message.Length);
                Retain = retain;
                QosLevel = qosLevel;
            }

            public string Topic { get; }

            public string Payload { get; }

            /// <summary>The retain flag the caller passed, not what a broker would replay.</summary>
            public bool Retain { get; }

            public MqttQoSLevel QosLevel { get; }
        }

        public ushort Unsubscribe(string[] topics)
        {
            foreach (var _ in topics)
            {
                SubscriptionCount--;
            }
            return 0;
        }
    }
}
