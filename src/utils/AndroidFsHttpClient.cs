using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace DROPME.Utils
{
    /// <summary>
    /// Выполняет HTTP-запросы к файловому API Android-клиента.
    /// </summary>
    internal sealed class AndroidFsHttpClient
    {
        internal sealed class NodeMetadata
        {
            public bool Exists { get; set; }
            public bool Directory { get; set; }
            public ulong Size { get; set; }
            public ulong LastModified { get; set; }
            public bool Writable { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Etag { get; set; } = string.Empty;
        }

        internal sealed class DirectoryEntry
        {
            public string Name { get; set; } = string.Empty;
            public bool Directory { get; set; }
            public ulong Size { get; set; }
            public ulong LastModified { get; set; }
            public bool Writable { get; set; }
        }

        private sealed class ResponseData
        {
            public HttpStatusCode StatusCode { get; set; }
            public byte[] Body { get; set; } = Array.Empty<byte>();
        }

        private readonly string host_;
        private readonly int port_;

        public AndroidFsHttpClient(string host, int port)
        {
            host_ = host;
            port_ = port;
        }

        /// <summary>
        /// Загружает метаданные узла через /.wifidropfs/meta.
        /// HTTP 404 преобразуется в корректный результат Exists=false.
        /// </summary>
        public bool GetMetadata(string path, out NodeMetadata metadata, out string errorMessage)
        {
            metadata = new NodeMetadata();
            if (!SendRequest(HttpMethod.Get, BuildMetaPath(path), Array.Empty<KeyValuePair<string, string>>(), out ResponseData response, out errorMessage))
            {
                return false;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return true;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                errorMessage = "Metadata request failed with HTTP " + (int)response.StatusCode;
                return false;
            }

            metadata.Exists = true;
            return ParseMetadataBody(Encoding.UTF8.GetString(response.Body), metadata);
        }

        /// <summary>
        /// Загружает список каталога через /.wifidropfs/list.
        /// </summary>
        public bool ListDirectory(string path, out List<DirectoryEntry> entries, out string errorMessage)
        {
            entries = new List<DirectoryEntry>();
            if (!SendRequest(HttpMethod.Get, BuildListPath(path), Array.Empty<KeyValuePair<string, string>>(), out ResponseData response, out errorMessage))
            {
                return false;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                errorMessage = "Directory list request failed with HTTP " + (int)response.StatusCode;
                return false;
            }

            return ParseListBody(Encoding.UTF8.GetString(response.Body), entries);
        }

        /// <summary>
        /// Читает диапазон файла и трактует HTTP 416 как чтение за концом файла.
        /// </summary>
        public bool ReadFileRange(string path, ulong offset, uint size, out byte[] bytes, out string errorMessage)
        {
            bytes = Array.Empty<byte>();
            errorMessage = string.Empty;
            if (size == 0)
            {
                return true;
            }

            ulong endInclusive = offset + size - 1;
            KeyValuePair<string, string>[] headers =
            {
                new KeyValuePair<string, string>("Range", "bytes=" + offset + "-" + endInclusive)
            };

            if (!SendRequest(HttpMethod.Get, BuildFilePath(path), headers, out ResponseData response, out errorMessage))
            {
                return false;
            }

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                return true;
            }

            if (response.StatusCode != HttpStatusCode.OK &&
                response.StatusCode != HttpStatusCode.PartialContent)
            {
                errorMessage = "Read request failed with HTTP " + (int)response.StatusCode;
                return false;
            }

            bytes = response.Body;
            return true;
        }

        /// <summary>
        /// Загружает локальный файл через PUT и принимает HTTP 200, 201 или 204.
        /// Размер запроса ограничен uint.MaxValue в соответствии с WinHTTP-реализацией исходного приложения.
        /// </summary>
        public bool UploadFile(string path, string localFilePath, out string errorMessage)
        {
            errorMessage = string.Empty;
            FileInfo file = new FileInfo(localFilePath);
            if (!file.Exists)
            {
                errorMessage = "Could not stat temporary file for upload";
                return false;
            }

            if ((ulong)file.Length > uint.MaxValue)
            {
                errorMessage = "Temporary file is larger than the supported upload limit";
                return false;
            }

            try
            {
                using HttpClient client = CreateHttpClient();
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Put, BuildAbsoluteUri(BuildFilePath(path)));
                using FileStream stream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using StreamContent content = new StreamContent(stream, 64 * 1024);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Content = content;

                using HttpResponseMessage response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                int statusCode = (int)response.StatusCode;
                if (statusCode != 200 && statusCode != 201 && statusCode != 204)
                {
                    errorMessage = "Upload request failed with HTTP " + statusCode;
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                errorMessage = "Upload request failed: " + exception.Message;
                return false;
            }
        }

        /// <summary>
        /// Удаляет файл или каталог через DELETE и принимает HTTP 200 или 204.
        /// </summary>
        public bool DeletePath(string path, out string errorMessage)
        {
            if (!SendRequest(HttpMethod.Delete, BuildFilePath(path), Array.Empty<KeyValuePair<string, string>>(), out ResponseData response, out errorMessage))
            {
                return false;
            }

            if (response.StatusCode != HttpStatusCode.OK &&
                response.StatusCode != HttpStatusCode.NoContent)
            {
                errorMessage = "Delete request failed with HTTP " + (int)response.StatusCode;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Создаёт каталог на удалённой файловой системе через MKCOL и принимает HTTP 201.
        /// </summary>
        public bool CreateRemoteDirectory(string path, out string errorMessage)
        {
            if (!SendRequest(new HttpMethod("MKCOL"), BuildFilePath(path), Array.Empty<KeyValuePair<string, string>>(), out ResponseData response, out errorMessage))
            {
                return false;
            }

            if (response.StatusCode != HttpStatusCode.Created)
            {
                errorMessage = "Create directory request failed with HTTP " + (int)response.StatusCode;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Перемещает путь через MOVE с заголовками Destination и Overwrite.
        /// </summary>
        public bool MovePath(string sourcePath, string destinationPath, bool overwrite, out string errorMessage)
        {
            KeyValuePair<string, string>[] headers =
            {
                new KeyValuePair<string, string>("Destination", BuildDestinationHeaderValue(destinationPath)),
                new KeyValuePair<string, string>("Overwrite", overwrite ? "T" : "F")
            };

            if (!SendRequest(new HttpMethod("MOVE"), BuildFilePath(sourcePath), headers, out ResponseData response, out errorMessage))
            {
                return false;
            }

            if (response.StatusCode != HttpStatusCode.Created &&
                response.StatusCode != HttpStatusCode.NoContent)
            {
                errorMessage = "Move request failed with HTTP " + (int)response.StatusCode;
                return false;
            }

            return true;
        }

        private bool SendRequest(
            HttpMethod method,
            string pathAndQuery,
            IReadOnlyCollection<KeyValuePair<string, string>> headers,
            out ResponseData response,
            out string errorMessage)
        {
            response = new ResponseData();
            errorMessage = string.Empty;

            try
            {
                using HttpClient client = CreateHttpClient();
                using HttpRequestMessage request = new HttpRequestMessage(method, BuildAbsoluteUri(pathAndQuery));
                foreach (KeyValuePair<string, string> header in headers)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                using HttpResponseMessage httpResponse =
                    client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();

                response.StatusCode = httpResponse.StatusCode;
                response.Body = httpResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                return true;
            }
            catch (Exception exception)
            {
                errorMessage = "HTTP request failed: " + exception.Message;
                return false;
            }
        }

        private static HttpClient CreateHttpClient()
        {
            HttpClientHandler handler = new HttpClientHandler
            {
                UseProxy = false
            };

            return new HttpClient(handler, true)
            {
                DefaultRequestHeaders =
                {
                    UserAgent =
                    {
                        new ProductInfoHeaderValue("DROPME-WinFsp", "1.0")
                    }
                }
            };
        }

        private Uri BuildAbsoluteUri(string pathAndQuery)
        {
            return new Uri("http://" + host_ + ":" + port_.ToString(CultureInfo.InvariantCulture) + pathAndQuery, UriKind.Absolute);
        }

        private static string BuildMetaPath(string path)
        {
            return "/.wifidropfs/meta?path=" + UrlEncode(path);
        }

        private static string BuildListPath(string path)
        {
            return "/.wifidropfs/list?path=" + UrlEncode(path);
        }

        private static string BuildFilePath(string path)
        {
            return UrlEncodePath(path);
        }

        private string BuildDestinationHeaderValue(string path)
        {
            return "http://" + host_ + ":" + port_.ToString(CultureInfo.InvariantCulture) + BuildFilePath(path);
        }

        private static string UrlEncode(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            StringBuilder encoded = new StringBuilder(bytes.Length);

            foreach (byte symbol in bytes)
            {
                bool isUnreserved =
                    (symbol >= (byte)'a' && symbol <= (byte)'z') ||
                    (symbol >= (byte)'A' && symbol <= (byte)'Z') ||
                    (symbol >= (byte)'0' && symbol <= (byte)'9') ||
                    symbol == (byte)'-' || symbol == (byte)'_' || symbol == (byte)'.' || symbol == (byte)'~';

                if (isUnreserved)
                {
                    encoded.Append((char)symbol);
                }
                else
                {
                    encoded.Append('%');
                    encoded.Append(symbol.ToString("X2", CultureInfo.InvariantCulture));
                }
            }

            return encoded.ToString();
        }

        private static string UrlEncodePath(string value)
        {
            if (string.IsNullOrEmpty(value) || value == "/")
            {
                return "/";
            }

            string normalized = value[0] == '/' ? value.Substring(1) : value;
            string[] segments = normalized.Split('/');
            StringBuilder encoded = new StringBuilder("/");
            for (int index = 0; index < segments.Length; ++index)
            {
                if (segments[index].Length > 0)
                {
                    encoded.Append(UrlEncode(segments[index]));
                }

                if (index + 1 < segments.Length)
                {
                    encoded.Append('/');
                }
            }

            return encoded.ToString();
        }

        private static string UrlDecode(string value)
        {
            using MemoryStream bytes = new MemoryStream();
            for (int index = 0; index < value.Length; ++index)
            {
                if (value[index] == '%' &&
                    index + 2 < value.Length &&
                    byte.TryParse(value.Substring(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte decoded))
                {
                    bytes.WriteByte(decoded);
                    index += 2;
                }
                else if (value[index] == '+')
                {
                    bytes.WriteByte((byte)' ');
                }
                else
                {
                    byte[] raw = Encoding.UTF8.GetBytes(value[index].ToString());
                    bytes.Write(raw, 0, raw.Length);
                }
            }

            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static bool ParseMetadataBody(string body, NodeMetadata metadata)
        {
            using StringReader reader = new StringReader(body);
            for (string? line = reader.ReadLine(); line != null; line = reader.ReadLine())
            {
                if (line.Length == 0)
                {
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator < 0)
                {
                    continue;
                }

                string key = line.Substring(0, separator);
                string value = line.Substring(separator + 1);
                switch (key)
                {
                    case "directory":
                        metadata.Directory = ParseBool(value);
                        break;
                    case "size":
                        metadata.Size = ParseUnsigned(value);
                        break;
                    case "lastModified":
                        metadata.LastModified = ParseUnsigned(value);
                        break;
                    case "writable":
                        metadata.Writable = ParseBool(value);
                        break;
                    case "name":
                        metadata.Name = UrlDecode(value);
                        break;
                    case "etag":
                        metadata.Etag = UrlDecode(value);
                        break;
                }
            }

            return true;
        }

        private static bool ParseListBody(string body, List<DirectoryEntry> entries)
        {
            entries.Clear();
            using StringReader reader = new StringReader(body);
            for (string? line = reader.ReadLine(); line != null; line = reader.ReadLine())
            {
                if (!line.StartsWith("entry=", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] fields = line.Substring(6).Split('\t');
                if (fields.Length < 5)
                {
                    continue;
                }

                entries.Add(new DirectoryEntry
                {
                    Name = UrlDecode(fields[0]),
                    Directory = ParseBool(fields[1]),
                    Size = ParseUnsigned(fields[2]),
                    LastModified = ParseUnsigned(fields[3]),
                    Writable = ParseBool(fields[4])
                });
            }

            return true;
        }

        private static bool ParseBool(string value)
        {
            return value == "1" || value == "true";
        }

        private static ulong ParseUnsigned(string value)
        {
            return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong result) ? result : 0;
        }
    }
}
