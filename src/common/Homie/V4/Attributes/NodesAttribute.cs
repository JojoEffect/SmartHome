namespace SmartHome.Homie.V4.Attributes
{
    public class NodesAttribute : StringArrayAttributeBase
    {
        public NodesAttribute(IHomieEntity parent, string[] nodes)
            : base($"{Constants.NodesAttributeTopicId}", parent, nodes)
        {
        }

        // No GetPayload override: the base joins on "," via StringUtils, which is what
        // this override did -- except the base also guards the null/empty case, where
        // StringUtils.Join throws.
    }
}
