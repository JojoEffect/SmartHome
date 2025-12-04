using HomieNano.Version4.Enums;

namespace HomieNano.Version4.Attributes
{
    public class UnitAttribute : AttributeBase
    {
        public UnitAttribute(IHomieEntity parent, Unit unit)
            : base($"{Constants.UnitAttributeTopicId}", parent)
        {
            Value = unit;
        }

        public Unit Value { get; internal set; }

        public override string GetPayload() => Value.ToString().ToLower();
    }
}
