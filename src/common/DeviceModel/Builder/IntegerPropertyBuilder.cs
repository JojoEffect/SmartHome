using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.Formats;
using SmartHome.DeviceModel.Properties;

namespace SmartHome.DeviceModel.Builder
{
    public class IntegerPropertyBuilder : PropertyBuilderBase
    {
        private readonly int _initialValue;
        private NumericRange? _range;

        internal IntegerPropertyBuilder(NodeBuilder nodeBuilder, string id, string name, int initialValue = 0)
            : base(nodeBuilder, id, name, DataType.Integer)
        {
            _initialValue = initialValue;
        }

        public override NodeBuilder BuildProperty() => BuildProperty(out _);

        public NodeBuilder BuildProperty(out IntegerProperty property)
        {
            property = new IntegerProperty(_id, _name, _range, _settable, _retained, _unit, _quantityKind, _initialValue);
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
        /// would punish the controller instead.
        ///
        /// It is not silent, though: it is logged as a warning naming the property and the
        /// text. Here, because nothing later can -- the property keeps the parsed range
        /// and not the string it came from, so from then on a declaration that failed to
        /// parse is indistinguishable from one that was never written, and a consumer
        /// republishing the bounds republishes none. Empty text means "no range" and is
        /// not reported.
        ///
        /// Use <see cref="WithRange"/> to state the bounds directly. It is clearer, a bad
        /// range there is an exception from <see cref="NumericRange"/>'s factories rather
        /// than a warning -- which reaches nobody on a device that configured no logger --
        /// and it is the only way to express an open end or a step.
        /// </remarks>
        public IntegerPropertyBuilder WithFormat(string format)
        {
            if (!NumericRange.TryParse(format, out _range))
            {
                ReportUnparsedFormat(format, "a range");
            }

            return this;
        }

        /// <summary>The bounds this property declares. See <see cref="NumericRange"/>.</summary>
        public IntegerPropertyBuilder WithRange(NumericRange? range)
        {
            _range = range;
            return this;
        }

        public IntegerPropertyBuilder WithSettable(bool settable)
        {
            _settable = settable;
            return this;
        }

        public IntegerPropertyBuilder WithRetained(bool retained)
        {
            _retained = retained;
            return this;
        }

        /// <summary>
        /// The unit, e.g. <see cref="Units.DegreeCelsius"/>. Any string is allowed; see
        /// <see cref="Units"/> for the well-known ones.
        /// </summary>
        public IntegerPropertyBuilder WithUnit(string unit)
        {
            _unit = unit;
            return this;
        }

        /// <summary>
        /// What the value means, independently of its unit -- the difference between a
        /// humidity in <c>%</c> and a battery charge in <c>%</c>. An adapter whose
        /// convention has a semantic category of its own maps this onto it; one that
        /// has none ignores it.
        /// </summary>
        public IntegerPropertyBuilder WithQuantityKind(QuantityKind quantityKind)
        {
            _quantityKind = quantityKind;
            return this;
        }
    }
}
