using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.Properties;
using System;

namespace SmartHome.Homie.V4.Builder
{
    public class HomieFloatPropertyBuilder : HomiePropertyBuilderBase
    {
        private readonly double _initialValue;

        internal HomieFloatPropertyBuilder(HomieNodeBuilder nodeBuilder, string topicId, string name, double initialValue = 0)
            : base(nodeBuilder, topicId, name, DataType.Float)
        {
            _initialValue = initialValue;
        }

        public override HomieNodeBuilder BuildProperty() => BuildProperty(out _);

        public HomieNodeBuilder BuildProperty(out FloatProperty property)
        {
            property = new FloatProperty(_topicId, _name, _format, _settable, _retained, _unit, _initialValue);
            _nodeBuilder.PushProperty(property);
            return _nodeBuilder;
        }

        public HomieFloatPropertyBuilder WithFormat(string format)
        {
            _format = format;
            return this;
        }

        public HomieFloatPropertyBuilder WithSettable(bool settable)
        {
            _settable = settable;
            return this;
        }

        public HomieFloatPropertyBuilder WithRetained(bool retained)
        {
            _retained = retained;
            return this;
        }

        public HomieFloatPropertyBuilder WithUnit(Unit unit)
        {
            _unit = unit;
            return this;
        }
    }
}
