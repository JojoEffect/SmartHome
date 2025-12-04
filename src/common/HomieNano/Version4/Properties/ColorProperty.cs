using HomieNano.Version4.Enums;
using HomieNano.Version4.EventArgs;
using System.Text;
using System; // for Convert

namespace HomieNano.Version4.Properties
{
    public struct HomieColor
    {
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public override string ToString() => $"{R:X2}{G:X2}{B:X2}";
        public static bool TryParse(string value, out HomieColor color)
        {
            color = default;
            if (value == null || value.Length != 6) return false;
            try
            {
                color.R = (byte)Convert.ToInt32(value.Substring(0,2),16);
                color.G = (byte)Convert.ToInt32(value.Substring(2,2),16);
                color.B = (byte)Convert.ToInt32(value.Substring(4,2),16);
                return true;
            }
            catch { return false; }
        }
    }

    public delegate void ColorPropertySetHandler(ColorPropertySetEventArgs args);

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

        public event ColorPropertySetHandler? OnSet;

        public override event PropertyUpdateHandler? OnUpdate;

        public void Update(HomieColor newValue)
        {
            Value = newValue;
            PropertyUpdateEventArgs args = new(this, Encoding.UTF8.GetBytes(newValue.ToString()));
            OnUpdate?.Invoke(args);
        }

        internal override void SetInternal(string value)
        {
            if (HomieColor.TryParse(value, out var parsed))
            {
                Value = parsed;
                ColorPropertySetEventArgs colorArgs = new(this, parsed);
                OnSet?.Invoke(colorArgs);
            }
        }
    }
}
