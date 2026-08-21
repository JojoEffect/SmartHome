using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.EventArgs;
using SmartHome.Homie.V4.Extensions;
using SmartHome.Homie.V4.Properties;
using SmartHome.Homie.V4.Settings;
using SmartHome.Mqtt;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using nanoFramework.M2Mqtt.Messages;
using System;
using System.Collections;

namespace SmartHome.Homie.V4
{
    public class HomieClient
    {
        private readonly Device _device;
        private readonly HomieClientSettings _homieClientSettings;
        private readonly HomiePublishSettings _homiePublishSettings;
        private readonly HomieLastWillSettings _homieLastWillSettings;
        private readonly ILogger _logger;
        private readonly IReconnectingMqttClient _mqttClient;
        private readonly IDictionary _settablePropertiesTable;

        public HomieClient(Device device,
            IReconnectingMqttClient mqttClient,
            HomieClientSettings? deviceClientSettings = null,
            HomiePublishSettings? homiePublishSettings = null,
            HomieLastWillSettings? homieLastWillSettings = null)
        {
            _device = device;
            _mqttClient = mqttClient;
            // Default the MQTT client id to the device's own topic id rather than a
            // random Guid. With a per-boot random id the broker keeps the dead session
            // alive until its keepalive expires, so the old session's 'lost' will is
            // delivered AFTER the rebooted device has already announced 'ready' --
            // leaving the retained $state at 'lost' while the device is running. A
            // stable id makes the new connection take the session over instead, so the
            // states stay ordered. Homie doesn't prescribe a client id; it does
            // prescribe one connection per device.
            _homieClientSettings = deviceClientSettings ?? new HomieClientSettings { ClientId = device.TopicId };
            _homiePublishSettings = homiePublishSettings ?? new HomiePublishSettings();
            _homieLastWillSettings = homieLastWillSettings ?? _device.CreateLastWillSettings();
            _logger = this.GetCurrentClassLogger();
            _settablePropertiesTable = InitializeSettablePropertiesTable(device);
        }

        /// <summary>
        /// Connects the device and announces it, returning whether that succeeded.
        /// </summary>
        /// <remarks>
        /// This client owns the MQTT session, and it has to: Homie v4 requires the
        /// connection to carry a last will setting <c>homie/[device-id]/$state</c> to
        /// <c>lost</c>, and a will can only be declared in CONNECT. A session opened
        /// by someone else -- for instance an app calling
        /// <c>IReconnectingMqttClient.Connect(clientId)</c> first -- cannot have that
        /// will, so continuing on it would leave the device permanently stuck at
        /// 'ready' from a controller's point of view whenever it dies abruptly. That
        /// is exactly what this code used to do. A foreign session is therefore
        /// replaced, not reused.
        /// </remarks>
        public bool Connect()
        {
            _logger.LogDebug("Connect...");

            try
            {
                _device.OnDeviceStateChange += HandleDeviceStateChange;

                if (_mqttClient.IsConnected)
                {
                    _logger.LogWarning("MQTT client was already connected; that session cannot carry the Homie last will. Reconnecting it.");
                    _mqttClient.Disconnect();
                }

                ConnectInternal();

                if (!_mqttClient.IsConnected)
                {
                    _logger.LogError("Failed to connect: the MQTT client did not connect.");
                    return false;
                }

                RegisterConnectionChangeHandlers();

                RegisterPropertyUpdateHandlers();
                SubscribeSettablePropertyTopics();

                if (!_device.TryChangeState(State.Init))
                {
                    Disconnect();
                    _logger.LogError("Failed to connect: unable to change device state to 'init' after connecting. Disconnecting.");
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                _logger.LogCritical(e, "Failed to connect.");
                return false;
            }
        }

        private void ConnectInternal()
        {
            var mqttCode = _mqttClient.Connect(
                                _homieClientSettings.ClientId,
                                _homieClientSettings.UserName,
                                _homieClientSettings.Password,
                                _homieLastWillSettings.WillRetain,
                                _homieLastWillSettings.WillQosLevel,
                                _homieLastWillSettings.WillFlag,
                                _homieLastWillSettings.WillTopic,
                                _homieLastWillSettings.WillMessage,
                                _homieClientSettings.CleanSession,
                                _homieClientSettings.KeepAlivePeriod
                                );
        }

        public void Disconnect()
        {
            _logger.LogDebug("Disconnect...");

            if (!_device.TryChangeState(State.Disconnected))
            {
                _logger.LogError("Failed to disconnect: unable to change device state to 'disconnected'. Disconnected MQTT client anyways.");
            }

            UnregisterConnectionChangeHandlers();
        }

        private void DisconnectInternal()
        {
            UnsubscribeSettablePropertyTopics();
            UnregisterPropertyUpdateHandlers();
            _mqttClient.Disconnect();
            _device.OnDeviceStateChange -= HandleDeviceStateChange;
        }

        private void HandleDeviceStateChange(DeviceStateChangeEventArgs args)
        {
            switch (args.CurrentState)
            {
                case State.Disconnected:
                    _mqttClient.PublishHomieAttribute(_device.StateAttribute, _homiePublishSettings.DeviceStatePublishSettings, _logger);
                    DisconnectInternal();
                    return;
                case State.Init:
                    _mqttClient.PublishHomieDeviceInfo(_device, _homiePublishSettings, _logger);
                    if (!_device.TryChangeState(State.Ready))
                    {
                        _logger.LogError("Failed to change device state to 'ready' after publishing device info. Disconnecting.");
                        DisconnectInternal();
                    }
                    return;
                case State.Ready:
                case State.Sleeping:
                case State.Alert:
                    _mqttClient.PublishHomieAttribute(_device.StateAttribute, _homiePublishSettings.DeviceStatePublishSettings, _logger);
                    return;
                case State.Lost:
                    return;
            }
        }

        private void SubscribeSettablePropertyTopics()
        {
            _logger.LogDebug("Subscribing to settable property topics...");

            if (_settablePropertiesTable.Count == 0)
            {
                _logger.LogDebug("No settable properties found. Skipping MQTT subscribe.");
                return;
            }

            var topics = new string[_settablePropertiesTable.Count];
            _settablePropertiesTable.Keys.CopyTo(topics, 0);

            var qosLevels = new MqttQoSLevel[topics.Length];
            for (int i = 0; i < qosLevels.Length; i++)
            {
                qosLevels[i] = MqttQoSLevel.AtLeastOnce;
            }

            _mqttClient.Subscribe(topics, qosLevels);
            _mqttClient.MqttMsgPublishReceived += HandleIncomingMessage;
        }

        private void UnsubscribeSettablePropertyTopics()
        {
            _logger.LogDebug("Unsubscribing from settable property topics...");

            if (_settablePropertiesTable.Count == 0)
            {
                return;
            }

            var topics = new string[_settablePropertiesTable.Count];
            _settablePropertiesTable.Keys.CopyTo(topics, 0);

            _mqttClient.Unsubscribe(topics);
            _mqttClient.MqttMsgPublishReceived -= HandleIncomingMessage;
        }

        private void HandleIncomingMessage(object sender, MqttMsgPublishEventArgs e)
        {
            string topic = e.Topic;
            byte[] message = e.Message;

            if (_settablePropertiesTable.Contains(topic))
            {
                var property = (PropertyBase)_settablePropertiesTable[topic];
                property.Set(message);
            }
        }

        private void HandleConnectionOpen(object sender, ConnectionOpenedEventArgs e)
        {
            _logger.LogInformation("MQTT connection opened handler called.");

            RegisterPropertyUpdateHandlers();
        }

        private void HandleConnectionClosed(object sender, System.EventArgs e)
        {
            _logger.LogInformation("MQTT connection closed handler called.");

            UnregisterPropertyUpdateHandlers();
        }

        private void RegisterConnectionChangeHandlers()
        {
            _logger.LogDebug("Registering connection change handlers...");

            _mqttClient.ConnectionClosed += HandleConnectionClosed;
            _mqttClient.ConnectionOpened += HandleConnectionOpen;
        }


        private void UnregisterConnectionChangeHandlers()
        {
            _logger.LogDebug("Unregistering connection change handlers...");

            _mqttClient.ConnectionClosed -= HandleConnectionClosed;
            _mqttClient.ConnectionOpened -= HandleConnectionOpen;
        }

        private void RegisterPropertyUpdateHandlers()
        {
            _logger.LogDebug("Registering property update handlers...");

            foreach (var node in _device.Nodes)
            {
                foreach (var property in node.Properties)
                {
                    property.OnUpdate += PublishPropertyUpdate;
                }
            }
        }

        private void UnregisterPropertyUpdateHandlers()
        {
            _logger.LogDebug("Unregistering property update handlers...");

            foreach (var node in _device.Nodes)
            {
                foreach (var property in node.Properties)
                {
                    property.OnUpdate -= PublishPropertyUpdate;
                }
            }
        }

        private void PublishPropertyUpdate(PropertyUpdateEventArgs args)
        {
            var property = args.Property;
            var message = args.Value;
            var retained = property.RetainedAttribute.Value;
            string topic = property.GetTopic();

            _logger.LogDebug($"Publishing property update. Topic: {topic}, Retained: {retained}, Message: {System.Text.Encoding.UTF8.GetString(message, 0, message.Length)}");

            _mqttClient.PublishHomiePropertyValue(topic, message, _homiePublishSettings.PropertyUpdatePublishSettings, retained, _logger);
        }

        private static IDictionary InitializeSettablePropertiesTable(Device device)
        {
            var settableProperties = device.GetAllSettableProperties();

            var settablePropertiesTable = new Hashtable(settableProperties.Length);

            for (int i = 0; i < settableProperties.Length; i++)
            {
                settablePropertiesTable.Add(settableProperties[i].GetTopic(), settableProperties[i]);
            }

            return settablePropertiesTable;
        }
    }
}
