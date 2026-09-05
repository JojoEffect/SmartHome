using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.Formats;
using SmartHome.DeviceModel.Properties;

namespace SmartHome.DeviceModel.Builder
{
    public class EnumPropertyBuilder : PropertyBuilderBase
    {
        private readonly string _initialValue;
        private EnumOptions? _options;

        internal EnumPropertyBuilder(NodeBuilder nodeBuilder, string id, string name, string initialValue = "")
            : base(nodeBuilder, id, name, DataType.Enum)
        {
            _initialValue = initialValue;
        }

        public override NodeBuilder BuildProperty() => BuildProperty(out _);

        public NodeBuilder BuildProperty(out EnumProperty property)
        {
            property = new EnumProperty(_id, _name, _options, _settable, _retained, _unit, _quantityKind, _initialValue);
            _nodeBuilder.PushProperty(property);
            return _nodeBuilder;
        }

        /// <summary>
        /// The permitted values, as the comma-separated text a device author writes.
        /// Entries are trimmed; text with nothing usable in it declares no options, and
        /// a property that declares none accepts any payload.
        /// </summary>
        public EnumPropertyBuilder WithFormat(string format)
        {
            EnumOptions.TryParse(format, out _options);
            return this;
        }

        /// <summary>
        /// The permitted values, stated directly.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="WithFormat"/>, this throws when nothing usable is left: an
        /// array handed in with no entries in it is unambiguously a programming error,
        /// where a format string that failed to parse is a device author's typo in text.
        /// </remarks>
        public EnumPropertyBuilder WithOptions(string[] options)
        {
            _options = new EnumOptions(options);
            return this;
        }

        public EnumPropertyBuilder WithSettable(bool settable)
        {
            _settable = settable;
            return this;
        }

        public EnumPropertyBuilder WithRetained(bool retained)
        {
            _retained = retained;
            return this;
        }

        /// <summary>
        /// The unit, e.g. <see cref="Units.DegreeCelsius"/>. Any string is allowed; see
        /// <see cref="Units"/> for the well-known ones.
        /// </summary>
        public EnumPropertyBuilder WithUnit(string unit)
        {
            _unit = unit;
            return this;
        }

        /// <summary>
        /// What the value means, independently of its unit -- the difference between a
        /// humidity in <c>%</c> and a battery charge in <c>%</c>. Ignored by the Homie
        /// adapters; the source of Home Assistant's device class.
        /// </summary>
        public EnumPropertyBuilder WithQuantityKind(QuantityKind quantityKind)
        {
            _quantityKind = quantityKind;
            return this;
        }
    }
}
