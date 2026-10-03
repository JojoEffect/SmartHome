namespace SmartHome.HomeAssistant
{
    /// <summary>
    /// What a discovery payload says about the device beyond what the model carries.
    /// </summary>
    /// <remarks>
    /// Everything here is optional and defaulted: the model already carries the id, the
    /// name and the whole property tree, which is the bulk of a discovery payload. What
    /// it has no vocabulary for is who built the device, which firmware it runs, and
    /// which application published the discovery message -- the last of which Home
    /// Assistant logs whenever it discovers or updates an entity, and is the first thing
    /// anyone asks when an unexpected entity appears.
    ///
    /// These are the adapter's, not the model's, for the same reason a convention's
    /// extension list is: they describe an implementation of a convention rather than the
    /// device.
    /// </remarks>
    public sealed class HomeAssistantSettings
    {
        /// <summary>
        /// Discovery topic prefix. Only change this if Home Assistant's own discovery
        /// prefix was changed from its default.
        /// </summary>
        public string DiscoveryPrefix { get; set; } = HomeAssistantTopics.DefaultDiscoveryPrefix;

        /// <summary>Device manufacturer, shown on the Home Assistant device page.</summary>
        public string Manufacturer { get; set; } = "SmartHome";

        /// <summary>Device model, shown on the Home Assistant device page.</summary>
        public string? Model { get; set; }

        /// <summary>Firmware version, shown on the Home Assistant device page.</summary>
        public string? SoftwareVersion { get; set; }

        /// <summary>
        /// Name of the application publishing the discovery message. Home Assistant
        /// requires this whenever an <c>origin</c> block is present, so it is defaulted
        /// rather than nullable.
        /// </summary>
        public string OriginName { get; set; } = "SmartHome.HomeAssistant";

        /// <summary>Version of the application publishing the discovery message.</summary>
        public string? OriginSoftwareVersion { get; set; }

        /// <summary>Support URL for the application publishing the discovery message.</summary>
        public string? OriginSupportUrl { get; set; }

        /// <summary>
        /// Seconds after which Home Assistant marks a sensor's value unknown if nothing
        /// new arrives. Zero omits it.
        /// </summary>
        /// <remarks>
        /// Worth setting, and the reason is what this adapter does with alerts: a device
        /// that has raised one is still reported *available*, because it genuinely is
        /// reachable and its other properties may be fine. A sensor whose readings have
        /// stopped -- which is exactly when a device raises an alert about its sensor --
        /// would otherwise go on showing its last good value indefinitely, live and
        /// wrong. Only an expiry can say that value went stale.
        ///
        /// Set it to a small multiple of the device's publish interval, enough that an
        /// ordinary late reading does not trip it.
        ///
        /// It is applied to sensors only. A binary sensor is usually change-driven -- a
        /// window contact publishes when the window moves and then stays silent for
        /// months -- so an expiry there would report a perfectly healthy device as
        /// unavailable.
        /// </remarks>
        public int ExpireAfterSeconds { get; set; } = 0;
    }
}
