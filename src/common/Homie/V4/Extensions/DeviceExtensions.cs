using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.Settings;
using nanoFramework.M2Mqtt.Messages;

namespace SmartHome.Homie.V4.Extensions
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
