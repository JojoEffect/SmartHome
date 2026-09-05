using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.EventArgs;
using SmartHome.DeviceModel.Formats;
using System.Text;

namespace SmartHome.DeviceModel.Properties
{
    /// <summary>A colour as a red/green/blue triple, each component 0..255.</summary>
    public struct ColorValue
    {
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }

        // An rgb payload is three comma-separated decimal components, "<r>,<g>,<b>".
        // This emitted 6-digit hex ("FF8000") and TryParse rejected anything that wasn't
        // exactly 6 characters, so a conforming controller sending "255,128,0" was
        // dropped silently -- no value change, no reflection, no log.
        //
        // Dropping the hex also drops a format specifier: $"{R:X2}" compiles to
        // string.Format, whose specifier branch in nanoFramework is reflection-only and
        // throws NotImplementedException outright on a NoReflection target.
        //
        // Note that Homie v5 prefixes the encoding name ("rgb,255,128,0") where v4 does
        // not. This is the bare triple, i.e. the v4 spelling; prefixing it is a v5
        // adapter's job, exactly like every other framing decision.
        public override readonly string ToString() => $"{R},{G},{B}";

        public static bool TryParse(string value, out ColorValue color)
        {
            color = default;

            if (value == null)
            {
                return false;
            }

            var parts = value.Split(',');
            if (parts.Length != 3)
            {
                return false;
            }

            if (!TryParseComponent(parts[0], out var r) ||
                !TryParseComponent(parts[1], out var g) ||
                !TryParseComponent(parts[2], out var b))
            {
                return false;
            }

            color.R = r;
            color.G = g;
            color.B = b;
            return true;
        }

        private static bool TryParseComponent(string value, out byte component)
        {
            component = 0;

            if (!int.TryParse(value.Trim(), out var parsed) || parsed < 0 || parsed > 255)
            {
                return false;
            }

            component = (byte)parsed;
            return true;
        }
    }

    public class ColorProperty : PropertyBase
    {
        public ColorProperty(
            string id,
            string name,
            ColorFormats? formats = null,
            bool settable = false,
            bool retained = true,
            string unit = Units.None,
            QuantityKind quantityKind = QuantityKind.None,
            ColorValue initialValue = default)
            : base(id, name, DataType.Color, settable, retained, unit, quantityKind)
        {
            Formats = formats;
            Value = initialValue;
        }

        public ColorValue Value { get; private set; }

        /// <summary>
        /// The colour encodings this property declares, most preferred first, or null if
        /// it declares none.
        /// </summary>
        public ColorFormats? Formats { get; }

        public override event PropertyUpdateHandler? OnUpdate;

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(Value.ToString());

        public void Update(ColorValue newValue)
        {
            Value = newValue;
            PropertyUpdateEventArgs args = new(this, Encoding.UTF8.GetBytes(newValue.ToString()));
            OnUpdate?.Invoke(args);
        }

        /// <summary>Declares the value this property is heading for. See <see cref="PropertyBase.Target"/>.</summary>
        public void SetTarget(ColorValue value) => SetTargetPayload(value.ToString());

        internal override string? Validate(string value)
        {
            // Only rgb is implemented, whatever the declared encodings say -- an hsv
            // property would be measured against the wrong grammar here, which is a
            // pre-existing gap in the value type rather than something this check
            // introduces.
            if (!ColorValue.TryParse(value, out _))
            {
                return "not an '<r>,<g>,<b>' triple with components in 0..255";
            }

            return null;
        }

        internal override void SetInternal(string value)
        {
            // Validate() has already proven this parses; the guard is what keeps that
            // structural rather than a comment.
            if (ColorValue.TryParse(value, out var parsed))
            {
                Update(parsed);
            }
        }
    }
}
