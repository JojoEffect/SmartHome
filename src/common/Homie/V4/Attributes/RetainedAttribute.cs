namespace SmartHome.Homie.V4.Attributes
{
    public class RetainedAttribute : BoolAttributeBase
    {
        public RetainedAttribute(IHomieEntity parent, bool initialValue = true) 
            : base($"{Constants.RetainedAttributeTopicId}", parent, initialValue)
        {
        }
    }
}
