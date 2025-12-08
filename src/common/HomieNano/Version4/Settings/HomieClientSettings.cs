using nanoFramework.M2Mqtt.Messages;
using System;

namespace HomieNano.Version4.Settings
{
    public class HomieClientSettings
    {
        public string ClientId { get; set; } = Guid.NewGuid().ToString();

        public string UserName { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        public bool WillRetain { get; set; } = false;

        public MqttQoSLevel WillQosLevel { get; set; } = MqttQoSLevel.AtLeastOnce;

        public bool WillFlag { get; set; } = false;

        public string WillTopic {  get; set; } = string.Empty;

        public string WillMessage { get; set;} = string.Empty;

        public bool CleanSession { get; set; } = false;

        public ushort KeepAlivePeriod {  get; set; } = ushort.MaxValue;

        public MqttQoSLevel SettablePropertySubscriptionQosLevel { get; set; } = MqttQoSLevel.AtLeastOnce;

        /// <summary>
        /// Gets or sets the settings used to configure publishing behavior for Homie messages.
        /// </summary>
        /// <remarks>Use this property to customize parameters such as topic formatting, message
        /// retention, and quality of service when publishing Homie messages. Changes to these settings affect how
        /// messages are sent to the broker.</remarks>
        public HomiePublishSettings PublishSettings { get; set; } = new HomiePublishSettings();
    }
}
