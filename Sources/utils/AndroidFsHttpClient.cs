using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using DROPME.Services;

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

        internal sealed class FileDigest
        {
            public long Length { get; set; }
            public string Sha256 { get; set; } = string.Empty;
            public string Etag { get; set; } = string.Empty;
        }

        private sealed class ResponseData
        {
            public HttpStatusCode StatusCode { get; set; }
            public byte[] Body { get; set; } = Array.Empty<byte>();
            public ContentRangeHeaderValue? ContentRange { get; set; }
            public string Etag { get; set; } = string.Empty;
            public string Sha256 { get; set; } = string.Empty;
        }

        private readonly string host_;
        private readonly int port_;

        public AndroidFsHttpClient(string host, int port)
        {
            host_ = host;
            port_ = port;
        }

        /// <summary>
        /// Загружает метаданные узла через /.dropmefs/meta.
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
            return ParseMetadataBody(Encoding.UTF8.GetString(response.Body), metadata, out errorMessage);
        }

        /// <summary>
        /// Загружает подтверждённые длину и SHA-256 файла через /.dropmefs/digest.
        /// </summary>
        public bool GetFileDigest(string path, out FileDigest digest, out string errorMessage)
        {
            digest = new FileDigest();
            if (!SendRequest(HttpMethod.Get, BuildDigestPath(path), Array.Empty<KeyValuePair<string, string>>(), out ResponseData response, out errorMessage))
            {
                return false;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                errorMessage = "Digest request failed with HTTP " + (int)response.StatusCode;
                return false;
            }

            return ParseDigestBody(Encoding.UTF8.GetString(response.Body), digest, out errorMessage);
        }

        /// <summary>
        /// Загружает список каталога через /.dropmefs/list.
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

            return ParseListBody(Encoding.UTF8.GetString(response.Body), entries, out errorMessage);
        }

        /// <summary>
        /// Читает диапазон файла и трактует HTTP 416 как чтение за концом файла.
        /// </summary>
        public bool ReadFileRange(string path, ulong offset, uint size, string expectedEtag, out byte[] bytes, out string errorMessage)
        {
            bytes = Array.Empty<byte>();
            errorMessage = string.Empty;
            if (size == 0)
            {
                return true;
            }

            ulong rangeLength = size;
            if (offset > ulong.MaxValue - (rangeLength - 1UL))
            {
                errorMessage = "Read range overflows UInt64";
                return false;
            }
            ulong endInclusive = offset + rangeLength - 1UL;
            if (offset > long.MaxValue || endInclusive > long.MaxValue)
            {
                errorMessage = "Read range exceeds supported Int64 file offsets";
                return false;
            }
            List<KeyValuePair<string, string>> headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Range", "bytes=" + offset + "-" + endInclusive),
                new KeyValuePair<string, string>("X-DROPME-Verify", "sha256")
            };
            if (!string.IsNullOrEmpty(expectedEtag))
            {
                headers.Add(new KeyValuePair<string, string>("If-Match", expectedEtag));
            }

            if (!SendRequest(HttpMethod.Get, BuildFilePath(path), headers, out ResponseData response, out errorMessage))
            {
                return false;
            }

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                return true;
            }
            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                errorMessage = "Remote file changed while it was open";
                return false;
            }

            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                errorMessage = "Range read expected HTTP 206 but received " + (int)response.StatusCode;
                return false;
            }
            if (response.Body.LongLength != size)
            {
                errorMessage =
                    "Range read length mismatch: expected=" + size +
                    " actual=" + response.Body.LongLength;
                return false;
            }
            if (response.ContentRange == null ||
                response.ContentRange.From != (long)offset ||
                response.ContentRange.To != (long)endInclusive)
            {
                errorMessage =
                    "Range read Content-Range mismatch: expected=" + offset + "-" + endInclusive;
                return false;
            }
            if (!string.IsNullOrEmpty(expectedEtag) &&
                !string.Equals(response.Etag, expectedEtag, StringComparison.Ordinal))
            {
                errorMessage = "Range read ETag mismatch";
                return false;
            }
            if (string.IsNullOrEmpty(response.Sha256))
            {
                errorMessage = "Range read SHA-256 was not provided";
                return false;
            }
            string actualSha256 = ComputeSha256Hex(response.Body);
            if (!string.Equals(response.Sha256, actualSha256, StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "Range read SHA-256 mismatch";
                return false;
            }

            bytes = response.Body;
            return true;
        }

        /// <summary>
        /// Загружает локальный файл через PUT и подтверждает длину и SHA-256,
        /// рассчитанные Android после повторного чтения сохранённого файла.
        /// </summary>
        public bool UploadFile(
            string path,
            string localFilePath,
            string expectedEtag,
            bool requireMissing,
            out string errorMessage)
        {
            errorMessage = string.Empty;
            FileInfo file = new FileInfo(localFilePath);
            if (!file.Exists)
            {
                errorMessage = "Could not stat temporary file for upload";
                return false;
            }

            string expectedSha256 = ComputeSha256Hex(localFilePath);
            try
            {
                using HttpClient client = CreateHttpClient();
                client.Timeout = TimeSpan.FromHours(2);
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Put, BuildAbsoluteUri(BuildFilePath(path)));
                request.Headers.ExpectContinue = false;
                request.Headers.TryAddWithoutValidation("X-DROPME-SHA256", expectedSha256);
                if (requireMissing)
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", "*");
                }
                else
                {
                    if (string.IsNullOrEmpty(expectedEtag))
                    {
                        errorMessage = "Upload requires a known ETag for optimistic concurrency";
                        return false;
                    }
                    request.Headers.TryAddWithoutValidation("If-Match", expectedEtag);
                }

                using FileStream stream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using StreamContent content = new StreamContent(stream, 64 * 1024);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Headers.ContentLength = file.Length;
                request.Content = content;

                using HttpResponseMessage response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                int statusCode = (int)response.StatusCode;
                if (statusCode != 200 && statusCode != 201 && statusCode != 204)
                {
                    if (!requireMissing &&
                        response.StatusCode == HttpStatusCode.PreconditionFailed &&
                        ConfirmRemoteFile(path, file.Length, expectedSha256, out _))
                    {
                        return true;
                    }
                    errorMessage = "Upload request failed with HTTP " + statusCode;
                    return false;
                }

                bool responseVerified =
                    TryGetSingleHeader(response, "X-DROPME-Length", out string storedLengthValue) &&
                    long.TryParse(storedLengthValue, NumberStyles.None, CultureInfo.InvariantCulture, out long storedLength) &&
                    storedLength == file.Length &&
                    TryGetSingleHeader(response, "X-DROPME-SHA256", out string storedSha256) &&
                    string.Equals(storedSha256.Trim(), expectedSha256, StringComparison.OrdinalIgnoreCase);
                if (responseVerified)
                {
                    return true;
                }

                if (ConfirmRemoteFile(path, file.Length, expectedSha256, out string verificationError))
                {
                    return true;
                }

                errorMessage = "Upload verification failed: " + verificationError;
                return false;
            }
            catch (Exception exception)
            {
                if (!requireMissing && ConfirmRemoteFile(path, file.Length, expectedSha256, out _))
                {
                    return true;
                }
                errorMessage = "Upload request failed: " + exception.Message;
                return false;
            }
        }

        private bool ConfirmRemoteFile(
            string path,
            long expectedLength,
            string expectedSha256,
            out string errorMessage)
        {
            if (!GetFileDigest(path, out FileDigest digest, out errorMessage))
            {
                return false;
            }
            if (digest.Length != expectedLength)
            {
                errorMessage =
                    "Remote digest length mismatch: expected=" + expectedLength +
                    " actual=" + digest.Length;
                return false;
            }
            if (!string.Equals(digest.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "Remote digest SHA-256 mismatch";
                return false;
            }
            return true;
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
                response.StatusCode != HttpStatusCode.NoContent &&
                response.StatusCode != HttpStatusCode.NotFound)
            {
                errorMessage = "Delete request failed with HTTP " + (int)response.StatusCode;
                return false;
            }

            if (!GetMetadata(path, out NodeMetadata metadata, out errorMessage))
            {
                return false;
            }
            if (metadata.Exists)
            {
                errorMessage = "Delete verification failed: remote path still exists";
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

            if (!GetMetadata(path, out NodeMetadata metadata, out errorMessage))
            {
                return false;
            }
            if (!metadata.Exists || !metadata.Directory)
            {
                errorMessage = "Create directory verification failed";
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

            if (!GetMetadata(destinationPath, out NodeMetadata destinationMetadata, out errorMessage))
            {
                return false;
            }
            if (!destinationMetadata.Exists)
            {
                errorMessage = "Move verification failed: destination does not exist";
                return false;
            }

            if (!string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
            {
                if (!GetMetadata(sourcePath, out NodeMetadata sourceMetadata, out errorMessage))
                {
                    return false;
                }
                if (sourceMetadata.Exists)
                {
                    errorMessage = "Move verification failed: source still exists";
                    return false;
                }
            }

            return true;
        }

        private static string ComputeSha256Hex(string filePath)
        {
            using FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using SHA256 sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
        }

        private static string ComputeSha256Hex(byte[] bytes)
        {
            using SHA256 sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(bytes)).ToLowerInvariant();
        }

        private static bool TryGetSingleHeader(HttpResponseMessage response, string name, out string value)
        {
            value = string.Empty;
            if (!response.Headers.TryGetValues(name, out IEnumerable<string>? values))
            {
                return false;
            }

            using IEnumerator<string> enumerator = values.GetEnumerator();
            if (!enumerator.MoveNext())
            {
                return false;
            }
            value = enumerator.Current;
            return !string.IsNullOrWhiteSpace(value);
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
                response.ContentRange = httpResponse.Content.Headers.ContentRange;
                response.Etag = httpResponse.Headers.ETag?.ToString() ?? string.Empty;
                response.Sha256 = TryGetSingleHeader(httpResponse, "X-DROPME-SHA256", out string sha256)
                    ? sha256.Trim().ToLowerInvariant()
                    : string.Empty;
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

            HttpClient client = new HttpClient(handler, true);
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("DROPME-WinFsp", "1.0"));
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-DROPME-Device-ID",
                AppSettings.DeviceId);
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-DROPME-Device-Name",
                AppSettings.DeviceName);
            return client;
        }
        private Uri BuildAbsoluteUri(string pathAndQuery)
        {
            return new Uri("http://" + host_ + ":" + port_.ToString(CultureInfo.InvariantCulture) + pathAndQuery, UriKind.Absolute);
        }

        private static string BuildMetaPath(string path)
        {
            return "/.dropmefs/meta?path=" + UrlEncode(path);
        }

        private static string BuildListPath(string path)
        {
            return "/.dropmefs/list?path=" + UrlEncode(path);
        }

        private static string BuildDigestPath(string path)
        {
            return "/.dropmefs/digest?path=" + UrlEncode(path);
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

        private static bool ParseMetadataBody(
            string body,
            NodeMetadata metadata,
            out string errorMessage)
        {
            errorMessage = string.Empty;
            bool ok = false;
            bool hasDirectory = false;
            bool hasSize = false;
            bool hasLastModified = false;
            bool hasWritable = false;
            bool hasName = false;
            bool hasEtag = false;

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
                    case "ok":
                        ok = value == "1";
                        break;
                    case "directory":
                        hasDirectory = TryParseBoolStrict(value, out bool directory);
                        if (hasDirectory)
                        {
                            metadata.Directory = directory;
                        }
                        break;
                    case "size":
                        hasSize = TryParseUnsignedStrict(value, out ulong size);
                        if (hasSize)
                        {
                            metadata.Size = size;
                        }
                        break;
                    case "lastModified":
                        hasLastModified = TryParseUnsignedStrict(value, out ulong lastModified);
                        if (hasLastModified)
                        {
                            metadata.LastModified = lastModified;
                        }
                        break;
                    case "writable":
                        hasWritable = TryParseBoolStrict(value, out bool writable);
                        if (hasWritable)
                        {
                            metadata.Writable = writable;
                        }
                        break;
                    case "name":
                        metadata.Name = UrlDecode(value);
                        hasName = metadata.Name.Length > 0;
                        break;
                    case "etag":
                        metadata.Etag = UrlDecode(value);
                        hasEtag = metadata.Etag.Length > 0;
                        break;
                }
            }

            if (!ok || !hasDirectory || !hasSize || !hasLastModified ||
                !hasWritable || !hasName || !hasEtag)
            {
                errorMessage = "Metadata response is incomplete or invalid";
                return false;
            }
            return true;
        }

        private static bool ParseDigestBody(string body, FileDigest digest, out string errorMessage)
        {
            errorMessage = string.Empty;
            bool ok = false;
            bool hasLength = false;
            bool hasSha256 = false;

            using StringReader reader = new StringReader(body);
            for (string? line = reader.ReadLine(); line != null; line = reader.ReadLine())
            {
                int separator = line.IndexOf('=');
                if (separator < 0)
                {
                    continue;
                }

                string key = line.Substring(0, separator);
                string value = line.Substring(separator + 1);
                switch (key)
                {
                    case "ok":
                        ok = value == "1";
                        break;
                    case "length":
                        hasLength = long.TryParse(
                            value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out long parsedLength) && parsedLength >= 0;
                        if (hasLength)
                        {
                            digest.Length = parsedLength;
                        }
                        break;
                    case "sha256":
                        hasSha256 = value.Length == 64;
                        if (hasSha256)
                        {
                            foreach (char character in value)
                            {
                                if (!Uri.IsHexDigit(character))
                                {
                                    hasSha256 = false;
                                    break;
                                }
                            }
                        }
                        if (hasSha256)
                        {
                            digest.Sha256 = value.ToLowerInvariant();
                        }
                        break;
                    case "etag":
                        digest.Etag = UrlDecode(value);
                        break;
                }
            }

            if (!ok || !hasLength || !hasSha256)
            {
                errorMessage = "Digest response is incomplete or invalid";
                return false;
            }
            return true;
        }

        private static bool ParseListBody(
            string body,
            List<DirectoryEntry> entries,
            out string errorMessage)
        {
            entries.Clear();
            errorMessage = string.Empty;
            bool ok = false;

            using StringReader reader = new StringReader(body);
            for (string? line = reader.ReadLine(); line != null; line = reader.ReadLine())
            {
                if (line.Length == 0)
                {
                    continue;
                }
                if (line == "ok=1")
                {
                    ok = true;
                    continue;
                }
                if (!line.StartsWith("entry=", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] fields = line.Substring(6).Split('\t');
                if (fields.Length != 5 ||
                    !TryParseBoolStrict(fields[1], out bool directory) ||
                    !TryParseUnsignedStrict(fields[2], out ulong size) ||
                    !TryParseUnsignedStrict(fields[3], out ulong lastModified) ||
                    !TryParseBoolStrict(fields[4], out bool writable))
                {
                    entries.Clear();
                    errorMessage = "Directory list contains an invalid entry";
                    return false;
                }

                string name = UrlDecode(fields[0]);
                if (name.Length == 0)
                {
                    entries.Clear();
                    errorMessage = "Directory list contains an empty name";
                    return false;
                }

                entries.Add(new DirectoryEntry
                {
                    Name = name,
                    Directory = directory,
                    Size = size,
                    LastModified = lastModified,
                    Writable = writable
                });
            }

            if (!ok)
            {
                entries.Clear();
                errorMessage = "Directory list response is incomplete or invalid";
                return false;
            }
            return true;
        }

        private static bool TryParseBoolStrict(string value, out bool result)
        {
            if (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            {
                result = true;
                return true;
            }
            if (value == "0" || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                result = false;
                return true;
            }
            result = false;
            return false;
        }

        private static bool TryParseUnsignedStrict(string value, out ulong result)
        {
            return ulong.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out result);
        }
    }
}
