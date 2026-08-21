using SmartHome.Homie.V4;
using SmartHome.Mqtt;
using nanoFramework.M2Mqtt;
using nanoFramework.M2Mqtt.Messages;
using System;
using System.Collections;

namespace SmartHome.UnitTests
{
    internal class MockMqttClient : IReconnectingMqttClient
    {
        public int PublishCount { get; private set; } = 0;
        public int SubscriptionCount { get; private set; } = 0;

        public bool IsConnected { get; private set; } = false;

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

        public MqttReasonCode Connect(string clientId)
        {
            ConnectedClientId = clientId;
            WillFlag = false;
            IsConnected = true;
            return MqttReasonCode.Success;
        }

        public MqttReasonCode Connect(string clientId, string username, string password, bool willRetain, MqttQoSLevel willQosLevel, bool willFlag, string willTopic, string willMessage, bool cleanSession, ushort keepAlivePeriod)
        {
            ConnectedClientId = clientId;
            WillFlag = willFlag;
            WillTopic = willTopic;
            WillMessage = willMessage;
            WillRetain = willRetain;
            KeepAlivePeriod = keepAlivePeriod;
            IsConnected = true;
            return MqttReasonCode.Success;
        }

        public void Disconnect()
        {
            IsConnected = false;
        }

        public void Init(string brokerHostName, int brokerPort, bool secure, byte[] caCert, byte[] clientCert, MqttSslProtocols sslProtocol)
        {
            throw new NotImplementedException();
        }

        public ushort Publish(string topic, byte[] message, string contentType, ArrayList userProperties, MqttQoSLevel qosLevel, bool retain)
        {
            PublishCount++;
            return 0;
        }

        public ushort Publish(string topic, byte[] message, string contentType)
        {
            PublishCount++;
            return 0;
        }

        public ushort Publish(string topic, byte[] message)
        {
            PublishCount++;
            return 0;
        }

        public ushort Subscribe(string[] topics, MqttQoSLevel[] qosLevels)
        {
            foreach (var _ in topics)
            {
                SubscriptionCount++;
            }
            return 0;
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
