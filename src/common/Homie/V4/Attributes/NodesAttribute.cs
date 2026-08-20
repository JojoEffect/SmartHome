using System.Text;
using SmartHome.Text;

namespace SmartHome.Homie.V4.Attributes
{
    public class NodesAttribute : StringArrayAttributeBase
    {
        public NodesAttribute(IHomieEntity parent, string[] nodes) 
            : base($"{Constants.NodesAttributeTopicId}", parent, nodes)
        {
        }

        public override byte[] GetPayload()
        {
            return Encoding.UTF8.GetBytes(StringUtils.Join(",", Value));
        }
    }
}
