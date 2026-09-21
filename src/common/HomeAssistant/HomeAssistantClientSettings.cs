namespace SmartHome.HomeAssistant
{
    /// <summary>
    /// The MQTT session this adapter opens: who it connects as, and how it stays
    /// connected.
    /// </summary>
    /// <remarks>
    /// The last will is deliberately not here. It is not a choice a caller gets to make:
    /// an adapter whose availability lives on one topic has exactly one will to declare,
    /// and letting it be configured would let a device be built that announces itself and
    /// then never goes offline when it dies.
    ///
    /// A near-copy of the Homie adapter's own settings type, which is the cost of the
    /// rule that neither adapter references the other. Sharing it would mean a type that
    /// belongs to one convention appearing in the other's public surface, or a third
    /// library holding five properties.
    /// </remarks>
    public sealed class HomeAssistantClientSettings
    {
        /// <summary>
        /// MQTT client id. Left empty, the client fills it with the device's own id.
        /// </summary>
        /// <remarks>
        /// Deliberately not defaulted to a fresh Guid. With a per-boot random id the
        /// broker keeps the dead session alive until its keep-alive expires, so the old
        /// session's will is delivered *after* the rebooted device has already published
        /// that it is online -- leaving the retained availability at 'offline' while the
        /// device runs. A stable id makes the new connection take the session over
        /// instead, so the two stay ordered.
        /// </remarks>
        public string? ClientId { get; set; } = null;

        public string? UserName { get; set; } = null;

        public string? Password { get; set; } = null;

        public bool CleanSession { get; set; } = true;

        public ushort KeepAlivePeriod { get; set; } = 10;
    }
}
