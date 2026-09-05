namespace SmartHome.DeviceModel.Enums
{
    /// <summary>
    /// Where a device is in its lifecycle.
    /// </summary>
    /// <remarks>
    /// The five states Homie v5 defines, which Homie v4 also has, under names that say
    /// what the device is doing rather than what a particular convention calls it. The
    /// mapping to a wire token belongs to the adapter (Homie publishes
    /// <c>init</c>/<c>ready</c>/<c>disconnected</c>/<c>sleeping</c>/<c>lost</c>; Home
    /// Assistant has no lifecycle attribute at all and expresses this through
    /// availability).
    ///
    /// There is deliberately no <c>Alert</c> state. Homie v4 has one, and it can say
    /// only *that* something is wrong; Homie v5 replaced it with alerts carrying an id
    /// and a message, which is strictly more information. The model therefore keeps
    /// alerts separate (<see cref="SmartHome.DeviceModel.Device.RaiseAlert"/>), and a v4 adapter synthesises
    /// its <c>alert</c> state from "any alert is raised". That mapping is one-way and
    /// lossy downward, which is why it lives in the adapter and not here.
    /// </remarks>
    public enum DeviceState
    {
        /// <summary>
        /// Connected to the broker, but not yet finished announcing itself.
        /// </summary>
        /// <remarks>
        /// A device may return here from <see cref="Ready"/> or <see cref="Sleeping"/>
        /// to re-announce -- after a broker restart, for instance, whose retained store
        /// no longer holds anything this device published.
        /// </remarks>
        Connecting = 0,

        /// <summary>Announced, subscribed, and operating.</summary>
        Ready = 1,

        /// <summary>
        /// About to disconnect cleanly, and the state a device that has never connected
        /// starts in.
        /// </summary>
        /// <remarks>
        /// Both meanings are the same fact -- this device is not on the broker -- and
        /// Homie spells them with the single token <c>disconnected</c>, which a device
        /// must publish before it disconnects.
        /// </remarks>
        Disconnecting = 2,

        /// <summary>Asleep, and not publishing until it wakes.</summary>
        Sleeping = 3,

        /// <summary>
        /// Disconnected without saying so. Never entered by the device itself: this is
        /// what the broker publishes on its behalf from the connection's last will, so
        /// nothing may transition *to* it.
        /// </summary>
        Lost = 4,
    }
}
