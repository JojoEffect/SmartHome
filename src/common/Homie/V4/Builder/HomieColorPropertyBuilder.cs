using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.Properties;

namespace SmartHome.Homie.V4.Builder
{
    public class HomieColorPropertyBuilder : HomiePropertyBuilderBase
    {
        private readonly Properties.HomieColor _initialValue;

        internal HomieColorPropertyBuilder(HomieNodeBuilder nodeBuilder, string topicId, string name, Properties.HomieColor initialValue = default)
            : base(nodeBuilder, topicId, name, DataType.Color)
        {
            _initialValue = initialValue;
        }

        public override HomieNodeBuilder BuildProperty() => BuildProperty(out _);

        public HomieNodeBuilder BuildProperty(out ColorProperty property)
        {
            property = new ColorProperty(_topicId, _name, _format, _settable, _retained, _unit, _initialValue);
            _nodeBuilder.PushProperty(property);
            return _nodeBuilder;
        }

        public HomieColorPropertyBuilder WithFormat(string format)
        {
            _format = format;
            return this;
        }

        public HomieColorPropertyBuilder WithSettable(bool settable)
        {
            _settable = settable;
            return this;
        }

        public HomieColorPropertyBuilder WithRetained(bool retained)
        {
            _retained = retained;
            return this;
        }

        public HomieColorPropertyBuilder WithUnit(Unit unit)
        {
            _unit = unit;
            return this;
        }
    }
}
