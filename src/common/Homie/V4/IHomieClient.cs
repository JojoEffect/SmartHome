using SmartHome.Protocol;

namespace SmartHome.Homie.V4
{
    /// <summary>
    /// A device operated over Homie v4: the seam, plus the one thing only this
    /// convention has a word for.
    /// </summary>
    /// <remarks>
    /// Everything an app does with a device -- connect, announce, sleep, say what is
    /// wrong, act on commands -- is <see cref="IDeviceProtocol"/>, and an app that only
    /// needs those should hold that type instead. This interface exists for a caller
    /// that has to reason about the v4 wire itself: the conformance check, and a device
    /// whose own state mirrors what a controller can see.
    ///
    /// Lifecycle transitions must go through this interface -- never through
    /// <c>Device.TryChangeState</c> directly. The model's transition table knows nothing
    /// about v4's rule that an alerting device may not go to sleep, so a direct call can
    /// put a state on the wire that this adapter would have refused, and the wire and
    /// <see cref="HomieState"/> then disagree.
    ///
    /// Deliberately NOT derived from <c>IReconnectingMqttClient</c>, for the reason
    /// <see cref="IDeviceProtocol"/> documents.
    /// </remarks>
    public interface IHomieClient : IDeviceProtocol
    {
        /// <summary>
        /// The <c>$state</c> token the device's lifecycle and raised alerts currently
        /// map to -- see <see cref="HomieStates.From"/>.
        /// </summary>
        /// <remarks>
        /// Computed from the model, not a record of the last publish: it is what the
        /// device *is*, which is what a caller correcting a controller's command needs.
        /// The two differ only inside the window between a state change and its publish.
        /// </remarks>
        string HomieState { get; }
    }
}
