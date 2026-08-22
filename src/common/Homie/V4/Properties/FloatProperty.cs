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
                throw new ArgumentException($"Decimals must be between 0 and 15, was {decimals}.");
            }

            Decimals = decimals;
            _numericFormat = $"F{decimals}";
            Value = initialValue;
        }

        public double Value { get; private set; }

        /// <summary>Decimal places this property publishes.</summary>
        public int Decimals { get; }

        public override event PropertyUpdateHandler? OnUpdate;

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(FormatValue(Value));

        public void Update(double newValue)
        {
            Value = newValue;
            PropertyUpdateEventArgs args = new(this, Encoding.UTF8.GetBytes(FormatValue(newValue)));
            OnUpdate?.Invoke(args);
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
