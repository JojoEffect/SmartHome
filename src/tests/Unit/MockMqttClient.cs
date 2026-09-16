using SmartHome.Mqtt;
using nanoFramework.M2Mqtt;
using nanoFramework.M2Mqtt.Messages;
using System;
using System.Collections;
using System.Text;

namespace SmartHome.UnitTests
{
    internal class MockMqttClient : IReconnectingMqttClient
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

        public int PublishCount { get; private set; } = 0;
        public int SubscriptionCount { get; private set; } = 0;

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

        // Captured from the last CONNECT so tests can assert on what the Homie client
        // actually declares -- the last will above all, which Homie v4 requires and
        // which is only ever visible in CONNECT.
        public string ConnectedClientId { get; private set; }

        public bool WillFlag { get; private set; }

        public string WillTopic { get; private set; }

        public string WillMessage { get; private set; }

        public bool WillRetain { get; private set; }

        public ushort KeepAlivePeriod { get; private set; }

        public event IMqttClient.MqttMsgPublishEventHandler MqttMsgPublishReceived;
        public event IMqttClient.MqttMsgPublishedEventHandler MqttMsgPublished;
        public event IMqttClient.MqttMsgSubscribedEventHandler MqttMsgSubscribed;
        public event IMqttClient.MqttMsgUnsubscribedEventHandler MqttMsgUnsubscribed;
        public event IMqttClient.ConnectionClosedEventHandler ConnectionClosed;
        public event MqttClient.ConnectionOpenedEventHandler ConnectionOpened;

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

        public void RaisePublishReceived(MqttMsgPublishEventArgs eventArgs)
        {
            MqttMsgPublishReceived?.Invoke(this, eventArgs);
        }

        public void Close()
        {
            throw new NotImplementedException();
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
            ConnectedClientId = clientId;
            WillFlag = false;
            IsConnected = true;
            ConnectionOpened?.Invoke(this, null);
            return MqttReasonCode.Success;
        }

        public MqttReasonCode Connect(string clientId, string username, string password, bool willRetain, MqttQoSLevel willQosLevel, bool willFlag, string willTopic, string willMessage, bool cleanSession, ushort keepAlivePeriod)
        {
            if (FailNextConnect)
            {
                FailNextConnect = false;
                IsConnected = false;
                return MqttReasonCode.UnspecifiedError;
            }

            ConnectedClientId = clientId;
            WillFlag = willFlag;
            WillTopic = willTopic;
            WillMessage = willMessage;
            WillRetain = willRetain;
            KeepAlivePeriod = keepAlivePeriod;
            IsConnected = true;
            ConnectionOpened?.Invoke(this, null);
            return MqttReasonCode.Success;
        }

        public void Disconnect()
        {
            var wasConnected = IsConnected;
            IsConnected = false;

            if (wasConnected)
            {
                ConnectionClosed?.Invoke(this, System.EventArgs.Empty);
            }
        }

        public void Init(string brokerHostName, int brokerPort, bool secure, byte[] caCert, byte[] clientCert, MqttSslProtocols sslProtocol)
        {
            throw new NotImplementedException();
        }

        public ushort Publish(string topic, byte[] message, string contentType, ArrayList userProperties, MqttQoSLevel qosLevel, bool retain)
        {
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
