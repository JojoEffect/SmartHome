using System;
using System.Text;

namespace SmartHome.Text
{
    public class StringUtils
    {
        public static string Join(string separator, string[] values)
        {
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            separator ??= string.Empty;

            if (values.Length == 0)
            {
                return string.Empty;
            }

            // Estimate capacity to reduce reallocations
            int estimated = 0;
            for (int i = 0; i < values.Length; i++)
            {
                string? v = values[i];
                if (v != null)
                {
                    estimated += v.Length;
                }
            }
            estimated += separator.Length * Math.Max(0, values.Length - 1);

            var sb = estimated > 0 ? new StringBuilder(estimated) : new StringBuilder();

            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0 && separator.Length > 0)
                {
                    sb.Append(separator);
                }

                string? v = values[i];
                if (v != null)
                {
                    sb.Append(v);
                }
            }

            return sb.ToString();
        }
    }
}
