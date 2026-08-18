using System.Text;

namespace HomieNano.Version4.Attributes
{
    public class FormatAttribute : StringAttributeBase
    {
        public FormatAttribute(IHomieEntity parent, string format)
            : base($"{Constants.FormatAttributeTopicId}", parent, format)
        { }

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(Value);
    }
}
