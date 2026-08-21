using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using nanoFramework.M2Mqtt;
using nanoFramework.M2Mqtt.Messages;
using System;
using System.Collections;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using static nanoFramework.M2Mqtt.MqttClient;

namespace SmartHome.Mqtt
{
    public class ReconnectingMqttClient : IReconnectingMqttClient
    {
        private readonly MqttClient _mqttCient;
        private readonly ILogger _logger;
        private Thread? _connectThread;

        private readonly IDictionary _subscribedTopics = new Hashtable();

        private bool _autoReconnectEnabled;

        private readonly object _reconnectSync = new();

        // cached connect params for reconnect
        private string? _clientId;
        private string? _username;
        private string? _password;
        private bool _willRetain;
        private MqttQoSLevel _willQosLevel;
        private bool _willFlag;
        private string? _willTopic;
        private string? _willMessage;
        private bool _cleanSession;
        private ushort _keepAlivePeriod;

        public ReconnectingMqttClient(string brokerHostName)
            : this(brokerHostName, 1883, false, null, null, MqttSslProtocols.None)
        {
        }

        public ReconnectingMqttClient(string brokerHostName, int brokerPort, bool secure, X509Certificate? caCert, X509Certificate? clientCert, MqttSslProtocols sslProtocol)
        {
            _logger = this.GetCurrentClassLogger();
            _mqttCient = new MqttClient(brokerHostName, brokerPort, secure, caCert, clientCert, sslProtocol);
        }

        public bool IsConnected => _mqttCient.IsConnected;

        public event IMqttClient.MqttMsgPublishEventHandler MqttMsgPublishReceived
        {
            add { _mqttCient.MqttMsgPublishReceived += value; }
            remove { _mqttCient.MqttMsgPublishReceived -= value; }
        }

        public event IMqttClient.MqttMsgPublishedEventHandler MqttMsgPublished
        {
            add { _mqttCient.MqttMsgPublished += value; }
            remove { _mqttCient.MqttMsgPublished -= value; }

        }

        public event IMqttClient.MqttMsgSubscribedEventHandler MqttMsgSubscribed
        {
            add { _mqttCient.MqttMsgSubscribed += value; }
            remove { _mqttCient.MqttMsgSubscribed -= value; }
        }

        public event IMqttClient.MqttMsgUnsubscribedEventHandler MqttMsgUnsubscribed
        {
            add { _mqttCient.MqttMsgUnsubscribed += value; }
            remove { _mqttCient.MqttMsgUnsubscribed -= value; }
        }

        public event IMqttClient.ConnectionClosedEventHandler ConnectionClosed
        {
            add { _mqttCient.ConnectionClosed += value; }
            remove { _mqttCient.ConnectionClosed -= value; }
        }

        public event ConnectionOpenedEventHandler ConnectionOpened
        {
            add { _mqttCient.ConnectionOpened += value; }
            remove { _mqttCient.ConnectionOpened -= value; }
        }

        public void Close() => _mqttCient.Close();

        public MqttReasonCode Connect(string clientId) 
            => Connect(clientId, null, null, willRetain: false, MqttQoSLevel.AtMostOnce, willFlag: false, null, null, cleanSession: true, 5);

        public MqttReasonCode Connect(string clientId, string username, string password, bool willRetain, MqttQoSLevel willQosLevel, bool willFlag, string willTopic, string willMessage, bool cleanSession, ushort keepAlivePeriod)
        {
            _logger.LogDebug("Connect to MQTT broker...");

            _autoReconnectEnabled = true;

            _clientId = clientId;
            _username = username;
            _password = password;
            _willRetain = willRetain;
            _willQosLevel = willQosLevel;
            _willFlag = willFlag;
            _willTopic = willTopic;
            _willMessage = willMessage;
            _cleanSession = cleanSession;
            _keepAlivePeriod = keepAlivePeriod;

            // Avoid double registration if Connect is called multiple times.
            _mqttCient.ConnectionClosed -= ReconnectHandler;
            _mqttCient.ConnectionClosedRequest -= ReconnectHandler;

            _mqttCient.ConnectionClosed += ReconnectHandler;
            _mqttCient.ConnectionClosedRequest += ReconnectHandler;

            var result = ConnectInternal();

            return result;
        }

        private MqttReasonCode ConnectInternal()
            => _mqttCient.Connect(_clientId, _username, _password, _willRetain, _willQosLevel, _willFlag, _willTopic, _willMessage, _cleanSession, _keepAlivePeriod);

        public void Disconnect()
        {
            _logger.LogDebug("Disconnect from MQTT broker...");
            _autoReconnectEnabled = false;

            _mqttCient.ConnectionClosed -= ReconnectHandler;
            _mqttCient.ConnectionClosedRequest -= ReconnectHandler;

            _connectThread?.Abort();
            _connectThread = null;

            // Keep internal subscription cache in sync with the broker state.
            _subscribedTopics.Clear();

            _mqttCient.Disconnect();
        }

        public void Init(string brokerHostName, int brokerPort, bool secure, byte[] caCert, byte[] clientCert, MqttSslProtocols sslProtocol)
            => _mqttCient.Init(brokerHostName, brokerPort, secure, caCert, clientCert, sslProtocol);

        public ushort Publish(string topic, byte[] message, string contentType, ArrayList userProperties, MqttQoSLevel qosLevel, bool retain)
            => _mqttCient.Publish(topic, message, contentType, userProperties, qosLevel, retain);

        public ushort Publish(string topic, byte[] message, string contentType)
            => _mqttCient.Publish(topic, message, contentType);

        public ushort Publish(string topic, byte[] message)
            => _mqttCient.Publish(topic, message);

        public ushort Subscribe(string[] topics, MqttQoSLevel[] qosLevels)
        {
            _logger.LogDebug("Subscribing to topics...");
            // Reuse the underlying client's subscribe method for all the input validation.
            // If it succeeds, cache the subscribed topics.
            var messageId = _mqttCient.Subscribe(topics, qosLevels);

            for (int i = 0; i < topics.Length; i++)
            {
                _subscribedTopics[topics[i]] = qosLevels[i];
            }

            return messageId;
        }

        public ushort Unsubscribe(string[] topics)
        {
            _logger.LogDebug("Unsubscribing from topics...");
            // Reuse the underlying client's unsubscribe method for all the input validation.
            // If it succeeds, remove the subscribed topics.
            var messageId = _mqttCient.Unsubscribe(topics);

            for (int i = 0; i < topics.Length; i++)
            {
                _subscribedTopics.Remove(topics[i]);
            }

            return messageId;
        }

        private bool TryReconnectOnce()
        {
            _logger.LogInformation("Trying to reconnect once...");

            var result = ConnectInternal();

            _logger.LogInformation($"Reconnect attempt finished with result: {result}.");

            return _mqttCient.IsConnected;
        }

        private void Reconnect()
        {
            _logger.LogInformation("Start reconnect...");

            while (_autoReconnectEnabled && !_mqttCient.IsConnected)
            {
                try
                {
                    if (TryReconnectOnce())
                    {
                        ResubscribeCachedTopics();
                        return;
                    }
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Reconnect attempt failed.");
                }

                Thread.Sleep(5_000);
            }
        }

        private void ReconnectHandler(object sender, System.EventArgs e)
        {
            _logger.LogWarning("MQTT connection lost. Reconnect handler called.");

            lock (_reconnectSync)
            {
                if (!_autoReconnectEnabled || _connectThread != null)
                {
                    return;
                }

                _connectThread = new Thread(() =>
                {
                    try
                    {
                        Reconnect();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Exception in reconnect thread.");
                    }
                    finally
                    {
                        lock (_reconnectSync)
                        {
                            _connectThread = null;
                        }
                    }
                });

                _connectThread.Start();
            }
        }

        private void ResubscribeCachedTopics()
        {
            if (_subscribedTopics.Count == 0)
            {
                return;
            }

            _logger.LogInformation("Resubscribing cached topics...");

            var topics = new string[_subscribedTopics.Count];
            var qosLevels = new MqttQoSLevel[_subscribedTopics.Count];

            int i = 0;
            foreach (DictionaryEntry entry in _subscribedTopics)
            {
                topics[i] = (string)entry.Key;
                qosLevels[i] = (MqttQoSLevel)entry.Value;
                i++;
            }
            _mqttCient.Subscribe(topics, qosLevels);
        }
    }
}
