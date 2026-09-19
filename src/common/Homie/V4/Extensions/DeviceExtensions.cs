using SmartHome.DeviceModel;
using SmartHome.Homie.V4.Settings;
using nanoFramework.M2Mqtt.Messages;

namespace SmartHome.Homie.V4.Extensions
{
    internal static class DeviceExtensions
    {
        /// <summary>
        /// The last will v4 requires: <c>homie/&lt;device-id&gt;/$state</c> set to
        /// <c>lost</c>, retained, at least once.
        /// </summary>
        /// <remarks>
        /// The model has no <c>Lost</c> transition of its own, deliberately: this is the
        /// broker publishing on the device's behalf precisely because the device is in
        /// no position to say it. Which is also why a will can only be declared in
        /// CONNECT, and why the adapter has to own the session rather than accept one
        /// somebody else opened.
        /// </remarks>
        internal static HomieLastWillSettings CreateLastWillSettings(this Device device)
            => new()
            {
                WillFlag = true,
                WillTopic = HomieTopics.Attribute(device, Constants.StateAttributeTopicId),
                WillMessage = HomieStates.Lost,
                WillQosLevel = MqttQoSLevel.AtLeastOnce,
                WillRetain = true
            };
    }
}
