using HomieNano.Version4.Enums;
using HomieNano.Version4.EventArgs;
using System.Text;

namespace HomieNano.Version4.Properties
{
    public delegate void IntegerPropertySetHandler(IntegerPropertySetEventArgs args);

    public class IntegerProperty : PropertyBase
    {
        public IntegerProperty(
            string topicId,
            string name,
            string format = "",
            bool settable = false,
            bool retained = true,
            Unit unit = Unit.None,
            int initialValue = 0)
            : base(topicId, name, DataType.Integer, format, settable, retained, unit)
        {
            Value = initialValue;
        }

        public int Value { get; private set; }

        public event IntegerPropertySetHandler? OnSet;

        public override event PropertyUpdateHandler? OnUpdate;

        public void Update(int newValue)
        {
            Value = newValue;
            PropertyUpdateEventArgs args = new(this, Encoding.UTF8.GetBytes(newValue.ToString()));
            OnUpdate?.Invoke(args);
        }

        internal override void SetInternal(string value)
        {
            if (int.TryParse(value, out var parsed))
            {
                Value = parsed;
                IntegerPropertySetEventArgs intArgs = new(this, parsed);
                OnSet?.Invoke(intArgs);
            }
        }
    }
}
