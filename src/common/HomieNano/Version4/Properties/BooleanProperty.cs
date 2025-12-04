using HomieNano.Version4.Enums;
using HomieNano.Version4.EventArgs;
using System.Text;

namespace HomieNano.Version4.Properties
{
    public delegate void BooleanPropertySetHandler(BooleanPropertySetEventArgs args);

    public class BooleanProperty : PropertyBase
    {
        public BooleanProperty(
            string topicId,
            string name,
            string format = "",
            bool settable = false,
            bool retained = true,
            Unit unit = Unit.None,
            bool initialValue = false)
            : base(topicId, name, DataType.Boolean, format, settable, retained, unit)
        {
            Value = initialValue;
        }

        public bool Value { get; private set; }

        public event BooleanPropertySetHandler? OnSet;

        public override event PropertyUpdateHandler? OnUpdate;

        public void Update(bool newValue)
        {
            Value = newValue;
            PropertyUpdateEventArgs args = new(this, Encoding.UTF8.GetBytes(newValue ? "true" : "false"));
            OnUpdate?.Invoke(args);
        }

        internal override void SetInternal(string value)
        {
            bool parsed = value == "true" || value == "1" || value == "True";
            Value = parsed;
            BooleanPropertySetEventArgs boolArgs = new(this, parsed);
            OnSet?.Invoke(boolArgs);
        }
    }
}
