using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.Properties;

namespace SmartHome.DeviceModel.Builder
{
    /// <summary>
    /// Builds a Homie v5 JSON property. A Homie v4 adapter has no such datatype and is
    /// expected to refuse the device rather than invent a spelling for it.
    /// </summary>
    public class JsonPropertyBuilder : PropertyBuilderBase
    {
        private readonly string _initialValue;
        private string? _schema;

        internal JsonPropertyBuilder(NodeBuilder nodeBuilder, string id, string name, string initialValue = "{}")
            : base(nodeBuilder, id, name, DataType.Json)
        {
            _initialValue = initialValue;
        }

        public override NodeBuilder BuildProperty() => BuildProperty(out _);

        public NodeBuilder BuildProperty(out JsonProperty property)
        {
            property = new JsonProperty(_id, _name, _schema, _settable, _retained, _unit, _quantityKind, _initialValue);
            _nodeBuilder.PushProperty(property);
            return _nodeBuilder;
        }

        /// <summary>
        /// The JSON schema this property declares, as text. Carried through to adapters
        /// that publish it, and never enforced here -- see <see cref="JsonProperty"/>.
        /// </summary>
        public JsonPropertyBuilder WithSchema(string schema)
        {
            _schema = schema;
            return this;
        }

        public JsonPropertyBuilder WithSettable(bool settable)
        {
            _settable = settable;
            return this;
        }

        public JsonPropertyBuilder WithRetained(bool retained)
        {
            _retained = retained;
            return this;
        }

        /// <summary>
        /// The unit, e.g. <see cref="Units.DegreeCelsius"/>. Any string is allowed; see
        /// <see cref="Units"/> for the well-known ones.
        /// </summary>
        public JsonPropertyBuilder WithUnit(string unit)
        {
            _unit = unit;
            return this;
        }

        /// <summary>
        /// What the value means, independently of its unit -- the difference between a
        /// humidity in <c>%</c> and a battery charge in <c>%</c>. Ignored by the Homie
        /// adapters; the source of Home Assistant's device class.
        /// </summary>
        public JsonPropertyBuilder WithQuantityKind(QuantityKind quantityKind)
        {
            _quantityKind = quantityKind;
            return this;
        }
    }
}
