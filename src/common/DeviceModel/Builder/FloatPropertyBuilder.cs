using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.Formats;
using SmartHome.DeviceModel.Properties;

namespace SmartHome.DeviceModel.Builder
{
    public class FloatPropertyBuilder : PropertyBuilderBase
    {
        private readonly double _initialValue;
        private NumericRange? _range;
        private int _decimals = FloatProperty.DefaultDecimals;

        internal FloatPropertyBuilder(NodeBuilder nodeBuilder, string id, string name, double initialValue = 0)
            : base(nodeBuilder, id, name, DataType.Float)
        {
            _initialValue = initialValue;
        }

        public override NodeBuilder BuildProperty() => BuildProperty(out _);

        public NodeBuilder BuildProperty(out FloatProperty property)
        {
            property = new FloatProperty(_id, _name, _range, _settable, _retained, _unit, _quantityKind, _initialValue, _decimals);
            _nodeBuilder.PushProperty(property);
            return _nodeBuilder;
        }

        /// <summary>
        /// The bounds this property declares, as the <c>[min]:[max][:step]</c> text a
        /// device author writes.
        /// </summary>
        /// <remarks>
        /// Text that does not parse -- a missing colon, a non-numeric bound, a minimum
        /// above its maximum -- declares no range at all, rather than stopping the device
        /// from being built. That is long-standing and deliberate: a malformed
        /// declaration is a device author's bug, and refusing every payload because of it
        /// would punish the controller instead. The malformed text still surfaces, in the
        /// adapter's own check of what it can publish.
        ///
        /// Use <see cref="WithRange"/> to state the bounds directly, which is both
        /// clearer and the only way to express an open end or a step.
        /// </remarks>
        public FloatPropertyBuilder WithFormat(string format)
        {
            NumericRange.TryParse(format, out _range);
            return this;
        }

        /// <summary>The bounds this property declares. See <see cref="NumericRange"/>.</summary>
        public FloatPropertyBuilder WithRange(NumericRange? range)
        {
            _range = range;
            return this;
        }

        /// <summary>
        /// Decimal places this property publishes. Defaults to
        /// <see cref="FloatProperty.DefaultDecimals"/>.
        /// </summary>
        /// <remarks>
        /// No convention defines a precision for floats, so the device chooses one.
        /// Raise it for a value that needs resolution, lower it for one that does not --
        /// a percentage rarely needs two places, and publishing them invites a controller
        /// to redraw on noise.
        /// </remarks>
        public FloatPropertyBuilder WithDecimals(int decimals)
        {
            _decimals = decimals;
            return this;
        }

        public FloatPropertyBuilder WithSettable(bool settable)
        {
            _settable = settable;
            return this;
        }

        public FloatPropertyBuilder WithRetained(bool retained)
        {
            _retained = retained;
            return this;
        }

        /// <summary>
        /// The unit, e.g. <see cref="Units.DegreeCelsius"/>. Any string is allowed; see
        /// <see cref="Units"/> for the well-known ones.
        /// </summary>
        public FloatPropertyBuilder WithUnit(string unit)
        {
            _unit = unit;
            return this;
        }

        /// <summary>
        /// What the value means, independently of its unit -- the difference between a
        /// humidity in <c>%</c> and a battery charge in <c>%</c>. Ignored by the Homie
        /// adapters; the source of Home Assistant's device class.
        /// </summary>
        public FloatPropertyBuilder WithQuantityKind(QuantityKind quantityKind)
        {
            _quantityKind = quantityKind;
            return this;
        }
    }
}
