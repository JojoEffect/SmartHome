namespace SmartHome.Homie.V4.Settings
{
    /// <summary>
    /// The device attributes v4 requires that the neutral model deliberately does not
    /// carry.
    /// </summary>
    /// <remarks>
    /// Both of these describe the *implementation of a convention*, not the device: an
    /// extension list is a statement about which v4 extensions this device speaks, and
    /// the implementation string names the library publishing it. A model shared by
    /// several adapters has nowhere to put either, so they are declared here, to the
    /// adapter that publishes them.
    /// </remarks>
    public class HomieDeviceSettings
    {
        /// <summary>
        /// The v4 extensions this device implements, published comma-joined as
        /// <c>$extensions</c>.
        /// </summary>
        /// <remarks>
        /// Mandatory whether or not there are any: "The following device attributes are
        /// mandatory and MUST be send, even if it is just an empty string." Note that an
        /// empty value does not survive in the broker's retained store -- MQTT defines a
        /// zero-length retained payload as deleting the retained message -- so a
        /// conformance check has to assert it on the live stream, not the store.
        /// </remarks>
        public string[] Extensions { get; set; } = new string[0];

        /// <summary>
        /// What is publishing this device, e.g. a firmware name, published as
        /// <c>$implementation</c>. Left empty, the attribute is not published at all.
        /// </summary>
        /// <remarks>
        /// Optional in v4, and omitted rather than published empty: an empty retained
        /// payload deletes whatever a previous run left in the store, which says
        /// something different from "this device does not name its implementation".
        /// </remarks>
        public string? Implementation { get; set; } = null;
    }
}
