using nanoFramework.M2Mqtt.Messages;

namespace HomieNano.Version4.Settings
{
    public class HomieLastWillSettings
    {
        public bool WillRetain { get; set; } = false;

        public MqttQoSLevel WillQosLevel { get; set; } = MqttQoSLevel.AtLeastOnce;

        public bool WillFlag { get; set; } = false;

        public string WillTopic { get; set; } = string.Empty;

        public string WillMessage { get; set; } = string.Empty;
    }
}
