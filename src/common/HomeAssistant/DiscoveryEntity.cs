namespace SmartHome.HomeAssistant
{
    /// <summary>
    /// One rendered Home Assistant discovery message: where it goes, and what it says.
    /// </summary>
    /// <remarks>
    /// Deliberately inert. <see cref="DiscoveryMapper"/> is a pure function from a Homie
    /// <see cref="SmartHome.Homie.V4.Device"/> to an array of these, which is what lets the whole
    /// mapping -- every topic, every payload key, every inferred device class -- be
    /// asserted in the unit suite, where CI can run it. Only
    /// <see cref="HomeAssistantAnnouncer"/> needs a broker.
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
