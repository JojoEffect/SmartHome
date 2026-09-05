using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.Properties;

namespace SmartHome.DeviceModel.Builder
{
    public class StringPropertyBuilder : PropertyBuilderBase
    {
        private readonly string _initialValue;

        internal StringPropertyBuilder(NodeBuilder nodeBuilder, string id, string name, string initialValue = "")
            : base(nodeBuilder, id, name, DataType.String)
        {
            _initialValue = initialValue;
        }

        public override NodeBuilder BuildProperty() => BuildProperty(out _);

        public NodeBuilder BuildProperty(out StringProperty property)
        {
            property = new StringProperty(_id, _name, _settable, _retained, _unit, _quantityKind, _initialValue);
            _nodeBuilder.PushProperty(property);
            return _nodeBuilder;
        }

        // No format: any payload is a valid string, so there is nothing to declare.

        public StringPropertyBuilder WithSettable(bool settable)
        {
            _settable = settable;
            return this;
        }

        public StringPropertyBuilder WithRetained(bool retained)
        {
            _retained = retained;
            return this;
        }

        /// <summary>
        /// The unit, e.g. <see cref="Units.DegreeCelsius"/>. Any string is allowed; see
        /// <see cref="Units"/> for the well-known ones.
        /// </summary>
        public StringPropertyBuilder WithUnit(string unit)
        {
            _unit = unit;
            return this;
        }

        /// <summary>
        /// What the value means, independently of its unit -- the difference between a
        /// humidity in <c>%</c> and a battery charge in <c>%</c>. Ignored by the Homie
        /// adapters; the source of Home Assistant's device class.
        /// </summary>
        public StringPropertyBuilder WithQuantityKind(QuantityKind quantityKind)
        {
            _quantityKind = quantityKind;
            return this;
        }
    }
}
