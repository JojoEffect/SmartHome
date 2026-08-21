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

        public event IMqttClient.MqttMsgPublishEventHandler MqttMsgPublishReceived;
        public event IMqttClient.MqttMsgPublishedEventHandler MqttMsgPublished;
        public event IMqttClient.MqttMsgSubscribedEventHandler MqttMsgSubscribed;
        public event IMqttClient.MqttMsgUnsubscribedEventHandler MqttMsgUnsubscribed;
        public event IMqttClient.ConnectionClosedEventHandler ConnectionClosed;
        public event MqttClient.ConnectionOpenedEventHandler ConnectionOpened;

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
            return MqttReasonCode.Success;
        }

        public MqttReasonCode Connect(string clientId, string username, string password, bool willRetain, MqttQoSLevel willQosLevel, bool willFlag, string willTopic, string willMessage, bool cleanSession, ushort keepAlivePeriod)
        {
            return MqttReasonCode.Success;
        }

        public void Disconnect()
        {
            return;
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
