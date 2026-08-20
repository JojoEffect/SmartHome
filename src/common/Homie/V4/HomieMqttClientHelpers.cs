using SmartHome.Homie.V4.Attributes;
using SmartHome.Homie.V4.Properties;
using SmartHome.Homie.V4.Settings;
using Microsoft.Extensions.Logging;
using nanoFramework.M2Mqtt;
using System.Text;

namespace SmartHome.Homie.V4
{
    internal static class HomieMqttClientHelpers
    {
        public static void PublishHomieAttribute(this IMqttClient mqttClient, AttributeBase? attribute, PublishSettings publishSettings, ILogger logger)
        {
            if (attribute == null)
            {
                return;
            }

            var topic = attribute.GetTopic();
            var payload = attribute.GetPayload();

            logger.LogDebug($"Publishing Homie attribute '{topic}' -> '{Encoding.UTF8.GetString(payload, 0, payload.Length)}'");

            mqttClient.Publish(
                topic,
                payload, 
                publishSettings.ContentType,
                publishSettings.UserProperties,
                publishSettings.QoSLevel,
                publishSettings.Retained);
        }

        public static void PublishHomiePropertyInfo(this IMqttClient mqttClient, PropertyBase property, HomiePublishSettings homiePublishSettings, ILogger logger)
        {
            logger.LogDebug($"Publishing Homie property info for property '{property.TopicId}'");

            mqttClient.PublishHomieAttribute(property.NameAttribute, homiePublishSettings.PropertyInfoPublishSettings, logger);
            mqttClient.PublishHomieAttribute(property.DataTypeAttribute, homiePublishSettings.PropertyInfoPublishSettings, logger);
            mqttClient.PublishHomieAttribute(property.FormatAttribute, homiePublishSettings.PropertyInfoPublishSettings, logger);
            mqttClient.PublishHomieAttribute(property.SettableAttribute, homiePublishSettings.PropertyInfoPublishSettings, logger);
            mqttClient.PublishHomieAttribute(property.RetainedAttribute, homiePublishSettings.PropertyInfoPublishSettings, logger);
            mqttClient.PublishHomieAttribute(property.UnitAttribute, homiePublishSettings.PropertyInfoPublishSettings, logger);
        }

        public static void PublishHomiePropertyValue(this IMqttClient mqttClient, string topic, byte[] payload, PublishSettings publishSettings, bool retained, ILogger logger)
        {
            logger.LogDebug($"Publishing Homie property value '{topic}' -> '{Encoding.UTF8.GetString(payload, 0, payload.Length)}'");

            mqttClient.Publish(
                        topic,
                        payload,
                        publishSettings.ContentType,
                        publishSettings.UserProperties,
                        publishSettings.QoSLevel,
                        retained);
        }

        public static void PublishHomieNodeInfo(this IMqttClient mqttClient, Node node, HomiePublishSettings homiePublishSettings, ILogger logger)
        {
            logger.LogDebug($"Publishing Homie node info for node '{node.TopicId}'");

            mqttClient.PublishHomieAttribute(node.NameAttribute, homiePublishSettings.NodeInfoPublishSettings, logger);
            mqttClient.PublishHomieAttribute(node.TypeAttribute, homiePublishSettings.NodeInfoPublishSettings, logger);
            mqttClient.PublishHomieAttribute(node.PropertiesAttribute, homiePublishSettings.NodeInfoPublishSettings, logger);
            foreach (var property in node.Properties)
            {
                mqttClient.PublishHomiePropertyInfo(property, homiePublishSettings, logger);
                mqttClient.PublishHomiePropertyValue(property.GetTopic(),
                    property.GetPayload(),
                    homiePublishSettings.PropertyUpdatePublishSettings,
                    property.RetainedAttribute.Value,
                    logger);
            }
        }

        public static void PublishHomieDeviceInfo(this IMqttClient mqttClient, Device device, HomiePublishSettings homiePublishSettings, ILogger logger)
        {
            logger.LogDebug($"Publishing Homie device info for device '{device.TopicId}'");

            mqttClient.PublishHomieAttribute(device.HomieAttribute, homiePublishSettings.DeviceInfoPublishSettings, logger);
            mqttClient.PublishHomieAttribute(device.NameAttribute, homiePublishSettings.DeviceInfoPublishSettings, logger);
            mqttClient.PublishHomieAttribute(device.NodesAttribute, homiePublishSettings.DeviceInfoPublishSettings, logger);
            mqttClient.PublishHomieAttribute(device.ImplementationAttribute, homiePublishSettings.DeviceInfoPublishSettings, logger);
            mqttClient.PublishHomieAttribute(device.StateAttribute, homiePublishSettings.DeviceInfoPublishSettings, logger);
            foreach (var node in device.Nodes)
            {
                mqttClient.PublishHomieNodeInfo(node, homiePublishSettings, logger);
            }
        }
    }
}
