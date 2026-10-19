using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DROPME.Services;
using DROPME.Utils;

namespace DROPME.Server
{
    /// <summary>
    /// Публикует выбранные Windows-хранилища через общий виртуальный корень для разрешённых DROPME file-сессий.
    /// Набор доступных корней перечитывается на каждый запрос, поэтому изменение доступа применяется без перезапуска сервера.
    /// </summary>
    internal sealed class FileAccessController
    {
        private const string MetaPath = "/.dropmefs/meta";
        private const string ListPath = "/.dropmefs/list";
        private const string DigestPath = "/.dropmefs/digest";
        private const string PreviewPath = "/.dropmefs/preview";
        private const string HeaderSha256 = "x-dropme-sha256";
        private const string HeaderVerify = "x-dropme-verify";

        private readonly DeviceAuthorizationService authorizationService_;

        public FileAccessController(DeviceAuthorizationService authorizationService)
        {
            authorizationService_ = authorizationService;
        }

        /// <summary>
        /// Обрабатывает метаданные, listing и файловые операции разрешённой DROPME file-сессии.
        /// </summary>
        public bool HandleRequest(HttpRequest request, HttpResponse response)
        {
            bool isFileApi =
                request.Path == MetaPath ||
                request.Path == ListPath ||
                request.Path == DigestPath ||
                request.Path == PreviewPath ||
                request.Method == "GET" ||
                request.Method == "PUT" ||
                request.Method == "DELETE" ||
                request.Method == "MKCOL" ||
                request.Method == "MOVE";
            if (!isFileApi)
            {
                return false;
            }

            if (!authorizationService_.Authorize(request, "files", string.Empty, false))
            {
                UploadController.SetJson(response, 403, "Forbidden", new
                {
                    ok = false,
                    error = "Device is not authorized for file access"
                });
                return true;
            }

            if (request.Method == "GET" && request.Path == MetaPath)
            {
                HandleMetadata(request, response);
                return true;
            }
            if (request.Method == "GET" && request.Path == ListPath)
            {
                HandleList(request, response);
                return true;
            }
            if (request.Method == "GET" && request.Path == DigestPath)
            {
                HandleDigest(request, response);
                return true;
            }
            if (request.Method == "GET" && request.Path == PreviewPath)
            {
                HandlePreview(request, response);
                return true;
            }

            switch (request.Method)
            {
                case "GET":
                    HandleRead(request, response);
                    return true;
                case "PUT":
                    HandlePut(request, response);
                    return true;
                case "DELETE":
                    HandleDelete(request, response);
                    return true;
                case "MKCOL":
                    HandleCreateDirectory(request, response);
                    return true;
                case "MOVE":
                    HandleMove(request, response);
                    return true;
                default:
                    return false;
            }
        }

        private void HandleMetadata(HttpRequest request, HttpResponse response)
        {
            string remotePath = QueryPath(request);
            if (IsVirtualRoot(remotePath))
            {
                StringBuilder virtualRoot = new StringBuilder();
                virtualRoot.Append("ok=1\n");
                virtualRoot.Append("directory=1\n");
                virtualRoot.Append("size=0\n");
                virtualRoot.Append("lastModified=0\n");
                virtualRoot.Append("writable=0\n");
                virtualRoot.Append("name=%2F\n");
                virtualRoot.Append("etag=%220-0%22\n");
                SetText(response, 200, "OK", virtualRoot.ToString());
                return;
            }

            string? localPath = ResolvePath(remotePath);
            if (localPath == null || (!File.Exists(localPath) && !Directory.Exists(localPath)))
            {
                SetText(response, 404, "Not Found", "ok=0\nerror=not_found\n");
                return;
            }

            bool directory = Directory.Exists(localPath);
            FileSystemInfo info = directory
                ? new DirectoryInfo(localPath)
                : new FileInfo(localPath);
            long size = directory ? 0L : ((FileInfo)info).Length;
            long modified = ToUnixMilliseconds(info.LastWriteTimeUtc);
            string name = IsStorageRootPath(localPath) ? RemoteRootName(localPath) : info.Name;
            string etag = BuildEtag(modified, size);

            StringBuilder body = new StringBuilder();
            body.Append("ok=1\n");
            body.Append("directory=").Append(directory ? '1' : '0').Append((char)10);
            body.Append("size=").Append(size).Append((char)10);
            body.Append("lastModified=").Append(modified).Append((char)10);
            body.Append("writable=").Append(IsWritable(info) ? '1' : '0').Append((char)10);
            body.Append("name=").Append(Uri.EscapeDataString(name)).Append((char)10);
            body.Append("etag=").Append(Uri.EscapeDataString(etag)).Append((char)10);
            SetText(response, 200, "OK", body.ToString());
        }

        private void HandleList(HttpRequest request, HttpResponse response)
        {
            string remotePath = QueryPath(request);
            StringBuilder body = new StringBuilder("ok=1\n");
            if (IsVirtualRoot(remotePath))
            {
                foreach (WindowsStorageService.StorageRoot root in WindowsStorageService.GetPublishedRoots())
                {
                    AppendVirtualRootEntry(body, root);
                }
                SetText(response, 200, "OK", body.ToString());
                return;
            }

            string? localPath = ResolvePath(remotePath);
            if (localPath == null || !Directory.Exists(localPath))
            {
                SetText(response, 404, "Not Found", "ok=0\nerror=not_found\n");
                return;
            }

            try
            {
                foreach (string directory in Directory.EnumerateDirectories(localPath))
                {
                    AppendEntry(body, directory, true);
                }
                foreach (string file in Directory.EnumerateFiles(localPath))
                {
                    AppendEntry(body, file, false);
                }
            }
            catch (UnauthorizedAccessException)
            {
                SetText(response, 403, "Forbidden", "ok=0\nerror=access_denied\n");
                return;
            }
            catch (IOException)
            {
                SetText(response, 500, "Internal Server Error", "ok=0\nerror=io\n");
                return;
            }

            SetText(response, 200, "OK", body.ToString());
        }

        private void HandleDigest(HttpRequest request, HttpResponse response)
        {
            string? localPath = ResolvePath(QueryPath(request));
            if (localPath == null || !File.Exists(localPath))
            {
                SetText(response, 404, "Not Found", "ok=0\nerror=not_found\n");
                return;
            }

            try
            {
                FileInfo before = new FileInfo(localPath);
                long length = before.Length;
                long modified = ToUnixMilliseconds(before.LastWriteTimeUtc);
                string etag = BuildEtag(modified, length);
                string sha256 = ComputeSha256(localPath);

                FileInfo after = new FileInfo(localPath);
                if (after.Length != length ||
                    ToUnixMilliseconds(after.LastWriteTimeUtc) != modified)
                {
                    SetText(response, 409, "Conflict", "ok=0\nerror=changed_during_hash\n");
                    return;
                }

                StringBuilder body = new StringBuilder();
                body.Append("ok=1\n");
                body.Append("length=").Append(length).Append((char)10);
                body.Append("sha256=").Append(sha256).Append((char)10);
                body.Append("etag=").Append(Uri.EscapeDataString(etag)).Append((char)10);
                SetText(response, 200, "OK", body.ToString());
            }
            catch (UnauthorizedAccessException)
            {
                SetText(response, 403, "Forbidden", "ok=0\nerror=access_denied\n");
            }
            catch (IOException)
            {
                SetText(response, 500, "Internal Server Error", "ok=0\nerror=io\n");
            }
        }

        /// <summary>
        /// Возвращает системное preview изображения или видео без передачи исходного файла.
        /// </summary>
        private void HandlePreview(HttpRequest request, HttpResponse response)
        {
            Dictionary<string, string> query = ParseQuery(request.Query);
            string remotePath = query.TryGetValue("path", out string? value)
                ? Uri.UnescapeDataString(value)
                : string.Empty;
            int size = 256;
            if (query.TryGetValue("size", out string? rawSize) &&
                int.TryParse(rawSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedSize))
            {
                size = Math.Clamp(parsedSize, 64, 512);
            }

            string? localPath = ResolvePath(remotePath);
            if (localPath == null || !File.Exists(localPath))
            {
                SetText(response, 404, "Not Found", string.Empty);
                return;
            }

            byte[]? preview = ShellThumbnailService.TryCreateJpeg(localPath, size);
            if (preview == null || preview.Length == 0)
            {
                SetText(response, 415, "Unsupported Media Type", string.Empty);
                return;
            }

            FileInfo source = new FileInfo(localPath);
            response.StatusCode = 200;
            response.ReasonPhrase = "OK";
            response.Headers["Content-Type"] = "image/jpeg";
            response.Headers["X-DROPME-Source-Modified"] =
                ToUnixMilliseconds(source.LastWriteTimeUtc).ToString(CultureInfo.InvariantCulture);
            response.Body = preview;
        }

        private void HandleRead(HttpRequest request, HttpResponse response)
        {
            string? localPath = ResolvePath(Uri.UnescapeDataString(request.Path));
            if (localPath == null || !File.Exists(localPath))
            {
                SetText(response, 404, "Not Found", string.Empty);
                return;
            }

            FileInfo file = new FileInfo(localPath);
            string etag = BuildEtag(
                ToUnixMilliseconds(file.LastWriteTimeUtc),
                file.Length);
            if (request.Headers.TryGetValue("if-match", out string? expectedEtag) &&
                !string.Equals(expectedEtag, etag, StringComparison.Ordinal))
            {
                SetText(response, 412, "Precondition Failed", string.Empty);
                return;
            }

            if (request.Headers.TryGetValue("range", out string? rangeHeader))
            {
                HandleRangeRead(request, response, localPath, file.Length, etag, rangeHeader);
                return;
            }

            response.StatusCode = 200;
            response.ReasonPhrase = "OK";
            response.Headers["Content-Type"] = "application/octet-stream";
            response.Headers["ETag"] = etag;
            response.BodyFilePath = localPath;
            response.BodyFileLength = file.Length;
        }

        private void HandleRangeRead(
            HttpRequest request,
            HttpResponse response,
            string localPath,
            long fileLength,
            string etag,
            string rangeHeader)
        {
            if (!TryParseRange(rangeHeader, fileLength, out long start, out long end))
            {
                response.StatusCode = 416;
                response.ReasonPhrase = "Range Not Satisfiable";
                response.Headers["Content-Range"] = "bytes */" + fileLength.ToString(CultureInfo.InvariantCulture);
                return;
            }

            int length = checked((int)(end - start + 1));
            byte[] bytes = new byte[length];
            using (FileStream input = new FileStream(
                localPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                64 * 1024,
                FileOptions.RandomAccess))
            {
                input.Position = start;
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = input.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0)
                    {
                        SetText(response, 409, "Conflict", string.Empty);
                        return;
                    }
                    offset += read;
                }
            }

            response.StatusCode = 206;
            response.ReasonPhrase = "Partial Content";
            response.Headers["Content-Type"] = "application/octet-stream";
            response.Headers["Content-Range"] =
                "bytes " + start + "-" + end + "/" + fileLength;
            response.Headers["ETag"] = etag;
            if (request.Headers.TryGetValue(HeaderVerify, out string? verify) &&
                string.Equals(verify, "sha256", StringComparison.OrdinalIgnoreCase))
            {
                response.Headers["X-DROPME-SHA256"] = ComputeSha256(bytes);
            }
            response.Body = bytes;
        }

        private void HandlePut(HttpRequest request, HttpResponse response)
        {
            string? localPath = ResolvePath(Uri.UnescapeDataString(request.Path));
            if (localPath == null || IsStorageRootPath(localPath))
            {
                SetText(response, 400, "Bad Request", string.Empty);
                return;
            }

            string? parent = Path.GetDirectoryName(localPath);
            if (parent == null || !Directory.Exists(parent))
            {
                SetText(response, 409, "Conflict", string.Empty);
                return;
            }
            if (request.BodyFilePath.Length == 0 || !File.Exists(request.BodyFilePath))
            {
                SetText(response, 400, "Bad Request", string.Empty);
                return;
            }

            bool existed = File.Exists(localPath);
            if (Directory.Exists(localPath))
            {
                SetText(response, 409, "Conflict", string.Empty);
                return;
            }

            if (request.Headers.TryGetValue("if-none-match", out string? ifNoneMatch) &&
                ifNoneMatch == "*" &&
                existed)
            {
                SetText(response, 412, "Precondition Failed", string.Empty);
                return;
            }

            if (request.Headers.TryGetValue("if-match", out string? ifMatch))
            {
                if (!existed)
                {
                    SetText(response, 412, "Precondition Failed", string.Empty);
                    return;
                }

                FileInfo existingInfo = new FileInfo(localPath);
                string currentEtag = BuildEtag(
                    ToUnixMilliseconds(existingInfo.LastWriteTimeUtc),
                    existingInfo.Length);
                if (!string.Equals(ifMatch, currentEtag, StringComparison.Ordinal))
                {
                    SetText(response, 412, "Precondition Failed", string.Empty);
                    return;
                }
            }

            if (request.Headers.TryGetValue(HeaderSha256, out string? expectedSha256) &&
                !string.Equals(
                    expectedSha256.Trim(),
                    request.BodySha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                SetText(response, 409, "Conflict", string.Empty);
                return;
            }

            string stagingPath = Path.Combine(
                parent,
                ".dropme-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(request.BodyFilePath, stagingPath, false);
                using (FileStream durable = new FileStream(
                    stagingPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None))
                {
                    durable.Flush(true);
                }

                File.Move(stagingPath, localPath, true);

                FileInfo stored = new FileInfo(localPath);
                string storedSha256 = ComputeSha256(localPath);
                response.StatusCode = existed ? 204 : 201;
                response.ReasonPhrase = existed ? "No Content" : "Created";
                response.Headers["X-DROPME-Length"] =
                    stored.Length.ToString(CultureInfo.InvariantCulture);
                response.Headers["X-DROPME-SHA256"] = storedSha256;
            }
            catch (UnauthorizedAccessException)
            {
                TryDelete(stagingPath);
                SetText(response, 403, "Forbidden", string.Empty);
            }
            catch (IOException)
            {
                TryDelete(stagingPath);
                SetText(response, 500, "Internal Server Error", string.Empty);
            }
        }

        private void HandleDelete(HttpRequest request, HttpResponse response)
        {
            string? localPath = ResolvePath(Uri.UnescapeDataString(request.Path));
            if (localPath == null || IsStorageRootPath(localPath))
            {
                SetText(response, 400, "Bad Request", string.Empty);
                return;
            }

            try
            {
                if (File.Exists(localPath))
                {
                    File.Delete(localPath);
                }
                else if (Directory.Exists(localPath))
                {
                    Directory.Delete(localPath, true);
                }
                else
                {
                    response.StatusCode = 404;
                    response.ReasonPhrase = "Not Found";
                    return;
                }

                response.StatusCode = 204;
                response.ReasonPhrase = "No Content";
            }
            catch (UnauthorizedAccessException)
            {
                SetText(response, 403, "Forbidden", string.Empty);
            }
            catch (IOException)
            {
                SetText(response, 409, "Conflict", string.Empty);
            }
        }

        private void HandleCreateDirectory(HttpRequest request, HttpResponse response)
        {
            string? localPath = ResolvePath(Uri.UnescapeDataString(request.Path));
            if (localPath == null || IsStorageRootPath(localPath))
            {
                SetText(response, 400, "Bad Request", string.Empty);
                return;
            }

            if (File.Exists(localPath) || Directory.Exists(localPath))
            {
                SetText(response, 405, "Method Not Allowed", string.Empty);
                return;
            }

            string? parent = Path.GetDirectoryName(localPath);
            if (parent == null || !Directory.Exists(parent))
            {
                SetText(response, 409, "Conflict", string.Empty);
                return;
            }

            try
            {
                Directory.CreateDirectory(localPath);
                response.StatusCode = 201;
                response.ReasonPhrase = "Created";
            }
            catch (UnauthorizedAccessException)
            {
                SetText(response, 403, "Forbidden", string.Empty);
            }
            catch (IOException)
            {
                SetText(response, 500, "Internal Server Error", string.Empty);
            }
        }

        private void HandleMove(HttpRequest request, HttpResponse response)
        {
            string? sourcePath = ResolvePath(Uri.UnescapeDataString(request.Path));
            if (sourcePath == null ||
                IsStorageRootPath(sourcePath) ||
                (!File.Exists(sourcePath) && !Directory.Exists(sourcePath)))
            {
                SetText(response, 404, "Not Found", string.Empty);
                return;
            }
            if (!request.Headers.TryGetValue("destination", out string? destinationValue) ||
                !Uri.TryCreate(destinationValue, UriKind.Absolute, out Uri? destinationUri))
            {
                SetText(response, 400, "Bad Request", string.Empty);
                return;
            }

            string? destinationPath = ResolvePath(Uri.UnescapeDataString(destinationUri.AbsolutePath));
            if (destinationPath == null || IsStorageRootPath(destinationPath))
            {
                SetText(response, 400, "Bad Request", string.Empty);
                return;
            }

            bool overwrite =
                !request.Headers.TryGetValue("overwrite", out string? overwriteValue) ||
                !string.Equals(overwriteValue, "F", StringComparison.OrdinalIgnoreCase);
            bool destinationExists =
                File.Exists(destinationPath) || Directory.Exists(destinationPath);
            if (destinationExists && !overwrite)
            {
                SetText(response, 412, "Precondition Failed", string.Empty);
                return;
            }

            string? destinationParent = Path.GetDirectoryName(destinationPath);
            if (destinationParent == null || !Directory.Exists(destinationParent))
            {
                SetText(response, 409, "Conflict", string.Empty);
                return;
            }

            try
            {
                if (destinationExists)
                {
                    if (File.Exists(destinationPath))
                    {
                        File.Delete(destinationPath);
                    }
                    else
                    {
                        Directory.Delete(destinationPath, true);
                    }
                }

                if (File.Exists(sourcePath))
                {
                    File.Move(sourcePath, destinationPath);
                }
                else
                {
                    Directory.Move(sourcePath, destinationPath);
                }

                response.StatusCode = destinationExists ? 204 : 201;
                response.ReasonPhrase = destinationExists ? "No Content" : "Created";
            }
            catch (UnauthorizedAccessException)
            {
                SetText(response, 403, "Forbidden", string.Empty);
            }
            catch (IOException)
            {
                SetText(response, 409, "Conflict", string.Empty);
            }
        }

        private string QueryPath(HttpRequest request)
        {
            return ParseQuery(request.Query).TryGetValue("path", out string? path)
                ? Uri.UnescapeDataString(path)
                : "/";
        }

        private string? ResolvePath(string remotePath)
        {
            string[] segments = remotePath
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                return null;
            }

            WindowsStorageService.StorageRoot? root = WindowsStorageService.GetPublishedRoots()
                .FirstOrDefault(item =>
                    string.Equals(item.DisplayName, segments[0], StringComparison.OrdinalIgnoreCase));
            if (root == null)
            {
                return null;
            }

            string relative = segments.Length == 1
                ? string.Empty
                : Path.Combine(segments.Skip(1).ToArray());
            string rootPath = Path.GetFullPath(root.Path);
            string fullPath = Path.GetFullPath(Path.Combine(rootPath, relative));
            string rootPrefix =
                rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!string.Equals(fullPath, rootPath, StringComparison.OrdinalIgnoreCase) &&
                !fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return fullPath;
        }

        private static bool IsVirtualRoot(string remotePath)
        {
            return remotePath.Trim().Trim('/', '\\').Length == 0;
        }

        private static bool IsStorageRootPath(string path)
        {
            string normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            return WindowsStorageService.GetRoots().Any(root =>
                string.Equals(
                    normalized,
                    Path.GetFullPath(root.Path).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase));
        }

        private static string RemoteRootName(string path)
        {
            string normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            WindowsStorageService.StorageRoot? root = WindowsStorageService.GetRoots().FirstOrDefault(item =>
                string.Equals(
                    normalized,
                    Path.GetFullPath(item.Path).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase));
            return root?.DisplayName ?? "/";
        }

        private static void AppendVirtualRootEntry(
            StringBuilder body,
            WindowsStorageService.StorageRoot root)
        {
            long modified = 0L;
            try
            {
                modified = ToUnixMilliseconds(new DirectoryInfo(root.Path).LastWriteTimeUtc);
            }
            catch
            {
            }

            body.Append("entry=")
                .Append(Uri.EscapeDataString(root.DisplayName))
                .Append("\t1\t0\t")
                .Append(modified)
                .Append("\t1\n");
        }

        private static void AppendEntry(StringBuilder body, string path, bool directory)
        {
            FileSystemInfo info = directory
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            long size = directory ? 0L : ((FileInfo)info).Length;
            bool writable = IsWritable(info);

            body.Append("entry=")
                .Append(Uri.EscapeDataString(info.Name))
                .Append('\t')
                .Append(directory ? '1' : '0')
                .Append('\t')
                .Append(size)
                .Append('\t')
                .Append(ToUnixMilliseconds(info.LastWriteTimeUtc))
                .Append('\t')
                .Append(writable ? '1' : '0')
                .Append((char)10);
        }

        private static bool IsWritable(FileSystemInfo info)
        {
            return !info.Attributes.HasFlag(FileAttributes.ReadOnly);
        }

        private static string BuildEtag(long lastModified, long length)
        {
            return "\"" +
                   lastModified.ToString(CultureInfo.InvariantCulture) +
                   "-" +
                   length.ToString(CultureInfo.InvariantCulture) +
                   "\"";
        }

        private static long ToUnixMilliseconds(DateTime utc)
        {
            DateTime normalized = utc.Kind == DateTimeKind.Utc
                ? utc
                : utc.ToUniversalTime();
            return new DateTimeOffset(normalized).ToUnixTimeMilliseconds();
        }

        private static bool TryParseRange(
            string value,
            long fileLength,
            out long start,
            out long end)
        {
            start = 0;
            end = 0;
            if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string raw = value.Substring(6);
            int separator = raw.IndexOf('-');
            if (separator <= 0 ||
                !long.TryParse(
                    raw.Substring(0, separator),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out start) ||
                start < 0 ||
                start >= fileLength)
            {
                return false;
            }

            string endValue = raw.Substring(separator + 1);
            if (endValue.Length == 0)
            {
                end = fileLength - 1;
            }
            else if (!long.TryParse(
                         endValue,
                         NumberStyles.None,
                         CultureInfo.InvariantCulture,
                         out end))
            {
                return false;
            }

            if (end < start)
            {
                return false;
            }

            end = Math.Min(end, fileLength - 1);
            return end - start + 1 <= int.MaxValue;
        }

        private static string ComputeSha256(string path)
        {
            using FileStream input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                1024 * 1024,
                FileOptions.SequentialScan);
            return ComputeSha256(input);
        }

        private static string ComputeSha256(byte[] bytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        private static string ComputeSha256(Stream input)
        {
            using SHA256 sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(input)).ToLowerInvariant();
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            Dictionary<string, string> result =
                new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = part.IndexOf('=');
                string key = separator < 0 ? part : part.Substring(0, separator);
                string value = separator < 0 ? string.Empty : part.Substring(separator + 1);
                result[Uri.UnescapeDataString(key)] = value;
            }
            return result;
        }

        private static void SetText(
            HttpResponse response,
            int statusCode,
            string reason,
            string text)
        {
            response.StatusCode = statusCode;
            response.ReasonPhrase = reason;
            response.Headers["Content-Type"] = "text/plain; charset=utf-8";
            response.Body = Encoding.UTF8.GetBytes(text);
        }

        private static void TryDelete(string path)
        {
            if (path.Length == 0)
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
