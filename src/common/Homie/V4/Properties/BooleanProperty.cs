using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.EventArgs;
using System.Text;

namespace SmartHome.Homie.V4.Properties
{
    public class BooleanProperty : PropertyBase
    {
        public BooleanProperty(
            string topicId,
            string name,
            string format = "",
            bool settable = false,
            bool retained = true,
            Unit unit = Unit.None,
            bool initialValue = false)
            : base(topicId, name, DataType.Boolean, format, settable, retained, unit)
        {
            Value = initialValue;
        }

        public bool Value { get; private set; }

        public override event PropertyUpdateHandler? OnUpdate;

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(Format(Value));

        public void Update(bool newValue)
        {
            Value = newValue;
            PropertyUpdateEventArgs args = new(this, Encoding.UTF8.GetBytes(Format(newValue)));
            OnUpdate?.Invoke(args);
        }

        // Homie v4 defines the boolean payload as exactly "true" or "false". This used to
        // be bool.ToString() on the announce path only, which returns "True"/"False", so
        // every boolean property was announced with a payload the type does not permit --
        // retained, and not corrected until the first update or /set hit the same topic.
        // Update() always had it right, which is what made the mismatch easy to miss.
        private static string Format(bool value) => value ? "true" : "false";

        internal override string? Validate(string value)
        {
            // Exactly "true" or "false", which is all Homie v4 permits. This used to
            // accept "1" and "True" as well and -- worse -- treat everything else as
            // false, so a controller sending "on" or a typo got a device that published
            // 'false' and acted on it. At the broker that is indistinguishable from a
            // controller having deliberately asked for false.
            if (value == "true" || value == "false")
            {
                return null;
            }

            return "not 'true' or 'false'";
        }

        internal override void SetInternal(string value)
        {
            // Validate() has already ruled out everything else.
            Update(value == "true");
        }
    }
}
