using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.EventArgs;
using System.Text;

namespace SmartHome.Homie.V4.Properties
{
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

        public override event PropertyUpdateHandler? OnUpdate;

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(Value);

        public void Update(string newValue)
        {
            Value = newValue;
            PropertyUpdateEventArgs args = new(this, Encoding.UTF8.GetBytes(newValue));
            OnUpdate?.Invoke(args);
        }

        internal override void SetInternal(string value)
        {
            Update(value);
        }
    }
}
