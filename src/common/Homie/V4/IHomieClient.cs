using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.EventArgs;

namespace SmartHome.Homie.V4
{
    /// <summary>
    /// Raised when a controller writes to a settable property's <c>/set</c> topic.
    /// </summary>
    public delegate void HomieCommandHandler(HomieCommandEventArgs args);

    /// <summary>
    /// Operates one Homie v4 device over MQTT: its connection, its lifecycle state and
    /// the commands a controller sends it.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT derived from IReconnectingMqttClient. A Homie device *owns* an
    /// MQTT connection (the spec requires one connection per device), it is not itself
    /// one -- and exposing Publish/Subscribe here would let an app publish an attribute
    /// non-retained, publish $state out of lifecycle order, or open a session without
    /// the required last will, each of which breaks the convention this type exists to
    /// keep.
    ///
    /// The device model (<see cref="Device"/>, built with HomieDeviceBuilder) says what
    /// the device *is*; this interface is what you *do* with it.
    /// </remarks>
    public interface IHomieClient
    {
        /// <summary>The device's Homie id, i.e. the topic level under <c>homie/</c>.</summary>
        string DeviceId { get; }

        /// <summary>The device's current <c>$state</c>.</summary>
        State State { get; }

        /// <summary>Whether the underlying MQTT session is up.</summary>
        bool IsConnected { get; }

        /// <summary>
        /// Connects, announces the device and its nodes, and reaches <c>ready</c>.
        /// Returns false rather than throwing, so callers can retry.
        /// </summary>
        bool Connect();

        /// <summary>
        /// Publishes <c>disconnected</c> and closes the session. The spec: "You must
        /// send this message before cleanly disconnecting."
        /// </summary>
        void Disconnect();

        /// <summary>
        /// Moves the device to <c>alert</c>. The spec: "send this message when something
        /// is wrong". Recover with <see cref="Ready"/> once it isn't.
        /// </summary>
        /// <remarks>
        /// Takes no reason: core Homie v4 has nowhere to put one -- <c>$state</c> carries
        /// the single token <c>alert</c> and nothing else. Log the reason instead.
        /// </remarks>
        /// <returns>False when the current state doesn't allow the transition.</returns>
        bool Alert();

        /// <summary>
        /// Moves the device to <c>sleeping</c>. The spec: "You have to send this message
        /// before sleeping."
        /// </summary>
        /// <returns>False when the current state doesn't allow the transition.</returns>
        bool Sleep();

        /// <summary>
        /// Returns the device to <c>ready</c>, e.g. after <see cref="Alert"/> or
        /// <see cref="Sleep"/>.
        /// </summary>
        /// <returns>False when the current state doesn't allow the transition.</returns>
        bool Ready();

        /// <summary>
        /// Raised when a controller sets a settable property, as opposed to the device
        /// updating its own value. Subscribe to this to *act* on a command; the
        /// property's own OnUpdate fires for both cases and cannot tell them apart.
        /// </summary>
        event HomieCommandHandler? OnCommand;
    }
}
