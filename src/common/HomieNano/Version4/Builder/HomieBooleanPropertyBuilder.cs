using HomieNano.Version4.Enums;
using HomieNano.Version4.Properties;

namespace HomieNano.Version4.Builder
{
    public class HomieBooleanPropertyBuilder : HomiePropertyBuilderBase
    {
        private readonly bool _initialValue;

        internal HomieBooleanPropertyBuilder(HomieNodeBuilder nodeBuilder, string topicId, string name, bool initialValue = false)
            : base(nodeBuilder, topicId, name, DataType.Boolean)
        {
            _initialValue = initialValue;
        }

        public override HomieNodeBuilder BuildProperty() => BuildProperty(out _);

        public HomieNodeBuilder BuildProperty(out BooleanProperty property)
        {
            property = new BooleanProperty(_topicId, _name, _format, _settable, _retained, _unit, _initialValue);
            _nodeBuilder.PushProperty(property);
            return _nodeBuilder;
        }

        public HomieBooleanPropertyBuilder WithFormat(string format)
        {
            _format = format;
            return this;
        }

        public HomieBooleanPropertyBuilder WithSettable(bool settable)
        {
            _settable = settable;
            return this;
        }

        public HomieBooleanPropertyBuilder WithRetained(bool retained)
        {
            _retained = retained;
            return this;
        }

        public HomieBooleanPropertyBuilder WithUnit(Unit unit)
        {
            _unit = unit;
            return this;
        }
    }
}
