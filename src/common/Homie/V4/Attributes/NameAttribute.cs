using System.Text;

namespace SmartHome.Homie.V4.Attributes
{
    public class NameAttribute : StringAttributeBase
    {
        public NameAttribute(IHomieEntity parent, string name) 
            : base($"{Constants.NameAttributeTopicId}", parent, name)
        {
        }

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(Value);
    }
}
