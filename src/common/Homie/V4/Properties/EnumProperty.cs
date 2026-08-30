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

        internal override string? Validate(string value)
        {
            var format = FormatAttribute.Value;

            // Homie v4 requires $format on an enum, but a property that declares none has
            // declared nothing to violate. Rejecting every payload here would turn a
            // device author's omission into a property no controller can ever set, which
            // is a worse failure than the one this validation exists to stop.
            if (format == null || format.Length == 0)
            {
                return null;
            }

            // The format entries are trimmed, the payload is not. A device author writing
            // "ready, alert" means the same three names; a controller sending " ready"
            // is sending a different value.
            foreach (var candidate in format.Split(','))
            {
                if (candidate.Trim() == value)
                {
                    return null;
                }
            }

            return $"not one of the values '{format}' declared by $format";
        }

        internal override void SetInternal(string value)
        {
            Update(value);
        }
    }
}
