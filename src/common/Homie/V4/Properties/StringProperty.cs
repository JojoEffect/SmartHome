using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.EventArgs;
using System.Text;

namespace SmartHome.Homie.V4.Properties
{
    public class StringProperty : PropertyBase
    {
        public StringProperty(
            string topicId, 
            string name,
            string format = "", 
            bool settable = false, 
            bool retained = true, 
            Unit unit = Unit.None,
            string initialValue = "") 
            : base(topicId, name, DataType.String, format, settable, retained, unit)
        {
            Value = initialValue;
        }

        public string Value { get; private set; }

        /// <inheritdoc/>
        public override event PropertyUpdateHandler? OnUpdate;

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(Value);

        public void Update(string newValue)
        {
            Value = newValue;
            PropertyUpdateEventArgs args = new(this, Encoding.UTF8.GetBytes(newValue));
            OnUpdate?.Invoke(args);
        }

        // Any payload is a valid Homie string, and $format carries no meaning for this
        // datatype in Homie v4 -- so there is nothing declared for a payload to violate.
        internal override string? Validate(string value) => null;

        internal override void SetInternal(string value) => Update(value);
    }
}
