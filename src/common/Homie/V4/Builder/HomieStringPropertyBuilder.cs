using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.Properties;

namespace SmartHome.Homie.V4.Builder
{
    public class HomieStringPropertyBuilder : HomiePropertyBuilderBase
    {
        private readonly string _initialValue;

        internal HomieStringPropertyBuilder(HomieNodeBuilder nodeBuilder, string topicId, string name, string initialValue = "")
            : base(nodeBuilder, topicId, name, DataType.String)
        {
            _initialValue = initialValue;
        }

        public override HomieNodeBuilder BuildProperty() => BuildProperty(out _);

        public HomieNodeBuilder BuildProperty(out StringProperty property)
        {
            property = new StringProperty(_topicId, _name, _format, _settable, _retained, _unit, _initialValue);
            _nodeBuilder.PushProperty(property);
            return _nodeBuilder;
        }

        public HomieStringPropertyBuilder WithFormat(string format)
        {
            _format = format;
            return this;
        }

        public HomieStringPropertyBuilder WithSettable(bool settable)
        {
            _settable = settable;
            return this;
        }

        public HomieStringPropertyBuilder WithRetained(bool retained)
        {
            _retained = retained;
            return this;
        }

        public HomieStringPropertyBuilder WithUnit(Unit unit)
        {
            _unit = unit;
            return this;
        }
    }
}
