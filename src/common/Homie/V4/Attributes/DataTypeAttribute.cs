using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.Extensions;
using System.Text;

namespace SmartHome.Homie.V4.Attributes
{
    public class DataTypeAttribute : AttributeBase
    {
        private readonly DataType _value;

        public DataTypeAttribute(IHomieEntity parent, DataType dataType)
            : base($"{Constants.DataTypeAttributeTopicId}", parent)
        {
            _value = dataType;
        }

        public DataType Value => _value;

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(Value.GetString());
    }
}
