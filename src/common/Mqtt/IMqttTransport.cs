using nanoFramework.M2Mqtt;

namespace SmartHome.Mqtt
{
    /// <summary>
    /// The MQTT client <see cref="ReconnectingMqttClient"/> wraps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because <see cref="IMqttClient"/> is not enough to wrap. The wrapper's
    /// whole job is to notice that a session died and rebuild it, and the two events that
    /// say a session died -- <see cref="ConnectionOpened"/>'s counterpart
    /// <c>ConnectionClosed</c> aside -- are declared on the concrete
    /// <see cref="MqttClient"/> and not on the interface it implements:
    /// <see cref="ConnectionClosedRequest"/> (the broker asked to close) and
    /// <see cref="ConnectionOpened"/> (a session came up, which is what tells a consumer
    /// its subscriptions and announcements need replaying). So the wrapper had to hold a
    /// concrete <see cref="MqttClient"/>, and nothing could stand in for it.
    /// </para>
    /// <para>
    /// This interface is the difference and nothing more: <see cref="IMqttClient"/> plus
    /// those two events. It is deliberately not the same seam as
    /// <see cref="IReconnectingMqttClient"/>, which is what a <em>consumer</em> of the
    /// wrapper depends on. A consumer has no business with
    /// <see cref="ConnectionClosedRequest"/> -- handling that is the wrapper's reason to
    /// exist -- so the two interfaces stay separate rather than one deriving from the
    /// other.
    /// </para>
    /// <para>
    /// The real implementation is <see cref="MqttClientTransport"/>, which needs no
    /// forwarding code at all.
    /// </para>
    /// </remarks>
    public interface IMqttTransport : IMqttClient
    {
        /// <summary>Raised when a session has come up, after CONNACK.</summary>
        event MqttClient.ConnectionOpenedEventHandler ConnectionOpened;

        /// <summary>Raised when the peer asked for the connection to be closed.</summary>
        event MqttClient.ConnectionClosedRequestEventHandler ConnectionClosedRequest;
    }
}
