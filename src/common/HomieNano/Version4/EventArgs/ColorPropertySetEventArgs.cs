using HomieNano.Version4.Properties;

namespace HomieNano.Version4.EventArgs
{
    public class ColorPropertySetEventArgs : System.EventArgs
    {
        public ColorPropertySetEventArgs(ColorProperty property, HomieColor value)
        {
            Property = property;
            Value = value;
        }

        public ColorProperty Property { get; }
        public HomieColor Value { get; }
    }
}
