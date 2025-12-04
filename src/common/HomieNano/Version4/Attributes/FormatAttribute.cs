namespace HomieNano.Version4.Attributes
{
    public class FormatAttribute : StringAttributeBase
    {
        public FormatAttribute(IHomieEntity parent, string format)
            : base($"{Constants.FormatAttributeTopicId}", parent, format)
        { }
    }
}
