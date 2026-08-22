using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.EventArgs;
using System.Text;

namespace SmartHome.Homie.V4.Properties
{
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

        public override event PropertyUpdateHandler? OnUpdate;

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(Value.ToString());

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
                Update(parsed);
            }
        }
    }
}
