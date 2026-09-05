using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.EventArgs;
using SmartHome.DeviceModel.Formats;
using System.Text;

namespace SmartHome.DeviceModel.Properties
{
    public class BooleanProperty : PropertyBase
    {
        public BooleanProperty(
            string id,
            string name,
            BooleanLabels? labels = null,
            bool settable = false,
            bool retained = true,
            string unit = Units.None,
            QuantityKind quantityKind = QuantityKind.None,
            bool initialValue = false)
            : base(id, name, DataType.Boolean, settable, retained, unit, quantityKind)
        {
            Labels = labels;
            Value = initialValue;
        }

        public bool Value { get; private set; }

        /// <summary>
        /// What to call the two values, or null if this property says nothing about that.
        /// Descriptive only -- see <see cref="BooleanLabels"/>; the payloads are
        /// <c>true</c> and <c>false</c> regardless.
        /// </summary>
        public BooleanLabels? Labels { get; }

        public override event PropertyUpdateHandler? OnUpdate;

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(Format(Value));

        public void Update(bool newValue)
        {
            Value = newValue;
            PropertyUpdateEventArgs args = new(this, Encoding.UTF8.GetBytes(Format(newValue)));
            OnUpdate?.Invoke(args);
        }

        /// <summary>Declares the value this property is heading for. See <see cref="PropertyBase.Target"/>.</summary>
        public void SetTarget(bool value) => SetTargetPayload(Format(value));

        // A boolean payload is exactly "true" or "false". This used to be bool.ToString()
        // on the announce path only, which returns "True"/"False", so every boolean
        // property was announced with a payload the type does not permit -- retained, and
        // not corrected until the first update or command hit the same topic. Update()
        // always had it right, which is what made the mismatch easy to miss.
        private static string Format(bool value) => value ? "true" : "false";

        internal override string? Validate(string value)
        {
            // Exactly "true" or "false", which is all the conventions permit. This used
            // to accept "1" and "True" as well and -- worse -- treat everything else as
            // false, so a controller sending "on" or a typo got a device that published
            // 'false' and acted on it. At the broker that is indistinguishable from a
            // controller having deliberately asked for false.
            //
            // Note that the labels are not consulted: they name the values for a human,
            // they do not define the payloads, so a property labelled "off,on" still
            // takes "true" and still refuses "on".
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
