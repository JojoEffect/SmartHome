using nanoFramework.M2Mqtt.Messages;
using System;

namespace HomieNano.Version4.Settings
{
    public class HomieClientSettings
    {
        public string ClientId { get; set; } = Guid.NewGuid().ToString();

        public string UserName { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        public bool CleanSession { get; set; } = false;

        public ushort KeepAlivePeriod {  get; set; } = ushort.MaxValue;

        public MqttQoSLevel SettablePropertySubscriptionQosLevel { get; set; } = MqttQoSLevel.AtLeastOnce;
    }
}
