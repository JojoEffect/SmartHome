using HomieNano.Version4.Enums;
using HomieNano.Version4.EventArgs;
using System.Text;

namespace HomieNano.Version4.Properties
{
    public delegate void StringPropertySetHandler(StringPropertySetEventArgs args);

    public class StringProperty : PropertyBase
    {
        public StringProperty(
            string topicId, 
            string name,
            string format = "", 
            bool settable = false, 
            bool retained = true, 
            Unit unit = Unit.None,
            string initialValue = "") 
            : base(topicId, name, DataType.String, format, settable, retained, unit)
        {
            Value = initialValue;
        }

        public string Value { get; private set; }

        /// <summary>
        /// Event raised when the property is set by an external source.
        /// </summary>
        public event StringPropertySetHandler? OnSet;

        /// <inheritdoc/>
        public override event PropertyUpdateHandler? OnUpdate;

        public void Update(string newValue)
        {
            Value = newValue;
            PropertyUpdateEventArgs args = new(this, Encoding.UTF8.GetBytes(newValue));
            OnUpdate?.Invoke(args);
        }

        internal override void SetInternal(string value)
        {
            Value = value;
            StringPropertySetEventArgs stringArgs = new(this, value);
            OnSet?.Invoke(stringArgs);      
        }
    }
}
