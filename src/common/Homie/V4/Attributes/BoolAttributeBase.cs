using System.Text;

namespace SmartHome.Homie.V4.Attributes
{
    public abstract class BoolAttributeBase : AttributeBase
    {
        protected BoolAttributeBase(string topicId, IHomieEntity parent, bool initialValue)
            : base(topicId, parent)
        {
            Value = initialValue;
        }

        public virtual bool Value { get; internal set; }

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(Value ? "true" : "false");
    }
}
