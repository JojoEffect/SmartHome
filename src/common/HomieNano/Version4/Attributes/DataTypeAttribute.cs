using HomieNano.Version4.Enums;
using HomieNano.Version4.Extensions;
using System.Text;

namespace HomieNano.Version4.Attributes
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
