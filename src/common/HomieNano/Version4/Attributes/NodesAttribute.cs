using System;

namespace HomieNano.Version4.Attributes
{
    public class NodesAttribute : StringArrayAttributeBase
    {
        public NodesAttribute(IHomieEntity parent, string[] nodes) 
            : base($"{Constants.NodesAttributeTopicId}", parent, nodes)
        {
        }
    }
}
