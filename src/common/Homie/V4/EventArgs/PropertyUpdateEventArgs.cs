using SmartHome.Homie.V4.Properties;

namespace SmartHome.Homie.V4.EventArgs
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
