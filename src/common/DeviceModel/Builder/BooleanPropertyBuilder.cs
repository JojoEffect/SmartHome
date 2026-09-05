using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.Formats;
using SmartHome.DeviceModel.Properties;

namespace SmartHome.DeviceModel.Builder
{
    public class BooleanPropertyBuilder : PropertyBuilderBase
    {
        private readonly bool _initialValue;
        private BooleanLabels? _labels;

        internal BooleanPropertyBuilder(NodeBuilder nodeBuilder, string id, string name, bool initialValue = false)
            : base(nodeBuilder, id, name, DataType.Boolean)
        {
            _initialValue = initialValue;
        }

        public override NodeBuilder BuildProperty() => BuildProperty(out _);

        public NodeBuilder BuildProperty(out BooleanProperty property)
        {
            property = new BooleanProperty(_id, _name, _labels, _settable, _retained, _unit, _quantityKind, _initialValue);
            _nodeBuilder.PushProperty(property);
            return _nodeBuilder;
        }

        /// <summary>
        /// Names for the two values, as the <c>&lt;false&gt;,&lt;true&gt;</c> text a
        /// device author writes, e.g. <c>"off,on"</c>. Text that is not exactly two
        /// non-empty entries declares no labels.
        /// </summary>
        public BooleanPropertyBuilder WithFormat(string format)
        {
            BooleanLabels.TryParse(format, out _labels);
            return this;
        }

        /// <summary>
        /// Names for the two values. Descriptive only -- the payloads stay <c>true</c>
        /// and <c>false</c> whatever these say. See <see cref="BooleanLabels"/>.
        /// </summary>
        public BooleanPropertyBuilder WithLabels(string falseLabel, string trueLabel)
        {
            _labels = new BooleanLabels(falseLabel, trueLabel);
            return this;
        }

        public BooleanPropertyBuilder WithSettable(bool settable)
        {
            _settable = settable;
            return this;
        }

        public BooleanPropertyBuilder WithRetained(bool retained)
        {
            _retained = retained;
            return this;
        }

        /// <summary>
        /// The unit, e.g. <see cref="Units.DegreeCelsius"/>. Any string is allowed; see
        /// <see cref="Units"/> for the well-known ones.
        /// </summary>
        public BooleanPropertyBuilder WithUnit(string unit)
        {
            _unit = unit;
            return this;
        }

        /// <summary>
        /// What the value means, independently of its unit -- the difference between a
        /// humidity in <c>%</c> and a battery charge in <c>%</c>. Ignored by the Homie
        /// adapters; the source of Home Assistant's device class.
        /// </summary>
        public BooleanPropertyBuilder WithQuantityKind(QuantityKind quantityKind)
        {
            _quantityKind = quantityKind;
            return this;
        }
    }
}
