using SmartHome.Homie.V4.Description;
using SmartHome.Homie.V4.Settings;
using Microsoft.Extensions.Logging;
using nanoFramework.M2Mqtt;
using System.Text;

namespace SmartHome.Homie.V4
{
    /// <summary>
    /// Puts a described device on the wire, in the order v4 requires.
    /// </summary>
    /// <remarks>
    /// Extends M2Mqtt's own <see cref="IMqttClient"/>, not the reconnect wrapper: none
    /// of this needs a connection that survives the broker going away, and hanging it
    /// off the wrapper would tie a publishing concern to a transport one.
    /// </remarks>
    internal static class HomiePublishExtensions
    {
        public static void PublishHomieAttribute(this IMqttClient mqttClient, HomieAttributeMessage attribute, PublishSettings publishSettings, ILogger logger)
            => mqttClient.PublishHomieAttribute(attribute.Topic, attribute.Payload, publishSettings, logger);

        public static void PublishHomieAttribute(this IMqttClient mqttClient, string topic, byte[] payload, PublishSettings publishSettings, ILogger logger)
        {
            logger.LogDebug($"Publishing Homie attribute '{topic}' -> '{Encoding.UTF8.GetString(payload, 0, payload.Length)}'");

            mqttClient.Publish(
                topic,
                payload,
                publishSettings.ContentType,
                publishSettings.UserProperties,
                publishSettings.QoSLevel,
                publishSettings.Retained);
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

        public static void PublishHomiePropertyInfo(this IMqttClient mqttClient, HomiePropertyDescription property, HomiePublishSettings homiePublishSettings, ILogger logger)
        {
            logger.LogDebug($"Publishing Homie property info for property '{property.Property.Id}'");

            for (int i = 0; i < property.Attributes.Length; i++)
            {
                mqttClient.PublishHomieAttribute(property.Attributes[i], homiePublishSettings.PropertyInfoPublishSettings, logger);
            }
        }

        public static void PublishHomieNodeInfo(this IMqttClient mqttClient, HomieNodeDescription node, HomiePublishSettings homiePublishSettings, ILogger logger)
        {
            logger.LogDebug($"Publishing Homie node info for node '{node.Id}'");

            for (int i = 0; i < node.Attributes.Length; i++)
            {
                mqttClient.PublishHomieAttribute(node.Attributes[i], homiePublishSettings.NodeInfoPublishSettings, logger);
            }

            for (int i = 0; i < node.Properties.Length; i++)
            {
                var property = node.Properties[i];

                mqttClient.PublishHomiePropertyInfo(property, homiePublishSettings, logger);

                // The value follows its own attributes, and carries the property's own
                // retained flag rather than the publish settings' -- a momentary event
                // must not be left in the store, while everything describing it must.
                mqttClient.PublishHomiePropertyValue(
                    property.Topic,
                    property.Property.GetPayload(),
                    homiePublishSettings.PropertyUpdatePublishSettings,
                    property.Property.Retained,
                    logger);
            }
        }

        /// <summary>
        /// The whole announcement bar the closing <c>$state</c>: the device's
        /// attributes, <c>$state = init</c>, then every node.
        /// </summary>
        /// <remarks>
        /// <c>init</c> goes out here and only here. It is the one lifecycle token the
        /// state publisher never emits on its own, because it means "the description
        /// that follows is being written" -- a standalone <c>init</c> with nothing after
        /// it would tell a controller to wait for an announcement that never comes.
        /// </remarks>
        public static void PublishHomieDeviceInfo(this IMqttClient mqttClient, HomieDescription description, HomiePublishSettings homiePublishSettings, ILogger logger)
        {
            logger.LogDebug($"Publishing Homie device info for device '{description.DeviceId}'");

            for (int i = 0; i < description.DeviceAttributes.Length; i++)
            {
                mqttClient.PublishHomieAttribute(description.DeviceAttributes[i], homiePublishSettings.DeviceInfoPublishSettings, logger);
            }

            mqttClient.PublishHomieAttribute(
                description.StateTopic,
                Encoding.UTF8.GetBytes(HomieStates.Init),
                homiePublishSettings.DeviceInfoPublishSettings,
                logger);

            for (int i = 0; i < description.Nodes.Length; i++)
            {
                mqttClient.PublishHomieNodeInfo(description.Nodes[i], homiePublishSettings, logger);
            }
        }
    }
}
