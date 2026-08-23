using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.Properties;
using System;

namespace SmartHome.Homie.V4.Builder
{
    public class HomieFloatPropertyBuilder : HomiePropertyBuilderBase
    {
        private readonly double _initialValue;
        private int _decimals = FloatProperty.DefaultDecimals;

        internal HomieFloatPropertyBuilder(HomieNodeBuilder nodeBuilder, string topicId, string name, double initialValue = 0)
            : base(nodeBuilder, topicId, name, DataType.Float)
        {
            _initialValue = initialValue;
        }

        public override HomieNodeBuilder BuildProperty() => BuildProperty(out _);

        public HomieNodeBuilder BuildProperty(out FloatProperty property)
        {
            property = new FloatProperty(_topicId, _name, _format, _settable, _retained, _unit, _initialValue, _decimals);
            _nodeBuilder.PushProperty(property);
            return _nodeBuilder;
        }

        /// <summary>
        /// Decimal places this property publishes. Defaults to
        /// <see cref="FloatProperty.DefaultDecimals"/>.
        /// </summary>
        /// <remarks>
        /// Homie does not define a precision for floats, so the device chooses one.
        /// Raise it for a value that needs resolution, lower it for one that does not --
        /// a percentage rarely needs two places, and publishing them invites a controller
        /// to redraw on noise.
        /// </remarks>
        public HomieFloatPropertyBuilder WithDecimals(int decimals)
        {
            _decimals = decimals;
            return this;
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
