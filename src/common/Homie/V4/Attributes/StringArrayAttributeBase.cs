using SmartHome.Text;
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

        // The one place $nodes, $properties and $extensions are turned into their wire
        // format. There used to be three: this loop, an identical private copy in
        // ExtensionsAttribute, and StringUtils.Join via NodesAttribute -- and they
        // disagreed on null, the first two returning empty where StringUtils.Join throws.
        // Which one ran depended only on which subclass you happened to be holding.
        public override byte[] GetPayload()
        {
            if (Value == null || Value.Length == 0)
            {
                return Encoding.UTF8.GetBytes(string.Empty);
            }

            return Encoding.UTF8.GetBytes(StringUtils.Join(",", Value));
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
