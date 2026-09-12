using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DROPME.Utils
{
    /// <summary>
    /// Выполняет декодирование и валидацию имён файлов, приходящих по HTTP upload.
    /// </summary>
    internal static class FileName
    {
        internal sealed class ValidationResult
        {
            public bool Ok { get; set; }
            public string SanitizedName { get; set; } = string.Empty;
            public string Error { get; set; } = string.Empty;
        }

        /// <summary>
        /// Декодирует percent-escaped UTF-8 строку и преобразует '+' в пробел.
        /// </summary>
        public static string UrlDecode(string value)
        {
            using MemoryStream stream = new MemoryStream();
            for (int index = 0; index < value.Length; ++index)
            {
                if (value[index] == '%' &&
                    index + 2 < value.Length &&
                    byte.TryParse(value.Substring(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte symbol))
                {
                    stream.WriteByte(symbol);
                    index += 2;
                    continue;
                }

                if (value[index] == '+')
                {
                    stream.WriteByte((byte)' ');
                    continue;
                }

                byte[] bytes = Encoding.UTF8.GetBytes(value[index].ToString());
                stream.Write(bytes, 0, bytes.Length);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>
        /// Проверяет имя на path traversal и удаляет недопустимые для Windows символы по правилам исходного C++ кода.
        /// </summary>
        public static ValidationResult ValidateUploadFileName(string decodedName)
        {
            if (decodedName.Length == 0)
            {
                return new ValidationResult { Error = "Empty file name" };
            }

            if (decodedName.IndexOf('/') >= 0 ||
                decodedName.IndexOf('\\') >= 0 ||
                decodedName.IndexOf(':') >= 0 ||
                decodedName == "." ||
                decodedName == "..")
            {
                return new ValidationResult { Error = "Path traversal is not allowed" };
            }

            StringBuilder result = new StringBuilder(decodedName.Length);
            foreach (char symbol in decodedName)
            {
                if (symbol < 32)
                {
                    continue;
                }

                if (symbol == '<' || symbol == '>' || symbol == ':' || symbol == '"' ||
                    symbol == '/' || symbol == '\\' || symbol == '|' || symbol == '?' || symbol == '*')
                {
                    continue;
                }

                result.Append(symbol);
            }

            string sanitized = result.ToString().TrimStart(' ').TrimEnd(' ', '.');
            if (sanitized.Length == 0)
            {
                return new ValidationResult { Error = "File name is empty after sanitization" };
            }

            if (!string.Equals(Path.GetFileName(sanitized), sanitized, StringComparison.Ordinal))
            {
                return new ValidationResult { Error = "Path traversal is not allowed" };
            }

            return new ValidationResult { Ok = true, SanitizedName = sanitized };
        }

        /// <summary>
        /// Подбирает путь без перезаписи существующего файла по правилу "name (n).ext", начиная с n=1.
        /// </summary>
        public static string MakeUniquePath(string directory, string sanitizedFileName)
        {
            string candidate = Path.Combine(directory, sanitizedFileName);
            string stem = Path.GetFileNameWithoutExtension(sanitizedFileName);
            string extension = Path.GetExtension(sanitizedFileName);

            for (int index = 1; File.Exists(candidate) || Directory.Exists(candidate); ++index)
            {
                candidate = Path.Combine(directory, stem + " (" + index + ")" + extension);
            }

            return candidate;
        }
    }
}
