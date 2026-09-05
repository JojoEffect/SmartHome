using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.EventArgs;
using System;
using System.Text;

namespace SmartHome.DeviceModel.Properties
{
    /// <summary>
    /// An instant in time, published as ISO 8601.
    /// </summary>
    /// <remarks>
    /// Homie v5 only. Homie v4 has no such datatype, so a v4 adapter must refuse a
    /// device carrying one rather than invent a spelling for it; Home Assistant has a
    /// timestamp device class and can take the same ISO 8601 text.
    ///
    /// Always UTC. nanoFramework's <see cref="DateTime"/> has no notion of local time --
    /// its <c>Kind</c> is hard-coded to UTC -- so this type publishes a trailing 'Z' and
    /// converts an incoming offset rather than pretending to carry one.
    /// </remarks>
    public class DateTimeProperty : PropertyBase
    {
        public DateTimeProperty(
            string id,
            string name,
            bool settable = false,
            bool retained = true,
            string unit = Units.None,
            QuantityKind quantityKind = QuantityKind.None)
            : base(id, name, DataType.DateTime, settable, retained, unit, quantityKind)
        {
            Value = DateTime.MinValue;
        }

        public DateTime Value { get; private set; }

        public override event PropertyUpdateHandler? OnUpdate;

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(FormatValue(Value));

        public void Update(DateTime newValue)
        {
            Value = newValue;
            PropertyUpdateEventArgs args = new(this, Encoding.UTF8.GetBytes(FormatValue(newValue)));
            OnUpdate?.Invoke(args);
        }

        /// <summary>Declares the value this property is heading for. See <see cref="PropertyBase.Target"/>.</summary>
        public void SetTarget(DateTime value) => SetTargetPayload(FormatValue(value));

        internal override string? Validate(string value)
        {
            if (!TryParseValue(value, out _))
            {
                return "not an ISO 8601 date and time, e.g. '2026-09-05T14:30:00Z'";
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
        /// Renders <c>yyyy-MM-ddTHH:mm:ssZ</c>.
        /// </summary>
        /// <remarks>
        /// Assembled from the components rather than through
        /// <c>DateTime.ToString(format)</c>. That overload goes through
        /// <c>DateTimeFormat</c> and <c>DateTimeFormatInfo.CurrentInfo</c>, so its output
        /// depends on culture data the same way <c>FloatProperty</c>'s did before it was
        /// pinned -- and the payload has to be one exact shape whatever else is linked
        /// into the image.
        ///
        /// Sub-second precision is dropped: this rendering has no place for it, and a
        /// device publishing a timestamp to the millisecond is not something either
        /// convention asks for.
        /// </remarks>
        private static string FormatValue(DateTime value)
        {
            var builder = new StringBuilder(20);
            AppendPadded(builder, value.Year, 4);
            builder.Append('-');
            AppendPadded(builder, value.Month, 2);
            builder.Append('-');
            AppendPadded(builder, value.Day, 2);
            builder.Append('T');
            AppendPadded(builder, value.Hour, 2);
            builder.Append(':');
            AppendPadded(builder, value.Minute, 2);
            builder.Append(':');
            AppendPadded(builder, value.Second, 2);
            builder.Append('Z');

            return builder.ToString();
        }

        /// <remarks>
        /// Zero-padding by hand because the natural spelling of this, <c>$"{n:D2}"</c>,
        /// compiles to <c>string.Format</c>, whose format-specifier branch is
        /// reflection-only in nanoFramework and throws outright on a NoReflection target.
        /// </remarks>
        private static void AppendPadded(StringBuilder builder, int value, int width)
        {
            var digits = value.ToString();
            for (int i = digits.Length; i < width; i++)
            {
                builder.Append('0');
            }

            builder.Append(digits);
        }

        /// <summary>
        /// Reads <c>YYYY-MM-DDTHH:MM:SS</c> followed by an optional fractional part and
        /// an optional zone: nothing, <c>Z</c>, or <c>+hh:mm</c> / <c>-hhmm</c>.
        /// </summary>
        /// <remarks>
        /// Hand-rolled rather than <c>DateTime.TryParse</c>, which is a native call whose
        /// accepted grammar is not part of the runtime's contract -- so what it takes
        /// could differ between the ESP32 firmware and the virtual device the unit tests
        /// run on, and the whole point of validation is that the device and its tests
        /// agree.
        ///
        /// Fractional seconds are accepted and discarded, which is the same truncation
        /// the rendering does. An offset is converted to UTC; a missing zone is taken as
        /// UTC, since this runtime has no local time to interpret it against.
        /// </remarks>
        private static bool TryParseValue(string value, out DateTime result)
        {
            result = DateTime.MinValue;

            if (value == null || value.Length < 19)
            {
                return false;
            }

            if (value[4] != '-' || value[7] != '-' || value[10] != 'T' || value[13] != ':' || value[16] != ':')
            {
                return false;
            }

            if (!TryReadDigits(value, 0, 4, out var year) ||
                !TryReadDigits(value, 5, 2, out var month) ||
                !TryReadDigits(value, 8, 2, out var day) ||
                !TryReadDigits(value, 11, 2, out var hour) ||
                !TryReadDigits(value, 14, 2, out var minute) ||
                !TryReadDigits(value, 17, 2, out var second))
            {
                return false;
            }

            // The runtime's own limits. Constructing outside them throws, and a throw out
            // of here would escape Set() on the transport's dispatch thread.
            if (year < 1601 || year > 3000 || month < 1 || month > 12 || hour > 23 || minute > 59 || second > 59)
            {
                return false;
            }

            if (day < 1 || day > DateTime.DaysInMonth(year, month))
            {
                return false;
            }

            var index = 19;

            // Fractional seconds: a dot followed by at least one digit, all discarded.
            if (index < value.Length && value[index] == '.')
            {
                index++;
                var firstDigit = index;
                while (index < value.Length && value[index] >= '0' && value[index] <= '9')
                {
                    index++;
                }

                if (index == firstDigit)
                {
                    return false;
                }
            }

            if (!TryReadOffsetMinutes(value, index, out var offsetMinutes))
            {
                return false;
            }

            try
            {
                var local = new DateTime(year, month, day, hour, minute, second);
                result = offsetMinutes == 0
                    ? local
                    : new DateTime(local.Ticks - (offsetMinutes * TimeSpan.TicksPerMinute));
            }
            catch (Exception)
            {
                // A backstop, not the parser: everything above has already been range
                // checked, and only applying an offset at the very edge of the
                // representable range can still fail here.
                return false;
            }

            return true;
        }

        /// <summary>
        /// Reads the zone suffix, as minutes to subtract to reach UTC. An absent zone and
        /// a 'Z' both mean zero.
        /// </summary>
        private static bool TryReadOffsetMinutes(string value, int index, out int offsetMinutes)
        {
            offsetMinutes = 0;

            if (index == value.Length)
            {
                return true;
            }

            if (value[index] == 'Z')
            {
                return index + 1 == value.Length;
            }

            var sign = value[index] == '+' ? 1 : value[index] == '-' ? -1 : 0;
            if (sign == 0)
            {
                return false;
            }

            index++;

            // Both spellings ISO 8601 allows: +hh:mm and +hhmm.
            var remaining = value.Length - index;
            if (remaining == 5)
            {
                if (value[index + 2] != ':')
                {
                    return false;
                }
            }
            else if (remaining != 4)
            {
                return false;
            }

            var minutesAt = remaining == 5 ? index + 3 : index + 2;
            if (!TryReadDigits(value, index, 2, out var hours) ||
                !TryReadDigits(value, minutesAt, 2, out var minutes))
            {
                return false;
            }

            if (hours > 14 || minutes > 59)
            {
                return false;
            }

            offsetMinutes = sign * ((hours * 60) + minutes);
            return true;
        }

        /// <summary>Reads exactly <paramref name="length"/> ASCII digits.</summary>
        private static bool TryReadDigits(string value, int start, int length, out int result)
        {
            result = 0;

            if (start + length > value.Length)
            {
                return false;
            }

            for (int i = start; i < start + length; i++)
            {
                var c = value[i];
                if (c < '0' || c > '9')
                {
                    return false;
                }

                result = (result * 10) + (c - '0');
            }

            return true;
        }
    }
}
