using SmartHome.Homie.V4.Properties;

namespace SmartHome.Homie.V4.EventArgs
{
    /// <summary>
    /// A controller wrote to a settable property's <c>/set</c> topic.
    /// </summary>
    /// <remarks>
    /// The property has already been updated by the time this is raised, and the new
    /// value has been reflected back to the property topic -- the spec requires the
    /// device to publish the processed value "as soon as possible". Handlers act on the
    /// command (drive a relay, start a pump); they don't have to publish anything.
    /// </remarks>
    public class HomieCommandEventArgs : System.EventArgs
    {
        internal HomieCommandEventArgs(PropertyBase property, byte[] payload)
        {
            Property = property;
            Payload = payload;
        }

        /// <summary>The settable property the controller addressed.</summary>
        public PropertyBase Property { get; }

        /// <summary>The raw payload the controller sent, before parsing.</summary>
        public byte[] Payload { get; }
    }
}
