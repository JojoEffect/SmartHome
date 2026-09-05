using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.EventArgs;
using System;
using System.Text;

namespace SmartHome.DeviceModel.Properties
{
    /// <summary>
    /// An elapsed time, published as an ISO 8601 duration -- <c>PT12H5M46S</c>,
    /// <c>PT5M</c>.
    /// </summary>
    /// <remarks>
    /// Homie v5 only; a v4 adapter must refuse a device carrying one. The convention
    /// spells the format <c>PTxHxMxS</c> with each component optional, so there are no
    /// day, month or year components: a duration longer than a day is published as hours.
    /// </remarks>
    public class DurationProperty : PropertyBase
    {
        public DurationProperty(
            string id,
            string name,
            bool settable = false,
            bool retained = true,
            string unit = Units.None,
            QuantityKind quantityKind = QuantityKind.None)
            : base(id, name, DataType.Duration, settable, retained, unit, quantityKind)
        {
            Value = TimeSpan.Zero;
        }

        public TimeSpan Value { get; private set; }

        public override event PropertyUpdateHandler? OnUpdate;

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(FormatValue(Value));

        public void Update(TimeSpan newValue)
        {
            Value = EnsurePublishable(newValue);
            PropertyUpdateEventArgs args = new(this, Encoding.UTF8.GetBytes(FormatValue(newValue)));
            OnUpdate?.Invoke(args);
        }

        /// <summary>Declares the value this property is heading for. See <see cref="PropertyBase.Target"/>.</summary>
        public void SetTarget(TimeSpan value) => SetTargetPayload(FormatValue(EnsurePublishable(value)));

        /// <remarks>
        /// An ISO 8601 duration in the <c>PTxHxMxS</c> form has no way to say "negative",
        /// so a negative <see cref="TimeSpan"/> has no representation at all. Refused at
        /// the boundary, like a non-finite float: the caller is the only one who knows
        /// what a negative elapsed time means.
        /// </remarks>
        private TimeSpan EnsurePublishable(TimeSpan value)
        {
            if (value.Ticks < 0)
            {
                throw new ArgumentException($"Property '{Id}' cannot publish a negative duration.");
            }

            return value;
        }

        internal override string? Validate(string value)
        {
            if (!TryParseValue(value, out _))
            {
                return "not an ISO 8601 duration, e.g. 'PT12H5M46S'";
            }

            return null;
        }

        internal override void SetInternal(string value)
        {
            if (TryParseValue(value, out var parsed))
            {
                Update(parsed);
            }
        }

        /// <summary>
        /// Renders <c>PT[h]H[m]M[s]S</c>, omitting the components that are zero.
        /// </summary>
        /// <remarks>
        /// Hours are the total hours, days included, because the format has no day
        /// component: two days is <c>PT48H</c>.
        ///
        /// Sub-second precision is truncated. <c>xS</c> would take a fractional number,
        /// but rendering one on this runtime means either <c>double.ToString</c> -- which
        /// prints 1.5 as 1.5 and 21.5 as 21.499999999999999 -- or hand-assembling the
        /// digits, and no convention this model targets asks for durations finer than a
        /// second.
        /// </remarks>
        private static string FormatValue(TimeSpan value)
        {
            var totalSeconds = value.Ticks / TimeSpan.TicksPerSecond;
            var hours = totalSeconds / 3600;
            var minutes = (totalSeconds / 60) % 60;
            var seconds = totalSeconds % 60;

            var builder = new StringBuilder(16);
            builder.Append("PT");

            if (hours != 0)
            {
                builder.Append(hours);
                builder.Append('H');
            }

            if (minutes != 0)
            {
                builder.Append(minutes);
                builder.Append('M');
            }

            // Zero has to render as something, and PT0S is the conventional spelling of
            // it. A bare "PT" is not a duration.
            if (seconds != 0 || (hours == 0 && minutes == 0))
            {
                builder.Append(seconds);
                builder.Append('S');
            }

            return builder.ToString();
        }

        /// <summary>
        /// Reads <c>PT</c> followed by at least one of <c>&lt;n&gt;H</c>,
        /// <c>&lt;n&gt;M</c>, <c>&lt;n&gt;S</c>, in that order.
        /// </summary>
        /// <remarks>
        /// Strict about the order, because that is what makes the grammar unambiguous:
        /// "PT1M" is one minute and "PT1H" is one hour, so a reader that accepted them in
        /// any order would still have to reject a repeat, and a repeat is the only thing
        /// a wrong order can be.
        ///
        /// Fractional components are refused rather than truncated. The rendering above
        /// never produces one, so accepting a payload this device could not have written
        /// and then silently changing it is the worse of the two behaviours.
        /// </remarks>
        private static bool TryParseValue(string value, out TimeSpan result)
        {
            result = TimeSpan.Zero;

            if (value == null || value.Length < 4 || value[0] != 'P' || value[1] != 'T')
            {
                return false;
            }

            long totalSeconds = 0;
            var index = 2;
            var seenAny = false;

            // The designators in the order they may appear; each at most once.
            var designators = new char[] { 'H', 'M', 'S' };
            var multipliers = new long[] { 3600, 60, 1 };

            for (int slot = 0; slot < designators.Length && index < value.Length; slot++)
            {
                var firstDigit = index;
                long magnitude = 0;

                while (index < value.Length && value[index] >= '0' && value[index] <= '9')
                {
                    magnitude = (magnitude * 10) + (value[index] - '0');

                    // Bounded well below where the multiplication below could overflow a
                    // long, and far beyond any duration a device has business publishing.
                    if (magnitude > 1000000000L)
                    {
                        return false;
                    }

                    index++;
                }

                if (index == firstDigit)
                {
                    // No digits here: either a stray character, or this designator is
                    // simply absent and the next one is coming.
                    continue;
                }

                if (index >= value.Length || value[index] != designators[slot])
                {
                    // Digits followed by something other than this slot's designator.
                    // Try the next slot without consuming them, so "PT5M" is read as
                    // minutes rather than refused for not being hours.
                    index = firstDigit;
                    continue;
                }

                totalSeconds += magnitude * multipliers[slot];
                seenAny = true;
                index++;
            }

            if (!seenAny || index != value.Length)
            {
                return false;
            }

            result = TimeSpan.FromSeconds(totalSeconds);
            return true;
        }
    }
}
