using SmartHome.DeviceModel.Enums;

namespace SmartHome.Homie.V4
{
    /// <summary>
    /// The <c>$state</c> vocabulary of Homie v4, and the mapping from the model's
    /// lifecycle onto it.
    /// </summary>
    /// <remarks>
    /// These tokens are the adapter's, not the model's: <see cref="DeviceState"/> is
    /// named for what a device is doing, and <c>DeviceStateExtensions.GetName()</c>
    /// deliberately returns capitalised names so that publishing one would fail
    /// conformance loudly. This class is where the wire spelling lives.
    ///
    /// Written out rather than left to <c>Enum.ToString()</c>, which reads the enum's
    /// names through reflection -- something a NoReflection firmware target does not
    /// carry.
    /// </remarks>
    public static class HomieStates
    {
        /// <summary>Connected, and still announcing itself.</summary>
        public const string Init = "init";

        /// <summary>Announced, subscribed, and operating.</summary>
        public const string Ready = "ready";

        /// <summary>About to disconnect cleanly.</summary>
        public const string Disconnected = "disconnected";

        /// <summary>Asleep, and not publishing until it wakes.</summary>
        public const string Sleeping = "sleeping";

        /// <summary>
        /// Disconnected without saying so. Published by the broker from the connection's
        /// last will, never by the device.
        /// </summary>
        public const string Lost = "lost";

        /// <summary>Something is wrong. See the remarks on <see cref="From"/>.</summary>
        public const string Alert = "alert";

        /// <summary>
        /// The token a device in this lifecycle state, with or without alerts raised,
        /// goes out as.
        /// </summary>
        /// <remarks>
        /// <c>alert</c> is synthesised here and nowhere else. The model has no alert
        /// state -- it keeps a keyed set of alerts, each with an id and a message, and a
        /// device with one raised is still ready as far as the model is concerned: it is
        /// running and it is publishing. v4's <c>$state</c> can carry only the single
        /// token <c>alert</c>, so the whole set collapses into "any alert is raised".
        /// That mapping is one-way and lossy, which is exactly why it lives in the
        /// adapter: the id and the message have nowhere to go on this wire and stay in
        /// the log.
        ///
        /// Alerts deliberately do not show while the device is sleeping, connecting or
        /// disconnecting. Those three say something about the connection that a
        /// controller needs more than it needs to know something is also wrong, and v4
        /// has one attribute to say it in.
        /// </remarks>
        public static string From(DeviceState state, bool hasAlerts) => state switch
        {
            DeviceState.Connecting => Init,
            DeviceState.Ready => hasAlerts ? Alert : Ready,
            DeviceState.Sleeping => Sleeping,
            DeviceState.Disconnecting => Disconnected,
            DeviceState.Lost => Lost,
            // Unreachable: every member of DeviceState is named above. The arm exists
            // because the expression needs one, and 'disconnected' is the only token
            // that cannot mislead a controller about a device we have just failed to
            // describe.
            _ => Disconnected,
        };
    }
}
