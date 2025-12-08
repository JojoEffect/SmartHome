using HomieNano.Version4.Enums;
using HomieNano.Version4.EventArgs;
using System.Text;

namespace HomieNano.Version4.Properties
{
    public class FloatProperty : PropertyBase
    {
        public FloatProperty(
            string topicId,
            string name,
            string format = "",
            bool settable = false,
            bool retained = true,
            Unit unit = Unit.None,
            double initialValue = 0.0)
            : base(topicId, name, DataType.Float, format, settable, retained, unit)
        {
            Value = initialValue;
        }

        public double Value { get; private set; }

        public override event PropertyUpdateHandler? OnUpdate;

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(Value.ToString());

        public void Update(double newValue)
        {
            Value = newValue;
            PropertyUpdateEventArgs args = new(this, Encoding.UTF8.GetBytes(newValue.ToString()));
            OnUpdate?.Invoke(args);
        }

        internal override void SetInternal(string value)
        {
            if (double.TryParse(value, out var parsed))
            {
                Update(parsed);
            }
        }
    }
}
