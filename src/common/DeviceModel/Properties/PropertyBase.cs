using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.EventArgs;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using System;
using System.Text;

namespace SmartHome.DeviceModel.Properties
{
    public delegate void PropertyUpdateHandler(PropertyUpdateEventArgs args);

    /// <summary>
    /// One value a device exposes: its datatype, what it may hold, and its current
    /// contents together with the canonical way that goes on the wire.
    /// </summary>
    /// <remarks>
    /// There is no <c>string Format</c> here, and its absence is deliberate. Each
    /// datatype's restrictions are modelled as the thing they are -- a
    /// <c>NumericRange</c>, an <c>EnumOptions</c>, a <c>BooleanLabels</c>, a
    /// <c>ColorFormats</c> -- and live on the property type that can actually use them.
    /// A single raw format string has to be re-read by everything that cares, and those
    /// readers drift: an earlier attempt at a second adapter shipped a range reader that
    /// disagreed with this class's own in three separate ways, so a payload the property
    /// refused was advertised to controllers as acceptable. Parsing now happens once,
    /// where the format type is defined.
    /// </remarks>
    public abstract class PropertyBase : NamedEntityBase
    {
        private readonly ILogger _logger;

        protected PropertyBase(
            string id,
            string name,
            DataType dataType,
            bool settable = false,
            bool retained = true,
            string unit = Units.None,
            QuantityKind quantityKind = QuantityKind.None)
            : base(id, name)
        {
            DataType = dataType;
            Settable = settable;
            Retained = retained;
            Unit = unit ?? Units.None;
            QuantityKind = quantityKind;
            _logger = this.GetCurrentClassLogger();
        }

        /// <summary>What kind of value this property holds.</summary>
        public DataType DataType { get; }

        /// <summary>Whether a controller may write to this property.</summary>
        public bool Settable { get; }

        /// <summary>
        /// Whether the last value should outlive its publication -- true for a state a
        /// controller wants on connect, false for a momentary event such as a button
        /// press.
        /// </summary>
        public bool Retained { get; }

        /// <summary>
        /// The unit, as a string. See <see cref="Units"/> for the well-known ones; any
        /// string is allowed.
        /// </summary>
        public string Unit { get; }

        /// <summary>
        /// What the value means, independently of its unit. Ignored by the Homie
        /// adapters, and the source of Home Assistant's device class.
        /// </summary>
        public QuantityKind QuantityKind { get; }

        /// <summary>
        /// Raised when the value moves, whoever moved it -- the device measuring
        /// something, or a controller writing to it. An adapter publishes from this.
        /// </summary>
        /// <remarks>
        /// This cannot tell those two apart, and an app acting on a *command* must not
        /// subscribe here for that reason. The protocol seam raises a separate command
        /// event, which fires only for a controller's write.
        /// </remarks>
        public abstract event PropertyUpdateHandler? OnUpdate;

        /// <summary>
        /// Raised when the intended value -- see <see cref="Target"/> -- is declared or
        /// cleared. Carries an empty payload when cleared.
        /// </summary>
        public event PropertyUpdateHandler? OnTargetUpdate;

        /// <summary>
        /// The value this property is heading for, encoded the same way the value itself
        /// is, or null when there is no transition in flight.
        /// </summary>
        /// <remarks>
        /// Homie v5's <c>$target</c>: it closes the loop for a controller that has just
        /// written a value, and it is the only way a device can say "I heard you, this
        /// will take a while" for something that is not instantaneous -- a light dimming
        /// over ten seconds, a motorised valve. Homie v4 and Home Assistant have no
        /// equivalent, so their adapters ignore it; nothing else in the model depends on
        /// it, so leaving it unset costs nothing.
        /// </remarks>
        public string? Target { get; private set; }

        /// <summary>The current value, UTF-8 encoded exactly as it should go on the wire.</summary>
        public abstract byte[] GetPayload();

        /// <summary>
        /// Drops any declared target, e.g. because the transition finished or was
        /// abandoned.
        /// </summary>
        /// <remarks>
        /// Raises <see cref="OnTargetUpdate"/> with an empty payload. That is not an
        /// arbitrary choice of sentinel: a zero-length payload is how a retained topic is
        /// deleted, which is exactly what "there is no target any more" has to mean to a
        /// consumer that saw the previous one.
        /// </remarks>
        public void ClearTarget()
        {
            if (Target == null)
            {
                return;
            }

            Target = null;
            OnTargetUpdate?.Invoke(new PropertyUpdateEventArgs(this, new byte[0]));
        }

        /// <summary>
        /// Applies a controller's payload, if this property's datatype and declared
        /// format permit it.
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
            if (!Settable)
            {
                throw new InvalidOperationException($"Property '{Id}' is not settable.");
            }

            var str = Encoding.UTF8.GetString(value, 0, value.Length);

            // Before SetInternal, deliberately. Validating afterwards would mean the
            // value had already moved and OnUpdate had already published it.
            var rejection = Validate(str);
            if (rejection != null)
            {
                _logger.LogWarning($"Property '{Id}' rejected the payload '{str}': {rejection}.");
                return false;
            }

            _logger.LogDebug($"Setting property '{Id}' to value '{str}'.");
            SetInternal(str);
            return true;
        }

        /// <summary>
        /// Why this payload is not a value the property may hold, or <c>null</c> if it is.
        /// </summary>
        /// <remarks>
        /// Every datatype answers this the same way -- reject -- which is the point: the
        /// implementations used to give three different answers to the same question.
        /// <see cref="EnumProperty"/> accepted anything, the numeric types ignored a
        /// declared range, <see cref="FloatProperty"/> and <see cref="ColorProperty"/>
        /// dropped an unparseable payload silently, and <see cref="BooleanProperty"/>
        /// turned anything unrecognised into <c>false</c> -- which at the broker is
        /// indistinguishable from a controller having deliberately asked for <c>false</c>.
        ///
        /// The returned text is the log line's reason, so it reads as a continuation of
        /// "rejected the payload 'x': ".
        /// </remarks>
        internal abstract string? Validate(string value);

        internal abstract void SetInternal(string value);

        /// <summary>
        /// Records an intended value and announces it. Called by the typed
        /// <c>SetTarget</c> of each property, which encodes the value exactly as
        /// <see cref="GetPayload"/> would.
        /// </summary>
        protected void SetTargetPayload(string payload)
        {
            Target = payload;
            OnTargetUpdate?.Invoke(new PropertyUpdateEventArgs(this, Encoding.UTF8.GetBytes(payload)));
        }
    }
}
