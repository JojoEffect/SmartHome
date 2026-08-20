using System.Text;

namespace SmartHome.Homie.V4.Attributes
{
    public class ExtensionsAttribute : StringArrayAttributeBase
    {
        public ExtensionsAttribute(IHomieEntity parent, string[] extensions)
            : base($"{Constants.ExtensionAttributeTopicId}", parent, extensions)
        {
        }

        public override byte[] GetPayload()
        {
            return Encoding.UTF8.GetBytes(Join(",", Value));
        }

        private static string Join(string separator, string[] values)
        {
            if (values == null || values.Length == 0)
                return string.Empty;
            var sb = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                    sb.Append(separator);
                sb.Append(values[i]);
            }
            return sb.ToString();
        }
    }
}
