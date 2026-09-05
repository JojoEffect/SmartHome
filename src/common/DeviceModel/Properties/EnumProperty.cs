using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.EventArgs;
using SmartHome.DeviceModel.Formats;
using System.Text;

namespace SmartHome.DeviceModel.Properties
{
    public class EnumProperty : PropertyBase
    {
        public EnumProperty(
            string id,
            string name,
            EnumOptions? options = null,
            bool settable = false,
            bool retained = true,
            string unit = Units.None,
            QuantityKind quantityKind = QuantityKind.None,
            string initialValue = "")
            : base(id, name, DataType.Enum, settable, retained, unit, quantityKind)
        {
            Options = options;
            Value = initialValue;
        }

        public string Value { get; private set; }

        /// <summary>The values this property may hold, or null if it declares none.</summary>
        public EnumOptions? Options { get; }

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

        internal override string? Validate(string value)
        {
            // An enum is required to declare its options, but a property that declares
            // none has declared nothing to violate. Rejecting every payload here would
            // turn a device author's omission into a property no controller can ever set,
            // which is a worse failure than the one this validation exists to stop.
            if (Options == null)
            {
                return null;
            }

            // The declared options were trimmed when they were parsed; the payload is
            // not. A device author writing "ready, alert" means the same two names; a
            // controller sending " ready" is sending a different value.
            if (Options.Contains(value))
            {
                return null;
            }

            return $"not one of the values '{Options}' the format declares";
        }

        internal override void SetInternal(string value)
        {
            Update(value);
        }
    }
}
