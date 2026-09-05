using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.EventArgs;
using System.Text;

namespace SmartHome.DeviceModel.Properties
{
    /// <summary>
    /// Structured data that has no single-value spelling, carried as a JSON array or
    /// object.
    /// </summary>
    /// <remarks>
    /// Homie v5 only; a v4 adapter must refuse a device carrying one. The convention is
    /// specific that the payload must be an array or an object -- a bare number, string
    /// or boolean is a case for one of the ordinary datatypes, not for this one.
    ///
    /// The schema this property may declare is carried and never enforced. Validating a
    /// payload against a JSON schema needs a JSON parser and a schema engine, neither of
    /// which belongs in a device model that is otherwise the size of a header file, and
    /// the convention itself tells a consumer that fails to compile a schema to ignore it.
    /// </remarks>
    public class JsonProperty : PropertyBase
    {
        public JsonProperty(
            string id,
            string name,
            string? schema = null,
            bool settable = false,
            bool retained = true,
            string unit = Units.None,
            QuantityKind quantityKind = QuantityKind.None,
            string initialValue = "{}")
            : base(id, name, DataType.Json, settable, retained, unit, quantityKind)
        {
            Schema = schema;
            Value = initialValue;
        }

        /// <summary>The raw JSON text.</summary>
        public string Value { get; private set; }

        /// <summary>
        /// The JSON schema this property declares, as text, or null if it declares none.
        /// Published by an adapter that has somewhere to put it; never enforced here.
        /// </summary>
        public string? Schema { get; }

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

        /// <remarks>
        /// A shape check, not a parse, and deliberately labelled as one: the payload has
        /// to open and close as an array or an object. That refuses the whole class of
        /// payload the convention rules out -- a number, a quoted string, a bare
        /// <c>true</c> -- without this assembly acquiring a JSON parser it would then
        /// have to keep correct. What it does not do is prove the contents are
        /// well-formed, and a consumer still has to parse defensively.
        /// </remarks>
        internal override string? Validate(string value)
        {
            if (value == null)
            {
                return "not a JSON array or object";
            }

            var start = 0;
            while (start < value.Length && IsJsonWhitespace(value[start]))
            {
                start++;
            }

            var end = value.Length - 1;
            while (end > start && IsJsonWhitespace(value[end]))
            {
                end--;
            }

            if (start > end)
            {
                return "not a JSON array or object";
            }

            var isObject = value[start] == '{' && value[end] == '}';
            var isArray = value[start] == '[' && value[end] == ']';

            if (!isObject && !isArray)
            {
                return "not a JSON array or object";
            }

            return null;
        }

        internal override void SetInternal(string value) => Update(value);

        private static bool IsJsonWhitespace(char c) => c == ' ' || c == '\t' || c == '\r' || c == '\n';
    }
}
