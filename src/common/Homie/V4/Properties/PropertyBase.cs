using SmartHome.Homie.V4.Attributes;
using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.EventArgs;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using System;
using System.Text;

namespace SmartHome.Homie.V4.Properties
{
    public delegate void PropertyUpdateHandler(PropertyUpdateEventArgs args);

    public abstract class PropertyBase : NamedHomieEntityBase
    {
        private readonly DataTypeAttribute _dataTypeAttribute;
        private readonly FormatAttribute _formatAttribute;
        private readonly SettableAttribute _settableAttribute;
        private readonly RetainedAttribute _retainedAttribute;
        private readonly UnitAttribute _unitAttribute;
        private readonly ILogger _logger;

        protected PropertyBase(string topicId,
            string name,
            DataType dataType, 
            string format = "", 
            bool settable = false, 
            bool retained = true, 
            Unit unit = Unit.None) 
            : base(topicId, name)
        {
            _dataTypeAttribute = new(this, dataType);
            _formatAttribute = new(this, format);
            _settableAttribute = new(this, settable);
            _retainedAttribute = new(this, retained);
            _unitAttribute = new(this, unit);
            _logger = this.GetCurrentClassLogger();
        }


        /// <summary>
        /// Event raised when the property value is updated internally.
        /// </summary>
        public abstract event PropertyUpdateHandler? OnUpdate;

        public abstract override byte[] GetPayload();

        public DataTypeAttribute DataTypeAttribute => _dataTypeAttribute;
        
        public FormatAttribute FormatAttribute => _formatAttribute;
        
        public SettableAttribute SettableAttribute => _settableAttribute;
        
        public RetainedAttribute RetainedAttribute => _retainedAttribute;
        
        public UnitAttribute UnitAttribute => _unitAttribute;

        /// <summary>
        /// Applies a controller's payload, if the property's own declared
        /// <c>$datatype</c> and <c>$format</c> permit it.
        /// </summary>
        /// <returns>
        /// <c>true</c> when the payload was applied, <c>false</c> when it was rejected.
        /// </returns>
        /// <remarks>
        /// A rejected payload is logged and dropped: the value does not move, so nothing
        /// is published and nothing lands in the broker's retained store. Homie has no
        /// "command refused" channel, so a property that did not change is the only
        /// feedback a controller gets -- and it is the same signal the convention gives
        /// for any other refusal.
        /// </remarks>
        public bool Set(byte[] value)
        {
            if (!SettableAttribute.Value)
            {
                throw new InvalidOperationException($"Property '{TopicId}' is not settable.");
            }

            var str = Encoding.UTF8.GetString(value, 0, value.Length);

            // Before SetInternal, deliberately. Validating afterwards would mean the
            // value had already moved and OnUpdate had already published it.
            var rejection = Validate(str);
            if (rejection != null)
            {
                _logger.LogWarning($"Property '{TopicId}' rejected the payload '{str}': {rejection}.");
                return false;
            }

            _logger.LogDebug($"Setting property '{TopicId}' to value '{str}'.");
            SetInternal(str);
            return true;
        }

        /// <summary>
        /// Why this payload is not a value the property may hold, or <c>null</c> if it is.
        /// </summary>
        /// <remarks>
        /// Every datatype answers this the same way -- reject -- which is the point: the
        /// five implementations used to give three different answers to the same
        /// question. <see cref="EnumProperty"/> accepted anything, the numeric types
        /// ignored a declared range, <see cref="FloatProperty"/> and
        /// <see cref="ColorProperty"/> dropped an unparseable payload silently, and
        /// <see cref="BooleanProperty"/> turned anything unrecognised into <c>false</c> --
        /// which at the broker is indistinguishable from a controller having deliberately
        /// asked for <c>false</c>.
        ///
        /// The returned text is the log line's reason, so it reads as a continuation of
        /// "rejected the payload 'x': ".
        /// </remarks>
        internal abstract string? Validate(string value);

        internal abstract void SetInternal(string value);

        /// <summary>
        /// Checks a numeric value against the <c>min:max</c> range <c>$format</c> may
        /// declare. Returns <c>null</c> when the value is in range, or when no range was
        /// declared.
        /// </summary>
        protected string? ValidateRange(double value)
        {
            if (!TryGetDeclaredRange(out var minimum, out var maximum))
            {
                return null;
            }

            if (value < minimum || value > maximum)
            {
                return $"outside the range '{FormatAttribute.Value}' declared by $format";
            }

            return null;
        }

        /// <summary>
        /// Reads the <c>min:max</c> range a numeric <c>$format</c> may declare.
        /// </summary>
        /// <remarks>
        /// Both ends are required, which is all Homie v4 defines ("from:to", e.g.
        /// "10:15"). A <c>$format</c> that is empty or does not parse as a range declares
        /// no range, so no range is enforced -- a device author's malformed format is not
        /// a reason to start refusing a controller's otherwise valid payloads.
        ///
        /// Both ends are read as doubles, integer properties included. An int is exact as
        /// a double well beyond the range the type covers, so one helper serves both
        /// numeric types rather than two that could drift apart.
        /// </remarks>
        private bool TryGetDeclaredRange(out double minimum, out double maximum)
        {
            minimum = 0;
            maximum = 0;

            var format = FormatAttribute.Value;
            if (format == null || format.Length == 0)
            {
                return false;
            }

            var bounds = format.Split(':');

            return bounds.Length == 2
                && double.TryParse(bounds[0].Trim(), out minimum)
                && double.TryParse(bounds[1].Trim(), out maximum)
                && minimum <= maximum;
        }
    }
}
