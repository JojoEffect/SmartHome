using SmartHome.Homie.V4.Enums;

namespace SmartHome.Homie.V4.Extensions
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
