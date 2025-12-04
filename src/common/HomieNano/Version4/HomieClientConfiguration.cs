using nanoFramework.M2Mqtt.Messages;
using System;
using System.Collections;

namespace HomieNano.Version4
{
    public class HomieClientConfiguration
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

        public MqttQoSLevel UpdatePropertyPublishQosLevel { get; set; } = MqttQoSLevel.AtLeastOnce;

        public string UpdatePropertyPublishContentType { get; set; } = string.Empty;

        public ArrayList UpdatePropertyPublishUserProperties { get; set; } = new ArrayList();
    }
}
