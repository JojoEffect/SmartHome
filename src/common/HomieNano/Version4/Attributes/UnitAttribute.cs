using HomieNano.Version4.Enums;
using System.Text;

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

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(Value.ToString().ToLower());
    }
}
