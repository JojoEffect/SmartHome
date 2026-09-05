using SmartHome.DeviceModel.Properties;

namespace SmartHome.DeviceModel.EventArgs
{
    /// <summary>
    /// A property's value moved, together with the value's canonical encoding.
    /// </summary>
    /// <remarks>
    /// Carries the encoded bytes rather than only the property because that is what an
    /// adapter publishes, and because the encoding is decided by the property itself --
    /// a float's fixed-decimal rendering, a boolean's <c>true</c>/<c>false</c>. An
    /// adapter that re-derived the payload from the typed value would be free to render
    /// it differently, which is how two publishers of the same reading come to disagree.
    /// </remarks>
    public class PropertyUpdateEventArgs : System.EventArgs
    {
        internal PropertyUpdateEventArgs(PropertyBase property, byte[] value)
        {
            Property = property;
            Value = value;
        }

        public PropertyBase Property { get; }

        /// <summary>The new value, UTF-8 encoded exactly as it should go out.</summary>
        public byte[] Value { get; }
    }
}
