using SmartHome.Homie.V4.Properties;

namespace SmartHome.Homie.V4.EventArgs
{
    /// <summary>
    /// A controller wrote to a settable property's <c>/set</c> topic.
    /// </summary>
    /// <remarks>
    /// The property has already been updated by the time this is raised, and the new
    /// value has been reflected back to the property topic -- the spec requires the
    /// device to publish the processed value "as soon as possible".
    ///
    /// That reflection is optimistic: it is the *command*, published before anyone knows
    /// whether it can be honoured. For an ordinary property that is right and a handler
    /// has nothing to publish. For a property whose value is a *request the device can
    /// turn down* -- an illegal $state transition, an out of range setpoint, a relay that
    /// failed to close -- the handler must publish the device's real value over the
    /// reflection once the command has been applied or refused, or the retained store is
    /// left advertising a value the device is not in, to every controller that connects
    /// afterwards. The correction goes *after* the reflection, never instead of it: the
    /// library's publish is already out by the time this is raised. HomieClientCheck's
    /// 'lifecycle' property is the worked example, and the conformance suite asserts the
    /// order on the wire.
    ///
    /// This is the app's job rather than the library's on purpose -- see issue #33.
    ///
    /// Handlers run on M2Mqtt's event-dispatch thread, the one thread that also carries
    /// SUBACK, PUBACK, UNSUBACK and the connection-closed signal. Don't block it waiting
    /// for hardware: hand slow work to another thread, return, and publish the outcome
    /// onto the property when it arrives.
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
