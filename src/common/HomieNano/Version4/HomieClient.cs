using HomieNano.Version4.Enums;
using HomieNano.Version4.EventArgs;
using HomieNano.Version4.Properties;
using HomieNano.Version4.Settings;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using nanoFramework.M2Mqtt;
using nanoFramework.M2Mqtt.Messages;
using System.Collections;

namespace HomieNano.Version4
{
    public class HomieClient
    {
        private readonly Device _device;
        private readonly HomieClientSettings _deviceClientSettings;
        private readonly ILogger _logger;
        private readonly IMqttClient _mqttClient;
        private readonly IDictionary _settablePropertiesTable;

        public HomieClient(Device device, IMqttClient mqttClient, HomieClientSettings? deviceClientSettings = null)
        {
            _device = device;
            _mqttClient = mqttClient;
            _deviceClientSettings = deviceClientSettings ?? new HomieClientSettings();
            _logger = this.GetCurrentClassLogger();
            _settablePropertiesTable = InitializeSettablePropertiesTable(device);
        }

        public void Connect()
        {
            _device.OnDeviceStateChange += HandleDeviceStateChange;

            _mqttClient.Connect(
                _deviceClientSettings.ClientId,
                _deviceClientSettings.UserName,
                _deviceClientSettings.Password,
                _deviceClientSettings.WillRetain,
                _deviceClientSettings.WillQosLevel,
                _deviceClientSettings.WillFlag,
                _deviceClientSettings.WillTopic,
                _deviceClientSettings.WillMessage,
                _deviceClientSettings.CleanSession,
                _deviceClientSettings.KeepAlivePeriod
                );

            RegisterPropertyUpdateHandlers();
            SubscribeSettablePropertyTopics();

            if (!_device.TryChangeState(State.Init))
            {
                Disconnect();
                _logger.LogError("Failed to connect: unable to change device state to 'init' after connecting. Disconnecting.");
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
                    DisconnectInternal();
                    return;
                case State.Init:
                    _mqttClient.PublishHomieDeviceInfo(_device, _deviceClientSettings.PublishSettings, _logger);
                    if (!_device.TryChangeState(State.Ready))
                    {
                        _logger.LogError("Failed to change device state to 'ready' after publishing device info. Disconnecting.");
                        DisconnectInternal();
                    }
                    return;
                case State.Ready:
                    return;
                case State.Sleeping:
                    return;
                case State.Lost:
                    return;
                case State.Alert:
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

            _mqttClient.PublishHomiePropertyValue(topic, message, _deviceClientSettings.PublishSettings.PropertyUpdatePublishSettings, retained, _logger);
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
