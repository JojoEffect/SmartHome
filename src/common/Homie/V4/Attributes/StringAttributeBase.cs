using System.Text;

namespace SmartHome.Homie.V4.Attributes
{
    public abstract class StringAttributeBase : AttributeBase
    {
        private string _value;

        public StringAttributeBase(string topicIdentifier, IHomieEntity parent, string value)
            : base(topicIdentifier, parent)
        {
            _value = value;
        }

        public virtual string Value { get => _value; set => _value = value; }

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(Value);
    }
}
