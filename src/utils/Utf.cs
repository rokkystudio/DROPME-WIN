using System;
using System.Globalization;
using System.Text;

namespace DROPME.Utils
{
    /// <summary>
    /// Выполняет преобразования UTF-8 и декодирование escape-последовательностей JSON.
    /// </summary>
    internal static class Utf
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// Декодирует UTF-8 bytes с ошибкой при недопустимой последовательности.
        /// </summary>
        public static string Utf8ToString(byte[] value)
        {
            return StrictUtf8.GetString(value);
        }

        /// <summary>
        /// Кодирует строку в UTF-8 без BOM.
        /// </summary>
        public static byte[] StringToUtf8(string value)
        {
            return StrictUtf8.GetBytes(value);
        }

        /// <summary>
        /// Декодирует JSON escape-последовательности так же, как Utf::JsonUnescape исходного C++ приложения.
        /// </summary>
        public static string JsonUnescape(string value)
        {
            StringBuilder result = new StringBuilder(value.Length);

            for (int index = 0; index < value.Length; ++index)
            {
                if (value[index] != '\\' || index + 1 >= value.Length)
                {
                    result.Append(value[index]);
                    continue;
                }

                char escape = value[++index];
                switch (escape)
                {
                    case '"':
                        result.Append('"');
                        break;
                    case '\\':
                        result.Append('\\');
                        break;
                    case '/':
                        result.Append('/');
                        break;
                    case 'b':
                        result.Append('\b');
                        break;
                    case 'f':
                        result.Append('\f');
                        break;
                    case 'n':
                        result.Append('\n');
                        break;
                    case 'r':
                        result.Append('\r');
                        break;
                    case 't':
                        result.Append('\t');
                        break;
                    case 'u':
                        if (index + 4 >= value.Length)
                        {
                            throw new FormatException("Invalid JSON unicode escape");
                        }

                        string hexValue = value.Substring(index + 1, 4);
                        ushort codeUnit = ushort.Parse(hexValue, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        result.Append((char)codeUnit);
                        index += 4;
                        break;
                    default:
                        result.Append(escape);
                        break;
                }
            }

            return result.ToString();
        }
    }
}
