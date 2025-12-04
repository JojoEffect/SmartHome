using HomieNano.Version4.Properties;

namespace HomieNano.Version4.EventArgs
{
    public class IntegerPropertySetEventArgs : System.EventArgs
    {
        public IntegerPropertySetEventArgs(IntegerProperty property, int value)
        {
            Property = property;
            Value = value;
        }

        public IntegerProperty Property { get; }
        public int Value { get; }
    }
}
