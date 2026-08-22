using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.EventArgs;
using System;
using System.Text;

namespace SmartHome.Homie.V4.Properties
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

        // Precomputed: this is on the publish path, and the format string never changes
        // after construction.
        private readonly string _numericFormat;

        public FloatProperty(
            string topicId,
            string name,
            string format = "",
            bool settable = false,
            bool retained = true,
            Unit unit = Unit.None,
            double initialValue = 0.0,
            int decimals = DefaultDecimals)
            : base(topicId, name, DataType.Float, format, settable, retained, unit)
        {
            // 15 is the most a double carries; beyond it the extra places are noise, and
            // the point of this type is to stop publishing noise.
            if (decimals < 0 || decimals > 15)
            {
                // Named: a node can carry several float properties, and "was -1" on its
                // own does not say which one to go and look at.
                throw new ArgumentException($"Property '{topicId}': decimals must be between 0 and 15, was {decimals}.");
            }

            Decimals = decimals;
            _numericFormat = $"F{decimals}";
            Value = EnsurePublishable(initialValue);
        }

        public double Value { get; private set; }

        /// <summary>Decimal places this property publishes.</summary>
        public int Decimals { get; }

        public override event PropertyUpdateHandler? OnUpdate;

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(FormatValue(Value));

        public void Update(double newValue)
        {
            Value = EnsurePublishable(newValue);
            PropertyUpdateEventArgs args = new(this, Encoding.UTF8.GetBytes(FormatValue(newValue)));
            OnUpdate?.Invoke(args);
        }

        /// <summary>
        /// Rejects values that have no Homie float representation.
        /// </summary>
        /// <remarks>
        /// Homie defines a float payload as the string literal representation of a
        /// number. NaN and the infinities are not: <c>double.ToString</c> returns "NaN",
        /// "Infinity" or "-Infinity" *before* it consults a format string, so the
        /// fixed-decimal rendering below cannot reach them. A controller cannot parse
        /// those, and neither can this device -- <c>double.TryParse</c> would refuse the
        /// property's own payload.
        ///
        /// Rejected at the boundary rather than published, because only the caller can
        /// decide what a non-finite value means. RoomSensor already has that answer: an
        /// invalid reading moves the device to 'alert' rather than publishing something
        /// nobody can read.
        /// </remarks>
        private double EnsurePublishable(double value)
        {
            if (double.IsNaN(value) || double.IsPositiveInfinity(value) || double.IsNegativeInfinity(value))
            {
                throw new ArgumentException($"Property '{TopicId}' cannot publish '{value}': a Homie float must be a finite number.");
            }

            return value;
        }

        internal override void SetInternal(string value)
        {
            if (double.TryParse(value, out var parsed))
            {
                Update(parsed);
            }
        }

        /// <summary>
        /// Renders the value the way it goes on the wire.
        /// </summary>
        /// <remarks>
        /// Fixed-decimal, not <c>ToString()</c>. Homie says only that a float payload is
        /// a number, so the device has to pick a rendering, and the default one is not a
        /// usable pick: nanoFramework's <c>double.ToString()</c> uses "G" and renders
        /// 21.5 as <c>21.499999999999999</c>. It is value-dependent, too -- 0.1 comes out
        /// as "0.1" -- which is what let it survive so long.
        ///
        /// The alternatives were checked on the virtual device rather than assumed:
        /// round-trip format "R" throws <c>NotImplementedException</c> on this runtime,
        /// and "N" inserts a thousands separator ("1,234.57") that would corrupt the
        /// payload and defeat any parser. "F&lt;n&gt;" is correct across the range tested.
        /// </remarks>
        private string FormatValue(double value)
        {
            var formatted = value.ToString(_numericFormat);

            // Homie requires a dot. nanoFramework's Double exposes no
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
