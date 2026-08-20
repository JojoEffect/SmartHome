using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.EventArgs;
using System.Text;

namespace SmartHome.Homie.V4.Properties
{
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

        public override event PropertyUpdateHandler? OnUpdate;

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(Value.ToString());

        public void Update(bool newValue)
        {
            Value = newValue;
            PropertyUpdateEventArgs args = new(this, Encoding.UTF8.GetBytes(newValue ? "true" : "false"));
            OnUpdate?.Invoke(args);
        }

        internal override void SetInternal(string value)
        {
            bool parsed = value == "true" || value == "1" || value == "True";
            Update(parsed);
        }
    }
}
