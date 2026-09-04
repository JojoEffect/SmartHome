namespace SmartHome.HomeAssistant
{
    /// <summary>
    /// What a discovery payload says about the device beyond what Homie already declares.
    /// </summary>
    /// <remarks>
    /// Everything here is optional and defaulted: a Homie <see cref="SmartHome.Homie.V4.Device"/>
    /// already carries the id, the name and the whole property tree, which is the bulk of
    /// a discovery payload. What it has no vocabulary for is who built the device, which
    /// firmware it runs, and which application published the discovery message -- the
    /// last of which Home Assistant logs whenever it discovers or updates an entity, and
    /// is the first thing anyone asks when an unexpected entity appears.
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
        /// Worth setting, and the reason is the availability mapping in
        /// <see cref="DiscoveryMapper"/>: Homie's <c>alert</c> means the device is
        /// connected but something is wrong, and this library reports that as *available*
        /// -- because the device genuinely is reachable and its other properties may be
        /// fine. RoomSensor reaches <c>alert</c> precisely when its sensor stops returning
        /// valid readings, so without this, Home Assistant would go on showing the last
        /// good temperature indefinitely, live and wrong.
        ///
        /// Set it to a small multiple of the device's publish interval -- enough that an
        /// ordinary late reading does not trip it.
        /// </remarks>
        public int ExpireAfterSeconds { get; set; } = 0;
    }
}
