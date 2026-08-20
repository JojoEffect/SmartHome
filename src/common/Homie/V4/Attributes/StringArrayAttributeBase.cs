using System.Text;

namespace SmartHome.Homie.V4.Attributes
{
    public abstract class StringArrayAttributeBase : AttributeBase
    {
        private string[] _value;

        public StringArrayAttributeBase(string topicIdentifier, IHomieEntity parent, string[] stringArray)
            : base(topicIdentifier, parent)
        {
            _value = stringArray;
        }

        public virtual string[] Value { get => _value; private set => _value = value; }

        public override byte[] GetPayload()
        {
            if (Value == null || Value.Length == 0)
            {
                return Encoding.UTF8.GetBytes(string.Empty);
            }

            var payload = new StringBuilder();
            for (int i = 0; i < Value.Length; i++)
            {
                if (i > 0)
                {
                    payload.Append(",");
                }
                payload.Append(Value[i]);
            }
            return Encoding.UTF8.GetBytes(payload.ToString());
        }

        public void Add(string value)
        {
            if (Value == null)
            {
                Value = new string[1];
                Value[0] = value;
            }
            else
            {
                var newValues = new string[Value.Length + 1];
                for (int i = 0; i < Value.Length; i++)
                {
                    newValues[i] = Value[i];
                }
                newValues[Value.Length] = value;
                Value = newValues;
            }
        }
    }
}
