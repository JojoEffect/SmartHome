using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.Formats;
using SmartHome.DeviceModel.Properties;

namespace SmartHome.DeviceModel.Builder
{
    public class ColorPropertyBuilder : PropertyBuilderBase
    {
        private readonly ColorValue _initialValue;
        private ColorFormats? _formats;

        internal ColorPropertyBuilder(NodeBuilder nodeBuilder, string id, string name, ColorValue initialValue = default)
            : base(nodeBuilder, id, name, DataType.Color)
        {
            _initialValue = initialValue;
        }

        public override NodeBuilder BuildProperty() => BuildProperty(out _);

        public NodeBuilder BuildProperty(out ColorProperty property)
        {
            property = new ColorProperty(_id, _name, _formats, _settable, _retained, _unit, _quantityKind, _initialValue);
            _nodeBuilder.PushProperty(property);
            return _nodeBuilder;
        }

        /// <summary>
        /// The colour encodings this property supports, most preferred first, as the
        /// comma-separated text a device author writes -- <c>"rgb"</c>, <c>"rgb,hsv"</c>.
        /// Text naming an encoding that is not <c>rgb</c>, <c>hsv</c> or <c>xyz</c>
        /// declares none.
        /// </summary>
        public ColorPropertyBuilder WithFormat(string format)
        {
            ColorFormats.TryParse(format, out _formats);
            return this;
        }

        /// <summary>
        /// The colour encodings this property supports, most preferred first, stated
        /// directly. Throws on an unknown encoding, for the reason
        /// <c>EnumPropertyBuilder.WithOptions</c> gives.
        /// </summary>
        public ColorPropertyBuilder WithFormats(string[] formats)
        {
            _formats = new ColorFormats(formats);
            return this;
        }

        public ColorPropertyBuilder WithSettable(bool settable)
        {
            _settable = settable;
            return this;
        }

        public ColorPropertyBuilder WithRetained(bool retained)
        {
            _retained = retained;
            return this;
        }

        /// <summary>
        /// The unit, e.g. <see cref="Units.DegreeCelsius"/>. Any string is allowed; see
        /// <see cref="Units"/> for the well-known ones.
        /// </summary>
        public ColorPropertyBuilder WithUnit(string unit)
        {
            _unit = unit;
            return this;
        }

        /// <summary>
        /// What the value means, independently of its unit -- the difference between a
        /// humidity in <c>%</c> and a battery charge in <c>%</c>. Ignored by the Homie
        /// adapters; the source of Home Assistant's device class.
        /// </summary>
        public ColorPropertyBuilder WithQuantityKind(QuantityKind quantityKind)
        {
            _quantityKind = quantityKind;
            return this;
        }
    }
}
