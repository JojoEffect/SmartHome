namespace SmartHome.Homie.V4.Attributes
{
    public class TypeAttribute : StringAttributeBase
    {
        public TypeAttribute(IHomieEntity parent, string type)
            : base($"{Constants.TypeAttributeTopicId}", parent, type)
        { }
    }
}
