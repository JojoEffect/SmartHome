using HomieNano.Version4.Enums;

namespace HomieNano.Version4.Extensions
{
    public static class  DataTypeExtensions
    {
        public static string GetString(this DataType dataType)
        {
            return dataType switch
            {
                DataType.String => "string",
                DataType.Integer => "integer",
                DataType.Float => "float",
                DataType.Boolean => "boolean",
                DataType.Enum => "enum",
                DataType.Color => "color",
                _ => "string",
            };
        }

    }
}
