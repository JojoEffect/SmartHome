using System.Collections;
using System.Text;

namespace SmartHome.DeviceModel.Formats
{
    /// <summary>
    /// The colour encodings a colour property supports, most preferred first.
    /// </summary>
    /// <remarks>
    /// An ordered list drawn from <c>rgb</c>, <c>hsv</c> and <c>xyz</c>. Ordered because
    /// conventions differ in how many encodings they can declare: one that carries only
    /// a single encoding takes <see cref="Preferred"/> and the rest go unsaid on that
    /// wire.
    ///
    /// Declaring an encoding is a statement about what a *controller* may send. This
    /// model's own colour value is an RGB triple and parses only the rgb payload form
    /// (see <c>ColorProperty</c>), so a property that declares <c>hsv</c> or <c>xyz</c>
    /// today advertises more than the value type can hold. That gap predates this model
    /// and is carried across deliberately rather than closed here, so that behaviour on
    /// the wire does not move; it is the value type that needs widening, not this
    /// declaration.
    /// </remarks>
    public class ColorFormats
    {
        /// <summary>The token for a red/green/blue triple.</summary>
        public const string Rgb = "rgb";

        /// <summary>The token for hue/saturation/value.</summary>
        public const string Hsv = "hsv";

        /// <summary>The token for CIE 1931 x/y chromaticity.</summary>
        public const string Xyz = "xyz";

        private readonly string[] _values;

        /// <param name="values">
        /// The supported encodings, most preferred first. Each must be one of
        /// <see cref="Rgb"/>, <see cref="Hsv"/> or <see cref="Xyz"/>.
        /// </param>
        /// <exception cref="System.ArgumentException">
        /// The list is empty, or names an encoding that is not one of the three.
        /// </exception>
        public ColorFormats(string[] values)
        {
            if (values == null || values.Length == 0)
            {
                throw new System.ArgumentException("A colour property must declare at least one encoding.");
            }

            var kept = new ArrayList();
            for (int i = 0; i < values.Length; i++)
            {
                var value = values[i] == null ? string.Empty : values[i].Trim();
                if (!IsKnown(value))
                {
                    throw new System.ArgumentException(
                        $"Unknown colour encoding '{value}': expected one of '{Rgb}', '{Hsv}' or '{Xyz}'.");
                }

                kept.Add(value);
            }

            _values = (string[])kept.ToArray(typeof(string));
        }

        /// <summary>The supported encodings, most preferred first.</summary>
        /// <remarks>A fresh array each time, so a caller cannot rewrite the declaration.</remarks>
        public string[] Values
        {
            get
            {
                var copy = new string[_values.Length];
                System.Array.Copy(_values, copy, _values.Length);
                return copy;
            }
        }

        /// <summary>
        /// The preferred encoding, and the only one a convention that declares just one
        /// can carry.
        /// </summary>
        public string Preferred => _values[0];

        /// <summary>Whether an encoding was declared.</summary>
        public bool Supports(string value)
        {
            for (int i = 0; i < _values.Length; i++)
            {
                if (_values[i] == value)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reads the comma-separated form a device author writes, e.g. <c>"rgb,hsv"</c>
        /// or plain <c>"rgb"</c>.
        /// </summary>
        /// <remarks>
        /// Returns false rather than throwing when an entry is not a known encoding, so
        /// that a malformed declaration leaves a property with none rather than stopping
        /// the device from being built. Same call as every other format here.
        /// </remarks>
        public static bool TryParse(string format, out ColorFormats? formats)
        {
            formats = null;

            if (format == null || format.Length == 0)
            {
                return false;
            }

            var parts = format.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                if (!IsKnown(parts[i].Trim()))
                {
                    return false;
                }
            }

            formats = new ColorFormats(parts);
            return true;
        }

        /// <summary>The comma-separated rendering, most preferred first.</summary>
        public override string ToString()
        {
            // Same call as EnumOptions.ToString, and see the note there: a StringBuilder
            // rather than a reference to SmartHome.Text for a comma, and rather than
            // accumulating into a string.
            var builder = new StringBuilder();
            for (int i = 0; i < _values.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(_values[i]);
            }

            return builder.ToString();
        }

        private static bool IsKnown(string value) => value == Rgb || value == Hsv || value == Xyz;
    }
}
