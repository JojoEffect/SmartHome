using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.EventArgs;
using SmartHome.DeviceModel.Formats;
using System.Text;

namespace SmartHome.DeviceModel.Properties
{
    public class IntegerProperty : PropertyBase
    {
        public IntegerProperty(
            string id,
            string name,
            NumericRange? range = null,
            bool settable = false,
            bool retained = true,
            string unit = Units.None,
            QuantityKind quantityKind = QuantityKind.None,
            int initialValue = 0)
            : base(id, name, DataType.Integer, settable, retained, unit, quantityKind)
        {
            Range = range;
            Value = initialValue;
        }

        public int Value { get; private set; }

        /// <summary>The bounds this property declares, or null if it declares none.</summary>
        public NumericRange? Range { get; }

        public override event PropertyUpdateHandler? OnUpdate;

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(Value.ToString());

        public void Update(int newValue)
        {
            Value = newValue;
            PropertyUpdateEventArgs args = new(this, Encoding.UTF8.GetBytes(newValue.ToString()));
            OnUpdate?.Invoke(args);
        }

        /// <summary>Declares the value this property is heading for. See <see cref="PropertyBase.Target"/>.</summary>
        public void SetTarget(int value) => SetTargetPayload(value.ToString());

        internal override string? Validate(string value)
        {
            if (!int.TryParse(value, out var parsed))
            {
                return "not an integer";
            }

            if (Range != null && !Range.Contains(parsed))
            {
                return $"outside the range '{Range}' the format declares";
            }

            return null;
        }

        internal override void SetInternal(string value)
        {
            // Validate() has already proven this parses; the guard is what keeps that
            // structural rather than a comment.
            if (int.TryParse(value, out var parsed))
            {
                Update(parsed);
            }
        }
    }
}
