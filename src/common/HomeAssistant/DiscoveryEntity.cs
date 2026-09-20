namespace SmartHome.HomeAssistant
{
    /// <summary>
    /// One rendered discovery message: where it goes, and what it says.
    /// </summary>
    /// <remarks>
    /// Deliberately inert. <see cref="DiscoveryMapper"/> is a pure function from a
    /// <c>Device</c> to an array of these, which is what lets the whole mapping -- every
    /// topic, every payload key, every device class -- be asserted in the unit suite,
    /// where CI can run it without a broker, a device or a Home Assistant.
    ///
    /// What that still cannot say is whether Home Assistant *accepts* the result. A
    /// discovery config can be well-formed JSON, use only real keys, and still be dropped
    /// by a validator that lives in Home Assistant's own source. Asserting acceptance
    /// needs a real instance, and is deferred to its own issue.
    /// </remarks>
    public sealed class DiscoveryEntity
    {
        internal DiscoveryEntity(string topic, string payload)
        {
            Topic = topic;
            Payload = payload;
        }

        /// <summary>The retained discovery topic this configuration is published to.</summary>
        public string Topic { get; }

        /// <summary>The discovery configuration, as JSON.</summary>
        public string Payload { get; }
    }
}
