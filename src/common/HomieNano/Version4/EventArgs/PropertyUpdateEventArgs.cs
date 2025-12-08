using HomieNano.Version4.Properties;

namespace HomieNano.Version4.EventArgs
{
    public class PropertyUpdateEventArgs : System.EventArgs
    {
        internal PropertyUpdateEventArgs(PropertyBase property, byte[] value)
        {
            Property = property;
            Value = value;
        }

        public PropertyBase Property { get; }

        public byte[] Value { get; }
    }
}
