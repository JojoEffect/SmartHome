using HomieNano.Version4.Enums;
using HomieNano.Version4.EventArgs;
using HomieNano.Version4.Extensions;
using HomieNano.Version4.Properties;
using HomieNano.Version4.Settings;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using nanoFramework.M2Mqtt;
using nanoFramework.M2Mqtt.Messages;
using System;
using System.Collections;

namespace HomieNano.Version4
{
    public class HomieClient
    {
        private readonly Device _device;
        private readonly HomieClientSettings _homieClientSettings;
        private readonly HomiePublishSettings _homiePublishSettings;
        private readonly HomieLastWillSettings _homieLastWillSettings;
        private readonly ILogger _logger;
        private readonly IMqttClient _mqttClient;
        private readonly IDictionary _settablePropertiesTable;

        public HomieClient(Device device,
            IMqttClient mqttClient,
            HomieClientSettings? deviceClientSettings = null,
            HomiePublishSettings? homiePublishSettings = null,
            HomieLastWillSettings? homieLastWillSettings = null)
        {
            _device = device;
            _mqttClient = mqttClient;
            _homieClientSettings = deviceClientSettings ?? new HomieClientSettings();
            _homiePublishSettings = homiePublishSettings ?? new HomiePublishSettings();
            _homieLastWillSettings = homieLastWillSettings ?? _device.CreateLastWillSettings();
            _logger = this.GetCurrentClassLogger();
            _settablePropertiesTable = InitializeSettablePropertiesTable(device);
        }

        public void Connect()
        {
            try
            {
                _device.OnDeviceStateChange += HandleDeviceStateChange;

                _mqttClient.Connect(
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

                RegisterPropertyUpdateHandlers();
                SubscribeSettablePropertyTopics();

                if (!_device.TryChangeState(State.Init))
                {
                    Disconnect();
                    _logger.LogError("Failed to connect: unable to change device state to 'init' after connecting. Disconnecting.");
                }
            }
            catch (Exception e)
            {
                _logger.LogCritical(e, "Failed to connect.");
            }
        }

        public void Disconnect()
        {
            if (!_device.TryChangeState(State.Disconnected))
            {
                _logger.LogError("Failed to disconnect: unable to change device state to 'disconnected'. Disconnected MQTT client anyways.");
            }
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

        private void RegisterPropertyUpdateHandlers()
        {
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
