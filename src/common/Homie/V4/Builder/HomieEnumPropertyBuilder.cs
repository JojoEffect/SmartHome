using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.Properties;

namespace SmartHome.Homie.V4.Builder
{
    public class HomieEnumPropertyBuilder : HomiePropertyBuilderBase
    {
        private readonly string _initialValue;

        internal HomieEnumPropertyBuilder(HomieNodeBuilder nodeBuilder, string topicId, string name, string initialValue = "")
            : base(nodeBuilder, topicId, name, DataType.Enum)
        {
            _initialValue = initialValue;
        }

        public override HomieNodeBuilder BuildProperty() => BuildProperty(out _);

        public HomieNodeBuilder BuildProperty(out EnumProperty property)
        {
            property = new EnumProperty(_topicId, _name, _format, _settable, _retained, _unit, _initialValue);
            _nodeBuilder.PushProperty(property);
            return _nodeBuilder;
        }

        public HomieEnumPropertyBuilder WithFormat(string format)
        {
            _format = format;
            return this;
        }

        public HomieEnumPropertyBuilder WithSettable(bool settable)
        {
            _settable = settable;
            return this;
        }

        public HomieEnumPropertyBuilder WithRetained(bool retained)
        {
            _retained = retained;
            return this;
        }

        public HomieEnumPropertyBuilder WithUnit(Unit unit)
        {
            _unit = unit;
            return this;
        }
    }
}
