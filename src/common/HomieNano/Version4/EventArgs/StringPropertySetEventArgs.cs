using HomieNano.Version4.Properties;

namespace HomieNano.Version4.EventArgs
{
    public class StringPropertySetEventArgs : System.EventArgs
    {
        public StringPropertySetEventArgs(StringProperty property, string value)
        {
            Property = property;
            Value = value;
        }

        public StringProperty Property { get; }
        public string Value { get; }
    }
}
