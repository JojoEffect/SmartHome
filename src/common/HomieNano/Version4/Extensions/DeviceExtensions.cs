using HomieNano.Version4.Enums;
using HomieNano.Version4.Settings;
using nanoFramework.M2Mqtt.Messages;

namespace HomieNano.Version4.Extensions
{
    internal static class DeviceExtensions
    {
        internal static HomieLastWillSettings CreateLastWillSettings(this Device device) 
            => new()
            {
                WillFlag = true,
                WillTopic = device.StateAttribute.GetTopic(),
                WillMessage = State.Lost.GetString(),
                WillQosLevel = MqttQoSLevel.AtLeastOnce,
                WillRetain = true
            };
    }
}
