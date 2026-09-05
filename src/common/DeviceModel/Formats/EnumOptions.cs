using System.Collections;

namespace SmartHome.DeviceModel.Formats
{
    /// <summary>
    /// The set of values an enum property may hold.
    /// </summary>
    /// <remarks>
    /// Trimmed once, at construction, so that no consumer has to remember to. A device
    /// author writing <c>"low, medium, high"</c> means the same three names as
    /// <c>"low,medium,high"</c>, and a reader that forgot to trim would refuse two of
    /// them.
    ///
    /// Note that this is a decision about the *declaration*, not about payloads: a
    /// controller sending <c>" low"</c> is sending a different value, and it is refused.
    /// Homie v5 says leading and trailing whitespace in an enum value is significant,
    /// which is exactly what makes trimming the declaration safe -- an option that only
    /// differed from another by its surrounding spaces could never have been addressed
    /// separately anyway.
    ///
    /// Immutable: a property's option set must not change after it has been announced.
    /// </remarks>
    public class EnumOptions
    {
        private readonly string[] _values;

        /// <param name="values">
        /// The permitted values. Trimmed, and entries that are empty afterwards are
        /// dropped -- an empty string is not a valid payload for any Homie datatype, so
        /// an empty option could never be selected.
        /// </param>
        /// <exception cref="System.ArgumentException">Nothing is left after trimming.</exception>
        public EnumOptions(string[] values)
        {
            if (values == null)
            {
                throw new System.ArgumentException("An enum property's options must not be null.");
            }

            _values = Sanitise(values);

            if (_values.Length == 0)
            {
                throw new System.ArgumentException("An enum property must declare at least one option.");
            }
        }

        /// <summary>The permitted values, in the order they were declared.</summary>
        /// <remarks>
        /// A fresh array each time. The stored one is this type's whole state, and
        /// handing it out would let a caller rewrite a declaration the device has
        /// already announced.
        /// </remarks>
        public string[] Values
        {
            get
            {
                var copy = new string[_values.Length];
                System.Array.Copy(_values, copy, _values.Length);
                return copy;
            }
        }

        /// <summary>How many options were declared. Always at least one.</summary>
        public int Count => _values.Length;

        /// <summary>Whether a payload is one of the declared values, compared exactly.</summary>
        public bool Contains(string value)
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
        /// Reads the comma-separated form a device author writes.
        /// </summary>
        /// <remarks>
        /// Returns false when nothing usable is declared -- an empty string, or only
        /// separators. A property with no options declared has declared nothing for a
        /// payload to violate, and refusing every payload there would turn a device
        /// author's omission into a property no controller can ever set: a worse failure
        /// than the one the check exists to stop.
        /// </remarks>
        public static bool TryParse(string format, out EnumOptions? options)
        {
            options = null;

            if (format == null || format.Length == 0)
            {
                return false;
            }

            var values = Sanitise(format.Split(','));
            if (values.Length == 0)
            {
                return false;
            }

            options = new EnumOptions(values);
            return true;
        }

        /// <summary>The comma-separated rendering, in declaration order.</summary>
        public override string ToString()
        {
            // Hand-rolled rather than a Join helper: this assembly deliberately
            // references nothing but the runtime.
            var joined = string.Empty;
            for (int i = 0; i < _values.Length; i++)
            {
                joined = i == 0 ? _values[i] : $"{joined},{_values[i]}";
            }

            return joined;
        }

        /// <summary>The whole rule: trim every entry, drop the ones left empty.</summary>
        private static string[] Sanitise(string[] values)
        {
            // ArrayList rather than a counted second pass: nanoFramework has no
            // List<string>, and the two-pass version had to repeat the "is this entry
            // kept" test in both passes, which is exactly the kind of duplicated
            // predicate that drifts.
            var kept = new ArrayList();
            for (int i = 0; i < values.Length; i++)
            {
                var value = values[i];
                if (value == null)
                {
                    continue;
                }

                var trimmed = value.Trim();
                if (trimmed.Length > 0)
                {
                    kept.Add(trimmed);
                }
            }

            return (string[])kept.ToArray(typeof(string));
        }
    }
}
