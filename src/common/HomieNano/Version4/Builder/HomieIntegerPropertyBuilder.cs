using HomieNano.Version4.Enums;
using HomieNano.Version4.Properties;

namespace HomieNano.Version4.Builder
{
    public class HomieIntegerPropertyBuilder : HomiePropertyBuilderBase
    {
        private readonly int _initialValue;

        internal HomieIntegerPropertyBuilder(HomieNodeBuilder nodeBuilder, string topicId, string name, int initialValue = 0)
            : base(nodeBuilder, topicId, name, DataType.Integer)
        {
            _initialValue = initialValue;
        }

        public override HomieNodeBuilder BuildProperty() => BuildProperty(out _);

        public HomieNodeBuilder BuildProperty(out IntegerProperty property)
        {
            property = new IntegerProperty(_topicId, _name, _format, _settable, _retained, _unit, _initialValue);
            _nodeBuilder.PushProperty(property);
            return _nodeBuilder;
        }

        public HomieIntegerPropertyBuilder WithFormat(string format)
        {
            _format = format;
            return this;
        }

        public HomieIntegerPropertyBuilder WithSettable(bool settable)
        {
            _settable = settable;
            return this;
        }

        public HomieIntegerPropertyBuilder WithRetained(bool retained)
        {
            _retained = retained;
            return this;
        }

        public HomieIntegerPropertyBuilder WithUnit(Unit unit)
        {
            _unit = unit;
            return this;
        }
    }
}
