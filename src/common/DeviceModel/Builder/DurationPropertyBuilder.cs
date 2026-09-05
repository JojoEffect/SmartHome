using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.Properties;

namespace SmartHome.DeviceModel.Builder
{
    /// <summary>
    /// Builds a Homie v5 duration property. A Homie v4 adapter has no such datatype and
    /// is expected to refuse the device rather than invent a spelling for it.
    /// </summary>
    public class DurationPropertyBuilder : PropertyBuilderBase
    {
        internal DurationPropertyBuilder(NodeBuilder nodeBuilder, string id, string name)
            : base(nodeBuilder, id, name, DataType.Duration)
        {
        }

        public override NodeBuilder BuildProperty() => BuildProperty(out _);

        public NodeBuilder BuildProperty(out DurationProperty property)
        {
            property = new DurationProperty(_id, _name, _settable, _retained, _unit, _quantityKind);
            _nodeBuilder.PushProperty(property);
            return _nodeBuilder;
        }

        // No format: an ISO 8601 duration has nothing to restrict.

        public DurationPropertyBuilder WithSettable(bool settable)
        {
            _settable = settable;
            return this;
        }

        public DurationPropertyBuilder WithRetained(bool retained)
        {
            _retained = retained;
            return this;
        }

        /// <summary>
        /// The unit, e.g. <see cref="Units.DegreeCelsius"/>. Any string is allowed; see
        /// <see cref="Units"/> for the well-known ones.
        /// </summary>
        public DurationPropertyBuilder WithUnit(string unit)
        {
            _unit = unit;
            return this;
        }

        /// <summary>
        /// What the value means, independently of its unit -- the difference between a
        /// humidity in <c>%</c> and a battery charge in <c>%</c>. Ignored by the Homie
        /// adapters; the source of Home Assistant's device class.
        /// </summary>
        public DurationPropertyBuilder WithQuantityKind(QuantityKind quantityKind)
        {
            _quantityKind = quantityKind;
            return this;
        }
    }
}
