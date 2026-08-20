using nanoFramework.M2Mqtt;
using nanoFramework.M2Mqtt.Messages;

namespace SmartHome.Homie.V4
{
    public interface IHomieMqttClient : IMqttClient
    {
        event MqttClient.ConnectionOpenedEventHandler ConnectionOpened;

        MqttReasonCode Connect(string clientId);
    }
}