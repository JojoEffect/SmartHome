namespace SmartHome.DeviceModel.Formats
{
    /// <summary>
    /// Human-readable names for a boolean property's two values, e.g. <c>off</c> and
    /// <c>on</c>, or <c>closed</c> and <c>open</c>.
    /// </summary>
    /// <remarks>
    /// **Descriptive, never payload-defining.** A boolean payload is the literal
    /// <c>true</c> or <c>false</c> whatever these say; the labels exist so a controller
    /// can draw a switch that reads "open/closed" instead of "true/false". Worth being
    /// explicit about, because a reader who assumed otherwise would build a device that
    /// refuses <c>true</c>.
    ///
    /// An adapter whose convention has no such notion simply drops these. One whose
    /// convention has a similar-looking pair that *does* define the payloads must not
    /// map these onto it -- same shape, opposite meaning.
    /// </remarks>
    public class BooleanLabels
    {
        /// <exception cref="System.ArgumentException">Either label is null or empty.</exception>
        public BooleanLabels(string falseLabel, string trueLabel)
        {
            // Both or neither: a half-declared pair would leave a controller rendering
            // one side of a switch, and conventions that carry labels at all generally
            // require both entries when the format is given.
            if (string.IsNullOrEmpty(falseLabel) || string.IsNullOrEmpty(trueLabel))
            {
                throw new System.ArgumentException("Boolean labels must name both the false and the true value.");
            }

            False = falseLabel;
            True = trueLabel;
        }

        /// <summary>What the value <c>false</c> is called.</summary>
        public string False { get; }

        /// <summary>What the value <c>true</c> is called.</summary>
        public string True { get; }

        /// <summary>
        /// Reads the <c>&lt;false&gt;,&lt;true&gt;</c> form, e.g. <c>"off,on"</c>.
        /// </summary>
        /// <remarks>
        /// Entries are trimmed, for the same reason an enum's options are. Anything that
        /// is not exactly two non-empty entries declares no labels.
        /// </remarks>
        public static bool TryParse(string format, out BooleanLabels? labels)
        {
            labels = null;

            if (format == null || format.Length == 0)
            {
                return false;
            }

            var parts = format.Split(',');
            if (parts.Length != 2)
            {
                return false;
            }

            var falseLabel = parts[0].Trim();
            var trueLabel = parts[1].Trim();

            if (falseLabel.Length == 0 || trueLabel.Length == 0)
            {
                return false;
            }

            labels = new BooleanLabels(falseLabel, trueLabel);
            return true;
        }

        /// <summary>The <c>&lt;false&gt;,&lt;true&gt;</c> rendering.</summary>
        public override string ToString() => $"{False},{True}";
    }
}
