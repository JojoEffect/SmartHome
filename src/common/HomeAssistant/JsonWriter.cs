using System.Text;

namespace SmartHome.HomeAssistant
{
    /// <summary>
    /// Builds one JSON object, a member at a time.
    /// </summary>
    /// <remarks>
    /// Hand-rolled rather than <c>nanoFramework.Json</c>. That package would serialize a
    /// <c>Hashtable</c> into exactly this shape, but it is a further assembly on a device
    /// that already carries seven, to emit a few dozen members of flat, machine-generated
    /// JSON. The upstream <c>nanoFramework.Iot.Device.HomeAssistant</c> binding made the
    /// same call for the same reason (its <c>MiniJson</c>).
    ///
    /// Members whose value is null or empty are skipped rather than written as
    /// <c>null</c>. Home Assistant validates a discovery payload key by key, so an
    /// explicit null is a value it has to accept for that key -- omitting the key lets
    /// the integration's own default stand, which is what "we have nothing to say about
    /// this" actually means.
    /// </remarks>
    internal sealed class JsonWriter
    {
        private readonly StringBuilder _json = new StringBuilder("{");
        private bool _empty = true;

        /// <summary>Writes a quoted string member, or nothing when <paramref name="value"/> is empty.</summary>
        public JsonWriter String(string? name, string? value)
        {
            if (value == null || value.Length == 0)
            {
                return this;
            }

            StartMember(name);
            _json.Append('"');
            Escape(value);
            _json.Append('"');
            return this;
        }

        /// <summary>
        /// Writes a member whose value is already JSON -- a nested object, or a number.
        /// </summary>
        public JsonWriter Raw(string? name, string? json)
        {
            if (json == null || json.Length == 0)
            {
                return this;
            }

            StartMember(name);
            _json.Append(json);
            return this;
        }

        public JsonWriter Int(string? name, int value)
        {
            StartMember(name);
            _json.Append(value.ToString());
            return this;
        }

        /// <summary>Writes an array of quoted strings, or nothing when the array is empty.</summary>
        public JsonWriter StringArray(string? name, string[]? values)
        {
            if (values == null || values.Length == 0)
            {
                return this;
            }

            StartMember(name);
            _json.Append('[');
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    _json.Append(',');
                }

                _json.Append('"');
                Escape(values[i]);
                _json.Append('"');
            }

            _json.Append(']');
            return this;
        }

        /// <summary>Closes the object. The writer must not be used afterwards.</summary>
        public string ToJson() => _json.Append('}').ToString();

        private void StartMember(string? name)
        {
            if (!_empty)
            {
                _json.Append(',');
            }

            _empty = false;
            _json.Append('"');
            Escape(name);
            _json.Append('"');
            _json.Append(':');
        }

        /// <remarks>
        /// Indexed rather than foreach: nanoFramework's string is not
        /// <c>IEnumerable&lt;char&gt;</c>, so foreach hands back <c>object</c> and the
        /// comparisons below do not compile. Same reason
        /// <c>NamedHomieEntityBase.ValidateTopicId</c> indexes.
        /// </remarks>
        private void Escape(string? value)
        {
            if (value == null)
            {
                return;
            }

            for (int i = 0; i < value.Length; i++)
            {
                var c = value[i];
                switch (c)
                {
                    case '"': _json.Append("\\\""); break;
                    case '\\': _json.Append("\\\\"); break;
                    case '\n': _json.Append("\\n"); break;
                    case '\r': _json.Append("\\r"); break;
                    case '\t': _json.Append("\\t"); break;
                    case '\b': _json.Append("\\b"); break;
                    case '\f': _json.Append("\\f"); break;
                    default:
                        if (c < ' ')
                        {
                            // The only characters JSON requires escaping beyond the seven
                            // above. Everything else -- including the '°' of "°C" and any
                            // other non-ASCII -- is a legal JSON string character and goes
                            // out as UTF-8, which is what MQTT carries and what Home
                            // Assistant decodes.
                            _json.Append("\\u00");
                            _json.Append(HexDigit(c >> 4));
                            _json.Append(HexDigit(c & 0xF));
                        }
                        else
                        {
                            _json.Append(c);
                        }

                        break;
                }
            }
        }

        private static char HexDigit(int nibble) =>
            (char)(nibble < 10 ? '0' + nibble : 'a' + (nibble - 10));
    }
}
