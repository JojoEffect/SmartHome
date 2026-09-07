using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.EventArgs;
using SmartHome.DeviceModel.Formats;
using System;
using System.Text;

namespace SmartHome.DeviceModel.Properties
{
    public class FloatProperty : PropertyBase
    {
        /// <summary>
        /// Decimal places used when no precision is given.
        /// </summary>
        /// <remarks>
        /// Two is a sensor-shaped default: it reads naturally for temperature, humidity
        /// and pressure, and round-trips through <see cref="double.TryParse"/> exactly.
        /// Set it per property with the builder's <c>WithDecimals</c> when a value needs
        /// more or fewer.
        /// </remarks>
        public const int DefaultDecimals = 2;

        /// <summary>
        /// The magnitude at which a fixed-decimal rendering stops fitting, so values
        /// must stay strictly below it.
        /// </summary>
        /// <remarks>
        /// nf-interpreter's <c>Format_F</c> builds a printf <c>%0.&lt;n&gt;f</c> into a
        /// fixed 128-byte buffer (<c>FORMAT_RESULT_BUFFER_SIZE</c>), and its bundled
        /// printf fork was patched to return the count it actually wrote rather than the
        /// count it would have needed. So a value too wide to render is not an error --
        /// it is silently cut off mid-digit, and the device publishes a number that is
        /// simply wrong. 1e100 leaves room for 101 integer digits, a sign, the point and
        /// the 15 decimals this type allows at most: 118 of the 127 usable bytes.
        ///
        /// Far outside anything a sensor produces. It exists for the settable property
        /// that declares no range, where the value comes from a controller.
        /// </remarks>
        public const double MaxPublishableMagnitude = 1e100;

        // Precomputed: this is on the publish path, and the format string never changes
        // after construction.
        private readonly string _numericFormat;

        public FloatProperty(
            string id,
            string name,
            NumericRange? range = null,
            bool settable = false,
            bool retained = true,
            string unit = Units.None,
            QuantityKind quantityKind = QuantityKind.None,
            double initialValue = 0.0,
            int decimals = DefaultDecimals)
            : base(id, name, DataType.Float, settable, retained, unit, quantityKind)
        {
            // 15 is the most a double carries; beyond it the extra places are noise, and
            // the point of this type is to stop publishing noise.
            if (decimals < 0 || decimals > 15)
            {
                // Named: a node can carry several float properties, and "was -1" on its
                // own does not say which one to go and look at.
                throw new ArgumentException($"Property '{id}': decimals must be between 0 and 15, was {decimals}.");
            }

            Range = range;
            Decimals = decimals;
            _numericFormat = $"F{decimals}";
            Value = EnsurePublishable(initialValue);

            // An initial value goes on the wire the same way a controller's does --
            // GetPayload() announces it into the retained store before anything has been
            // Set -- so it is held to the same declaration. Without this a property can
            // be built already advertising a number its own Set() refuses.
            if (Range != null && !Range.Contains(initialValue))
            {
                throw new ArgumentException($"Property '{id}': the initial value {initialValue} is outside the range '{Range}' it declares.");
            }
        }

        public double Value { get; private set; }

        /// <summary>The bounds this property declares, or null if it declares none.</summary>
        public NumericRange? Range { get; }

        /// <summary>Decimal places this property publishes.</summary>
        public int Decimals { get; }

        public override event PropertyUpdateHandler? OnUpdate;

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(FormatValue(Value));

        public void Update(double newValue)
        {
            Value = EnsurePublishable(newValue);
            OnUpdate?.Invoke(new PropertyUpdateEventArgs(this, Encoding.UTF8.GetBytes(FormatValue(newValue))));
        }

        /// <summary>Declares the value this property is heading for. See <see cref="PropertyBase.Target"/>.</summary>
        public void SetTarget(double value) => SetTargetPayload(FormatValue(EnsurePublishable(value)));

        /// <summary>
        /// Rejects values that have no float representation on the wire.
        /// </summary>
        /// <remarks>
        /// A float payload is the string literal representation of a number. NaN and the
        /// infinities are not: <c>double.ToString</c> returns "NaN", "Infinity" or
        /// "-Infinity" *before* it consults a format string, so the fixed-decimal
        /// rendering below cannot reach them. A controller cannot parse those, and
        /// neither can this device -- <c>double.TryParse</c> would refuse the property's
        /// own payload.
        ///
        /// Rejected at the boundary rather than published, because only the caller can
        /// decide what a non-finite value means. A device reading a sensor already has
        /// that answer: an invalid reading raises an alert rather than publishing
        /// something nobody can read.
        /// </remarks>
        private double EnsurePublishable(double value)
        {
            if (double.IsNaN(value) || double.IsPositiveInfinity(value) || double.IsNegativeInfinity(value))
            {
                throw new ArgumentException($"Property '{Id}' cannot publish '{value}': a float value must be a finite number.");
            }

            if (value >= MaxPublishableMagnitude || value <= -MaxPublishableMagnitude)
            {
                throw new ArgumentException($"Property '{Id}' cannot publish a magnitude of {MaxPublishableMagnitude} or more: its fixed-decimal rendering does not fit the runtime's format buffer.");
            }

            return value;
        }

        internal override string? Validate(string value)
        {
            if (!double.TryParse(value, out var parsed))
            {
                return "not a number";
            }

            // A float payload is the literal representation of a number, and NaN and the
            // infinities are not one -- see EnsurePublishable, which throws on them.
            // Caught here rather than there: SetInternal runs on the transport's dispatch
            // thread, so a controller must not be able to reach that throw with a payload.
            if (double.IsNaN(parsed) || double.IsPositiveInfinity(parsed) || double.IsNegativeInfinity(parsed))
            {
                return "not a finite number";
            }

            // Caught here for the same reason the line above is: a magnitude this large
            // has no fixed-decimal rendering that fits, so letting it through would
            // publish a silently truncated number and reach EnsurePublishable's throw on
            // the dispatch thread. A declared Range makes this unreachable; a property
            // without one is what needs it.
            if (parsed >= MaxPublishableMagnitude || parsed <= -MaxPublishableMagnitude)
            {
                return $"too large to render: the magnitude must be below {MaxPublishableMagnitude}";
            }

            if (Range != null && !Range.Contains(parsed))
            {
                return $"outside the range '{Range}' the format declares";
            }

            return null;
        }

        internal override void SetInternal(string value)
        {
            // Validate() has already proven this parses; the guard is what keeps that
            // structural rather than a comment.
            if (double.TryParse(value, out var parsed))
            {
                Update(parsed);
            }
        }

        /// <summary>
        /// Renders the value the way it goes on the wire.
        /// </summary>
        /// <remarks>
        /// Fixed-decimal, not <c>ToString()</c>. The conventions say only that a float
        /// payload is a number, so the device has to pick a rendering, and the default
        /// one is not a usable pick: nanoFramework's <c>double.ToString()</c> uses "G"
        /// and renders 21.5 as <c>21.499999999999999</c>. It is value-dependent, too --
        /// 0.1 comes out as "0.1" -- which is what let it survive so long.
        ///
        /// The alternatives were checked on the virtual device rather than assumed:
        /// round-trip format "R" throws <c>NotImplementedException</c> on this runtime,
        /// and "N" inserts a thousands separator ("1,234.57") that would corrupt the
        /// payload and defeat any parser. "F&lt;n&gt;" is correct across the range tested.
        /// </remarks>
        private string FormatValue(double value)
        {
            var formatted = value.ToString(_numericFormat);

            // The conventions require a dot. nanoFramework's Double exposes no
            // ToString(format, IFormatProvider) overload, so the separator cannot be
            // pinned at the call: the formatter uses NumberFormatInfo.CurrentInfo, which
            // is invariant today only because nothing references
            // nanoFramework.System.Globalization. Adding that package anywhere in the
            // solution would silently start publishing "21,50", which no controller can
            // read. "F" never emits a group separator, so a comma here can only be the
            // decimal one.
            //
            // Repaired on the way out, and deliberately NOT mirrored on the way in.
            // Formatting and parsing are asymmetric here: double.TryParse does not read
            // NumberFormatInfo at all -- it goes straight to Convert.NativeToDouble --
            // so it always expects a dot, whatever the culture. SetInternal is therefore
            // already correct, and "fixing" it for symmetry would break it.
            //
            // Character-wise because nanoFramework's String has no Replace at all.
            if (formatted.IndexOf(',') < 0)
            {
                return formatted;
            }

            var characters = formatted.ToCharArray();
            var repaired = new StringBuilder(characters.Length);
            for (int i = 0; i < characters.Length; i++)
            {
                repaired.Append(characters[i] == ',' ? '.' : characters[i]);
            }

            return repaired.ToString();
        }
    }
}
