using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.Properties;

namespace SmartHome.DeviceModel.Builder
{
    /// <summary>
    /// Builds a Homie v5 datetime property. A Homie v4 adapter has no such datatype and
    /// is expected to refuse the device rather than invent a spelling for it.
    /// </summary>
    public class DateTimePropertyBuilder : PropertyBuilderBase
    {
        internal DateTimePropertyBuilder(NodeBuilder nodeBuilder, string id, string name)
            : base(nodeBuilder, id, name, DataType.DateTime)
        {
        }

        public override NodeBuilder BuildProperty() => BuildProperty(out _);

        public NodeBuilder BuildProperty(out DateTimeProperty property)
        {
            property = new DateTimeProperty(_id, _name, _settable, _retained, _unit, _quantityKind);
            _nodeBuilder.PushProperty(property);
            return _nodeBuilder;
        }

        // No format: an ISO 8601 instant has nothing to restrict.

        public DateTimePropertyBuilder WithSettable(bool settable)
        {
            _settable = settable;
            return this;
        }

        public DateTimePropertyBuilder WithRetained(bool retained)
        {
            _retained = retained;
            return this;
        }

        /// <summary>
        /// The unit, e.g. <see cref="Units.DegreeCelsius"/>. Any string is allowed; see
        /// <see cref="Units"/> for the well-known ones.
        /// </summary>
        public DateTimePropertyBuilder WithUnit(string unit)
        {
            _unit = unit;
            return this;
        }

        /// <summary>
        /// What the value means, independently of its unit -- the difference between a
        /// humidity in <c>%</c> and a battery charge in <c>%</c>. Ignored by the Homie
        /// adapters; the source of Home Assistant's device class.
        /// </summary>
        public DateTimePropertyBuilder WithQuantityKind(QuantityKind quantityKind)
        {
            _quantityKind = quantityKind;
            return this;
        }
    }
}
