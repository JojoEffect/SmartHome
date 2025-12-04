using HomieNano.Version4.EventArgs;
using HomieNano.Version4.Properties;
using nanoFramework.M2Mqtt;
using nanoFramework.M2Mqtt.Messages;
using System.Collections;

namespace HomieNano.Version4
{
    public class HomieClient
    {
        private readonly Device _device;
        private readonly HomieClientConfiguration _deviceClientConfiguration;
        private readonly IMqttClient _mqttClient;
        private readonly IDictionary _settablePropertiesTable;

        public HomieClient(Device device, IMqttClient mqttClient, HomieClientConfiguration? deviceClientConfiguration = null)
        {
            _device = device;
            _mqttClient = mqttClient;
            _deviceClientConfiguration = deviceClientConfiguration ?? new HomieClientConfiguration();
            _settablePropertiesTable = InitializeSettablePropertiesTable(device);
        }

        public void Connect()
        {
            _mqttClient.Connect(
                _deviceClientConfiguration.ClientId,
                _deviceClientConfiguration.UserName,
                _deviceClientConfiguration.Password,
                _deviceClientConfiguration.WillRetain,
                _deviceClientConfiguration.WillQosLevel,
                _deviceClientConfiguration.WillFlag,
                _deviceClientConfiguration.WillTopic,
                _deviceClientConfiguration.WillMessage,
                _deviceClientConfiguration.CleanSession,
                _deviceClientConfiguration.KeepAlivePeriod
                );

            RegisterPropertyUpdateHandlers();
            SubscribeSettablePropertyTopics();
        }

        public void Disconnect()
        {
            UnsubscribeSettablePropertyTopics();
            UnregisterPropertyUpdateHandlers();
            _mqttClient.Disconnect();
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

            _mqttClient.Publish(
                topic,
                message,
                _deviceClientConfiguration.UpdatePropertyPublishContentType,
                _deviceClientConfiguration.UpdatePropertyPublishUserProperties,
                _deviceClientConfiguration.UpdatePropertyPublishQosLevel,
                retained);
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
