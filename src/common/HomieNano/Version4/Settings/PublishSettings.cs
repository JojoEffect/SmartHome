using nanoFramework.M2Mqtt.Messages;
using System.Collections;

namespace HomieNano.Version4.Settings
{
    public class PublishSettings
    {
        /// <summary>
        /// Gets or sets a value indicating whether the message is marked as retained on the server.
        /// </summary>
        /// <remarks>When set to <see langword="true"/>, the server will store the message and deliver it
        /// to future subscribers. When set to <see langword="false"/>, the message will not be retained after delivery.</remarks>
        public bool Retained { get; set; } = true;
        
        /// <summary>
        /// Gets or sets the media type of the content, such as "application/json" or "text/html".
        /// </summary>
        public string ContentType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the collection of custom user properties associated with the instance.
        /// </summary>
        /// <remarks>User properties can be used to store additional metadata or application-specific
        /// information. The collection is initialized as an empty list and can contain objects of any type. Changes to
        /// the collection are not thread-safe.</remarks>
        public ArrayList UserProperties { get; set; } = new ArrayList();
        
        /// <summary>
        /// Gets or sets the Quality of Service (QoS) level to use when publishing MQTT messages.
        /// </summary>
        /// <remarks>The QoS level determines the guarantee of delivery for MQTT messages. Higher levels
        /// provide increased reliability but may incur additional overhead. The default value is <see
        /// cref="MqttQoSLevel.AtLeastOnce"/>.</remarks>
        public MqttQoSLevel QoSLevel { get; set; } = MqttQoSLevel.AtLeastOnce;
    }
}
