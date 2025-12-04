using HomieNano.Version4.Properties;

namespace HomieNano.Version4.EventArgs
{
    public class EnumPropertySetEventArgs : System.EventArgs
    {
        public EnumPropertySetEventArgs(EnumProperty property, string value)
        {
            Property = property;
            Value = value;
        }

        public EnumProperty Property { get; }
        public string Value { get; }
    }
}
