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
        // Matches MqttMsgConnect.KEEP_ALIVE_PERIOD_DEFAULT in the wrapped client. An
        // overload that mirrors MqttClient's signature has to mirror its defaults too:
        // this used to pass 5, so the keep-alive thread pinged every 5s and gave the
        // broker 5s to answer before declaring the connection dead -- a disconnect the
        // broker never saw, on the one test that measures reconnects.
        private const ushort DefaultKeepAlivePeriodSeconds = 60;

        private const int ReconnectDelayMs = 5_000;

        private readonly MqttClient _mqttCient;
        private readonly ILogger _logger;
        private Thread? _connectThread;

        private readonly IDictionary _subscribedTopics = new Hashtable();

        private bool _autoReconnectEnabled;

        private readonly object _reconnectSync = new();

        // Guards _subscribedTopics only. Separate from _reconnectSync so a resubscribe
        // never holds the reconnect lock while it blocks on the socket. The table is
        // touched from three threads: the app's (Subscribe/Unsubscribe/Disconnect), the
        // reconnect thread's (replay), and M2Mqtt's receive thread by way of Disconnect.
        private readonly object _subscriptionSync = new();

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

        public void Close()
        {
            // Same teardown as Disconnect(). MqttClient.Close() drops the channel, and
            // its receive thread reports that as ConnectionClosed -- so with
            // auto-reconnect still armed and ReconnectHandler still attached, the
            // wrapper would reopen the very session the caller just closed.
            Teardown();

            _mqttCient.Close();
        }

        public MqttReasonCode Connect(string clientId)
            => Connect(clientId, null, null, willRetain: false, MqttQoSLevel.AtMostOnce, willFlag: false, null, null, cleanSession: true, DefaultKeepAlivePeriodSeconds);

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

            Teardown();

            _mqttCient.Disconnect();
        }

        // Everything both Disconnect() and Close() have to undo. Disarming
        // auto-reconnect first is what stops the teardown from being immediately
        // reversed by our own ConnectionClosed handler.
        private void Teardown()
        {
            _autoReconnectEnabled = false;

            _mqttCient.ConnectionClosed -= ReconnectHandler;
            _mqttCient.ConnectionClosedRequest -= ReconnectHandler;

            // Clear the cache BEFORE aborting the reconnect thread, not after. That
            // thread takes _subscriptionSync to snapshot the cache, and Thread.Abort is
            // not documented to release a monitor the aborted thread was holding -- so
            // aborting first and then blocking on the same lock risks deadlocking the
            // caller of Disconnect(). Taking it first cannot block for long: the lock is
            // only ever held for a short CPU-bound copy, never across the socket.
            lock (_subscriptionSync)
            {
                _subscribedTopics.Clear();
            }

            // Under the lock: ReconnectHandler creates and assigns _connectThread inside
            // it, from M2Mqtt's receive thread. Reading the field outside meant a
            // reconnect could be started just after Disconnect() decided there was
            // nothing to abort.
            lock (_reconnectSync)
            {
                _connectThread?.Abort();
                _connectThread = null;
            }
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

            // The returned id means "SUBSCRIBE was enqueued", nothing more: the wrapped
            // client builds the message and queues it, without validating the topics,
            // checking the connection, or waiting for SUBACK. So the cache below records
            // intent to be replayed on reconnect -- it is not evidence the broker
            // accepted anything.
            var messageId = _mqttCient.Subscribe(topics, qosLevels);

            lock (_subscriptionSync)
            {
                for (int i = 0; i < topics.Length; i++)
                {
                    _subscribedTopics[topics[i]] = qosLevels[i];
                }
            }

            return messageId;
        }

        public ushort Unsubscribe(string[] topics)
        {
            _logger.LogDebug("Unsubscribing from topics...");

            // Same caveat as Subscribe: enqueued, not acknowledged.
            var messageId = _mqttCient.Unsubscribe(topics);

            lock (_subscriptionSync)
            {
                for (int i = 0; i < topics.Length; i++)
                {
                    _subscribedTopics.Remove(topics[i]);
                }
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

            // The loop condition deliberately does NOT include !IsConnected. Restoring a
            // session is connect *and* resubscribe, and only the pair is worth returning
            // on. When the guard was `!IsConnected`, a throw out of the resubscribe was
            // caught, slept on, and then re-evaluated as "connected, nothing to do" -- so
            // the thread exited leaving a live connection with no subscriptions replayed.
            // Publishing kept working, every /set was silently ignored until reboot, and
            // the only evidence was one LogWarning.
            while (_autoReconnectEnabled)
            {
                try
                {
                    if (!_mqttCient.IsConnected && !TryReconnectOnce())
                    {
                        Thread.Sleep(ReconnectDelayMs);
                        continue;
                    }

                    // Disconnect() can have run while the connect was in flight. It has
                    // already detached ReconnectHandler, so nothing else would ever close
                    // the session this thread just opened -- and for a HomieClient
                    // session that means the will fires against a device the app believes
                    // it shut down.
                    if (!_autoReconnectEnabled)
                    {
                        _logger.LogInformation("Disconnect requested during reconnect; closing the session that was just opened.");
                        _mqttCient.Disconnect();
                        return;
                    }

                    ResubscribeCachedTopics();
                    return;
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Reconnect attempt failed.");
                }

                Thread.Sleep(ReconnectDelayMs);
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
            string[] topics;
            MqttQoSLevel[] qosLevels;

            // Snapshot under the lock, then talk to the broker outside it. Enumerating
            // the live table here raced every Subscribe/Unsubscribe/Disconnect on the app
            // thread: sizing the arrays from Count and then walking the table are two
            // steps, so a concurrent write produced either trailing nulls in `topics` or
            // a collection-modified throw.
            lock (_subscriptionSync)
            {
                if (_subscribedTopics.Count == 0)
                {
                    return;
                }

                topics = new string[_subscribedTopics.Count];
                qosLevels = new MqttQoSLevel[_subscribedTopics.Count];

                int i = 0;
                foreach (DictionaryEntry entry in _subscribedTopics)
                {
                    topics[i] = (string)entry.Key;
                    qosLevels[i] = (MqttQoSLevel)entry.Value;
                    i++;
                }
            }

            _logger.LogInformation("Resubscribing cached topics...");

            _mqttCient.Subscribe(topics, qosLevels);
        }
    }
}
