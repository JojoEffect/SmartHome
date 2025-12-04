using HomieNano.Version4.Properties;

namespace HomieNano.Version4.EventArgs
{
    public class BooleanPropertySetEventArgs : System.EventArgs
    {
        public BooleanPropertySetEventArgs(BooleanProperty property, bool value)
        {
            Property = property;
            Value = value;
        }

        public BooleanProperty Property { get; }
        public bool Value { get; }
    }
}
