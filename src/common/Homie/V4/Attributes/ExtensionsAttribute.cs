namespace SmartHome.Homie.V4.Attributes
{
    public class ExtensionsAttribute : StringArrayAttributeBase
    {
        public ExtensionsAttribute(IHomieEntity parent, string[] extensions)
            : base($"{Constants.ExtensionAttributeTopicId}", parent, extensions)
        {
        }

        // No GetPayload override: the base produces the identical comma-separated
        // payload, as it already does for PropertiesAttribute.
    }
}
