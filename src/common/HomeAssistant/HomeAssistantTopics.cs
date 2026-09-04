namespace SmartHome.HomeAssistant
{
    /// <summary>
    /// The topics Home Assistant's MQTT discovery defines, and the one this library builds.
    /// </summary>
    public static class HomeAssistantTopics
    {
        /// <summary>
        /// Default discovery prefix. Configurable in Home Assistant, so
        /// <see cref="HomeAssistantSettings.DiscoveryPrefix"/> can override it.
        /// </summary>
        public const string DefaultDiscoveryPrefix = "homeassistant";

        /// <summary>
        /// Where Home Assistant publishes its own birth ("online") and will ("offline").
        /// </summary>
        /// <remarks>
        /// Not derived from the discovery prefix. Home Assistant's birth topic is
        /// configured separately from it and defaults to this literal, so a deployment
        /// that moved the discovery prefix has almost certainly not moved this.
        /// </remarks>
        public const string StatusTopic = "homeassistant/status";

        /// <summary>The payload Home Assistant's birth message carries.</summary>
        public const string StatusOnline = "online";

        /// <summary>
        /// <c>&lt;prefix&gt;/&lt;component&gt;/&lt;object-id&gt;/config</c>: the single-component
        /// discovery topic.
        /// </summary>
        /// <remarks>
        /// The optional <c>&lt;node-id&gt;</c> level is deliberately not used. Home
        /// Assistant's own guidance is that an entity carrying a <c>unique_id</c> should
        /// put that id in <c>&lt;object-id&gt;</c> and omit the node level, and the id
        /// built by <see cref="DiscoveryMapper"/> already spans device, node and property
        /// -- so the node level would only repeat a component of the object id.
        /// </remarks>
        public static string ConfigTopic(string discoveryPrefix, string component, string objectId) =>
            $"{discoveryPrefix}/{component}/{objectId}/config";
    }
}
