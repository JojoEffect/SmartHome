namespace HomieNano.Version4.Attributes
{
    public class ExtensionsAttribute : StringArrayAttributeBase
    {
        public ExtensionsAttribute(IHomieEntity parent, string[] extensions)
            : base($"{Constants.ExtensionAttributeTopicId}", parent, extensions)
        {
        }
    }
}
