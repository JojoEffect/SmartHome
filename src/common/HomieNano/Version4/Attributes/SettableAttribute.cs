namespace HomieNano.Version4.Attributes
{
    public class SettableAttribute : BoolAttributeBase
    {
        public SettableAttribute(IHomieEntity parent, bool initialValue = false)
            : base($"{Constants.SettableAttributeTopicId}", parent, initialValue)
        {
        }
    }
}
