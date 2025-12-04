using HomieNano.Version4.Properties;

namespace HomieNano.Version4.EventArgs
{
    public class FloatPropertySetEventArgs : System.EventArgs
    {
        public FloatPropertySetEventArgs(FloatProperty property, double value)
        {
            Property = property;
            Value = value;
        }

        public FloatProperty Property { get; }
        public double Value { get; }
    }
}
