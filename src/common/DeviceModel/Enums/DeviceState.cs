namespace SmartHome.DeviceModel.Enums
{
    /// <summary>
    /// Where a device is in its lifecycle.
    /// </summary>
    /// <remarks>
    /// Named for what the device is doing rather than for what any one convention calls
    /// it. The mapping to a wire token belongs to the adapter, and conventions vary
    /// widely: some publish a lifecycle attribute with tokens of their own, others have
    /// no lifecycle attribute at all and express the same facts through an availability
    /// topic.
    ///
    /// There is deliberately no <c>Alert</c> state. A lifecycle state can say only
    /// *that* something is wrong, where an alert carries an id and a message, which is
    /// strictly more information. The model therefore keeps alerts separate
    /// (<see cref="SmartHome.DeviceModel.Device.RaiseAlert"/>). An adapter whose
    /// convention has only the coarser spelling synthesises it from "any alert is
    /// raised"; that mapping is one-way and lossy, which is why it lives in the adapter
    /// and not here.
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
        /// one state is enough to carry it. Conventions that want it announced expect a
        /// device to say so before it disconnects.
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
