using SmartHome.DeviceModel.Enums;

namespace SmartHome.Protocol
{
    /// <summary>
    /// Raised when a controller writes to a settable property.
    /// </summary>
    public delegate void DeviceCommandHandler(DeviceCommandEventArgs args);

    /// <summary>
    /// The seam between a device app and the MQTT convention it speaks.
    /// </summary>
    /// <remarks>
    /// An app builds a <c>Device</c> with the neutral builder, hands it to whichever
    /// adapter is compiled in, and then talks only to this interface: connect, announce,
    /// go to sleep, say what is wrong, act on commands. Which convention that goes out
    /// as -- Homie v4, Homie v5, Home Assistant MQTT Discovery -- is a choice about which
    /// implementation is constructed, and nothing above this line changes with it.
    ///
    /// Deliberately NOT derived from <c>IReconnectingMqttClient</c>. A device *owns* an
    /// MQTT connection, it is not itself one -- and exposing Publish/Subscribe here would
    /// let an app publish an attribute non-retained, publish a lifecycle state out of
    /// order, or open a session without the required last will, each of which breaks the
    /// convention an implementation exists to keep.
    ///
    /// An implementation takes an <c>IReconnectingMqttClient</c> by constructor injection
    /// and owns the session on it, including the last will: Homie requires the connection
    /// to carry one setting the device's state to <c>lost</c>, and a will can only be
    /// declared in CONNECT. A session someone else opened cannot have it, so an
    /// implementation must replace such a session rather than reuse it.
    ///
    /// The device model says what the device *is*; this interface is what you *do* with
    /// it.
    /// </remarks>
    public interface IDeviceProtocol
    {
        /// <summary>The device's id, as the convention will address it.</summary>
        string DeviceId { get; }

        /// <summary>Where the device is in its lifecycle.</summary>
        DeviceState State { get; }

        /// <summary>Whether the underlying MQTT session is up.</summary>
        bool IsConnected { get; }

        /// <summary>
        /// Connects, announces the device and its nodes, and reaches
        /// <see cref="DeviceState.Ready"/>. Returns false rather than throwing, so
        /// callers can retry.
        /// </summary>
        bool Connect();

        /// <summary>
        /// Calls <see cref="Connect"/> until it succeeds or the attempts run out.
        /// </summary>
        /// <remarks>
        /// Here rather than in each app. Every device that connects has to retry -- the
        /// broker is routinely not up yet at boot, and the integration suite cycles it
        /// deliberately -- so each app had grown its own copy of the same loop, differing
        /// only in the attempt count and in whether exhaustion returned false or threw.
        /// That mattered beyond the duplication: the requirement that Connect() be safe
        /// to call repeatedly is a property of this interface, and it had to be
        /// rediscovered and re-checked against every hand-rolled call site.
        /// </remarks>
        /// <returns>False when every attempt failed.</returns>
        bool ConnectWithRetry(int maxAttempts = 10, int retryDelayMs = 3000);

        /// <summary>
        /// Announces that the device is disconnecting and closes the session. The
        /// conventions require this before a clean disconnect.
        /// </summary>
        void Disconnect();

        /// <summary>
        /// Returns the device to <see cref="DeviceState.Ready"/>, e.g. after
        /// <see cref="Sleep"/>.
        /// </summary>
        void Ready();

        /// <summary>
        /// Moves the device to <see cref="DeviceState.Sleeping"/>. The conventions
        /// require this before a device sleeps.
        /// </summary>
        void Sleep();

        /// <summary>
        /// Says what is wrong, under an id that names the condition.
        /// </summary>
        /// <remarks>
        /// An id and a message, rather than a bare "something is wrong": a device with a
        /// flat battery and a device with an unreadable sensor are different situations,
        /// and the state-only spelling this replaces could not tell a controller which
        /// it was looking at. An adapter whose convention has no room for the detail --
        /// Homie v4 has one <c>alert</c> state and nothing else -- still gets to publish
        /// the fact, and logs the rest.
        ///
        /// The id follows the same character rule as a node or property id, because
        /// Homie v5 publishes it as a topic level.
        /// </remarks>
        /// <param name="id">What is wrong, e.g. <c>battery</c>.</param>
        /// <param name="message">The human-readable description.</param>
        void RaiseAlert(string id, string message);

        /// <summary>Clears an alert, e.g. because the condition is resolved.</summary>
        void ClearAlert(string id);

        /// <summary>
        /// Raised when a controller sets a settable property, as opposed to the device
        /// updating its own value. Subscribe to this to *act* on a command; the
        /// property's own <c>OnUpdate</c> fires for both cases and cannot tell them apart.
        /// </summary>
        event DeviceCommandHandler? OnCommand;
    }
}
