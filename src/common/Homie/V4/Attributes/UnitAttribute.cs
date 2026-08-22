using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.Extensions;
using System.Text;

namespace SmartHome.Homie.V4.Attributes
{
    public class UnitAttribute : AttributeBase
    {
        public UnitAttribute(IHomieEntity parent, Unit unit)
            : base($"{Constants.UnitAttributeTopicId}", parent)
        {
            Value = unit;
        }

        public Unit Value { get; internal set; }

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(Value.GetString());
    }
}
