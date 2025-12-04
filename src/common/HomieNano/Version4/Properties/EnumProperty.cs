using HomieNano.Version4.Enums;
using HomieNano.Version4.EventArgs;
using System.Text;

namespace HomieNano.Version4.Properties
{
    public delegate void EnumPropertySetHandler(EnumPropertySetEventArgs args);

    public class EnumProperty : PropertyBase
    {
        public EnumProperty(
            string topicId,
            string name,
            string format = "",
            bool settable = false,
            bool retained = true,
            Unit unit = Unit.None,
            string initialValue = "")
            : base(topicId, name, DataType.Enum, format, settable, retained, unit)
        {
            Value = initialValue;
        }

        public string Value { get; private set; }

        public event EnumPropertySetHandler? OnSet;

        public override event PropertyUpdateHandler? OnUpdate;

        public void Update(string newValue)
        {
            Value = newValue;
            PropertyUpdateEventArgs args = new(this, Encoding.UTF8.GetBytes(newValue));
            OnUpdate?.Invoke(args);
        }

        internal override void SetInternal(string value)
        {
            Value = value;
            EnumPropertySetEventArgs enumArgs = new(this, value);
            OnSet?.Invoke(enumArgs);
        }
    }
}
