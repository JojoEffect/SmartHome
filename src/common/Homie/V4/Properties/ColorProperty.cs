using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.EventArgs;
using System.Text;

namespace SmartHome.Homie.V4.Properties
{
    public struct HomieColor
    {
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }

        // Homie v4 defines an rgb payload as three comma-separated decimal components,
        // "<r>,<g>,<b>". This emitted 6-digit hex ("FF8000") and TryParse rejected
        // anything that wasn't exactly 6 characters, so a conforming controller sending
        // "255,128,0" was dropped silently -- no value change, no reflection, no log.
        //
        // Dropping the hex also drops a format specifier: $"{R:X2}" compiles to
        // string.Format, whose specifier branch in nanoFramework is reflection-only and
        // throws NotImplementedException outright on a NoReflection target.
        public override readonly string ToString() => $"{R},{G},{B}";

        public static bool TryParse(string value, out HomieColor color)
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
            string topicId,
            string name,
            string format = "",
            bool settable = false,
            bool retained = true,
            Unit unit = Unit.None,
            HomieColor initialValue = default)
            : base(topicId, name, DataType.Color, format, settable, retained, unit)
        {
            Value = initialValue;
        }

        public HomieColor Value { get; private set; }

        public override event PropertyUpdateHandler? OnUpdate;

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(Value.ToString());

        public void Update(HomieColor newValue)
        {
            Value = newValue;
            PropertyUpdateEventArgs args = new(this, Encoding.UTF8.GetBytes(newValue.ToString()));
            OnUpdate?.Invoke(args);
        }

        internal override string? Validate(string value)
        {
            // Only rgb is implemented, whatever $format declares -- an hsv property would
            // be measured against the wrong grammar here, which is a pre-existing gap in
            // HomieColor rather than something this check introduces.
            if (!HomieColor.TryParse(value, out _))
            {
                return "not an '<r>,<g>,<b>' triple with components in 0..255";
            }

            return null;
        }

        internal override void SetInternal(string value)
        {
            // Validate() has already proven this parses; the guard is what keeps that
            // structural rather than a comment.
            if (HomieColor.TryParse(value, out var parsed))
            {
                Update(parsed);
            }
        }
    }
}
