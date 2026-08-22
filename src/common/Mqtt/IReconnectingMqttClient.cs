using nanoFramework.M2Mqtt;
using nanoFramework.M2Mqtt.Messages;

namespace SmartHome.Mqtt
{
    public interface IReconnectingMqttClient : IMqttClient
    {
        event MqttClient.ConnectionOpenedEventHandler ConnectionOpened;

        MqttReasonCode Connect(string clientId);
    }
}