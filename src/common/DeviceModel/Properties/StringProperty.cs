using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.EventArgs;
using System.Text;

namespace SmartHome.DeviceModel.Properties
{
    public class StringProperty : PropertyBase
    {
        public StringProperty(
            string id,
            string name,
            bool settable = false,
            bool retained = true,
            string unit = Units.None,
            QuantityKind quantityKind = QuantityKind.None,
            string initialValue = "")
            : base(id, name, DataType.String, settable, retained, unit, quantityKind)
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

        /// <summary>Declares the value this property is heading for. See <see cref="PropertyBase.Target"/>.</summary>
        public void SetTarget(string value) => SetTargetPayload(value);

        // Any payload is a valid string, and no convention this model targets gives a
        // string property a format -- so there is nothing declared for a payload to
        // violate, and this type carries no format at all.
        internal override string? Validate(string value) => null;

        internal override void SetInternal(string value) => Update(value);
    }
}
