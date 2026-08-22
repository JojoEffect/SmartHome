namespace SmartHome.Homie.V4.Attributes
{
    public class PropertiesAttribute : StringArrayAttributeBase
    {
        public PropertiesAttribute(IHomieEntity parent, string[] properties)
            : base($"{Constants.PropertiesAttributeTopicId}", parent, properties)
        { }
    }
}
