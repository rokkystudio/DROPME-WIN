using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using DROPME.Utils;

namespace DROPME.Server
{
    /// <summary>
    /// Обрабатывает PUT upload endpoints DROPME и группирует последовательные передачи по IP в одну дневную папку.
    /// </summary>
    internal sealed class UploadController
    {
        private sealed class UploadBatchState
        {
            public string Folder { get; set; } = string.Empty;
            public long LastActivityTimestamp { get; set; }
        }

        private static readonly TimeSpan UploadBatchReuseTimeout = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan StaleStagingAge = TimeSpan.FromDays(1);
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly object uploadBatchesMutex_ = new object();
        private readonly Dictionary<string, UploadBatchState> uploadBatches_ =
            new Dictionary<string, UploadBatchState>(StringComparer.Ordinal);

        /// <summary>
        /// Обрабатывает PUT /dropme/upload и /wifidrop/upload и формирует JSON-ответ.
        /// </summary>
        public bool HandleRequest(HttpRequest request, HttpResponse response)
        {
            if (!string.Equals(request.Method, "PUT", StringComparison.Ordinal) ||
                (request.Path != "/dropme/upload" && request.Path != "/wifidrop/upload"))
            {
                return false;
            }

            if (!request.Headers.TryGetValue("content-length", out string? contentLengthText) ||
                !long.TryParse(contentLengthText, out long contentLength) ||
                contentLength < 0)
            {
                SetJson(response, 411, "Length Required", new
                {
                    ok = false,
                    error = "Content-Length is required"
                });
                return true;
            }
            if (request.BodyFilePath.Length == 0 || !File.Exists(request.BodyFilePath))
            {
                SetJson(response, 400, "Bad Request", new
                {
                    ok = false,
                    error = "Upload body was not staged"
                });
                return true;
            }
            if (contentLength != request.BodyLength)
            {
                SetJson(response, 400, "Bad Request", new
                {
                    ok = false,
                    error = "Request body length does not match Content-Length"
                });
                return true;
            }

            if (!request.Headers.TryGetValue("x-dropme-sha256", out string? expectedSha256) ||
                !IsSha256Hex(expectedSha256))
            {
                SetJson(response, 400, "Bad Request", new
                {
                    ok = false,
                    error = "X-DROPME-SHA256 is required"
                });
                return true;
            }

            string receivedSha256 = request.BodySha256;
            if (receivedSha256.Length == 0 ||
                !string.Equals(receivedSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                SetJson(response, 422, "Unprocessable Content", new
                {
                    ok = false,
                    error = "Request body SHA-256 mismatch"
                });
                return true;
            }

            Dictionary<string, string> query = ParseQuery(request.Query);
            if (!query.TryGetValue("name", out string? encodedName) || encodedName.Length == 0)
            {
                SetJson(response, 400, "Bad Request", new
                {
                    ok = false,
                    error = "Missing file name"
                });
                return true;
            }

            string decodedName = FileName.UrlDecode(encodedName);
            FileName.ValidationResult validation = FileName.ValidateUploadFileName(decodedName);
            if (!validation.Ok)
            {
                SetJson(response, 400, "Bad Request", new
                {
                    ok = false,
                    error = validation.Error
                });
                return true;
            }

            string stagingPath = string.Empty;
            try
            {
                string folder = ResolveIncomingFolder(request);
                CleanupStaleStagingFiles(folder);
                string filePath = FileName.MakeUniquePath(folder, validation.SanitizedName);
                stagingPath = Path.Combine(folder, ".dropme-" + Guid.NewGuid().ToString("N") + ".tmp");
                if (TryAdoptRequestBody(request, stagingPath))
                {
                    using FileStream adopted = new FileStream(
                        stagingPath,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1024 * 1024,
                        FileOptions.SequentialScan);
                    adopted.Flush(true);
                }
                else
                {
                    using FileStream input = new FileStream(
                        request.BodyFilePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        1024 * 1024,
                        FileOptions.SequentialScan);
                    using FileStream output = new FileStream(
                        stagingPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        1024 * 1024,
                        FileOptions.SequentialScan);
                    input.CopyTo(output, 1024 * 1024);
                    output.Flush(true);
                }

                FileInfo stagingFile = new FileInfo(stagingPath);
                if (stagingFile.Length != contentLength)
                {
                    throw new IOException(
                        "Stored upload length mismatch: expected=" + contentLength +
                        " actual=" + stagingFile.Length);
                }
                string storedSha256 = ComputeSha256Hex(stagingPath);
                if (!string.Equals(receivedSha256, storedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Stored upload SHA-256 mismatch");
                }

                File.Move(stagingPath, filePath);
                stagingPath = string.Empty;

                Log.Info("Upload saved and verified: " + filePath);
                response.Headers["X-DROPME-Length"] = contentLength.ToString();
                response.Headers["X-DROPME-SHA256"] = storedSha256;
                SetJson(response, 200, "OK", new
                {
                    ok = true,
                    savedPath = filePath
                });
                return true;
            }
            catch (Exception exception)
            {
                if (stagingPath.Length > 0)
                {
                    try
                    {
                        File.Delete(stagingPath);
                    }
                    catch
                    {
                    }
                }

                Log.Error("Upload handling failed: " + exception.Message);
                SetJson(response, 500, "Internal Server Error", new
                {
                    ok = false,
                    error = "Failed to save file"
                });
                return true;
            }
        }

        private static bool IsSha256Hex(string value)
        {
            if (value.Length != 64)
            {
                return false;
            }
            foreach (char character in value)
            {
                if (!Uri.IsHexDigit(character))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryAdoptRequestBody(HttpRequest request, string stagingPath)
        {
            if (request.BodyFilePath.Length == 0)
            {
                return false;
            }

            string sourceRoot = Path.GetPathRoot(Path.GetFullPath(request.BodyFilePath)) ?? string.Empty;
            string destinationRoot = Path.GetPathRoot(Path.GetFullPath(stagingPath)) ?? string.Empty;
            if (!string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                File.Move(request.BodyFilePath, stagingPath);
                request.BodyFilePath = string.Empty;
                return true;
            }
            catch (IOException)
            {
                if (File.Exists(stagingPath))
                {
                    throw;
                }
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static void CleanupStaleStagingFiles(string folder)
        {
            DateTime cutoff = DateTime.UtcNow - StaleStagingAge;
            try
            {
                foreach (string path in Directory.EnumerateFiles(folder, ".dropme-*.tmp"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) < cutoff)
                        {
                            File.Delete(path);
                        }
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string ComputeSha256Hex(string filePath)
        {
            using FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using SHA256 sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
        }

        /// <summary>
        /// Возвращает активную папку batch для remote IP либо создаёт новую и удаляет устаревшие batch-записи.
        /// Время повторного использования измеряется монотонным Stopwatch.
        /// </summary>
        private string ResolveIncomingFolder(HttpRequest request)
        {
            long now = Stopwatch.GetTimestamp();
            long timeoutTicks = (long)(UploadBatchReuseTimeout.TotalSeconds * Stopwatch.Frequency);
            string batchKey = string.IsNullOrEmpty(request.RemoteIp) ? "<unknown>" : request.RemoteIp;

            lock (uploadBatchesMutex_)
            {
                List<string> expired = new List<string>();
                foreach (KeyValuePair<string, UploadBatchState> pair in uploadBatches_)
                {
                    if (now - pair.Value.LastActivityTimestamp > timeoutTicks)
                    {
                        expired.Add(pair.Key);
                    }
                }

                foreach (string key in expired)
                {
                    uploadBatches_.Remove(key);
                }

                if (uploadBatches_.TryGetValue(batchKey, out UploadBatchState? state))
                {
                    state.LastActivityTimestamp = now;
                    return state.Folder;
                }

                string folder = DesktopFolders.EnsureIncomingFolderForTime(DateTime.Now);
                uploadBatches_[batchKey] = new UploadBatchState
                {
                    Folder = folder,
                    LastActivityTimestamp = now
                };

                Log.Info("Upload batch started for " + batchKey + " in folder " + folder);
                return folder;
            }
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            int offset = 0;
            while (offset <= query.Length)
            {
                int separator = query.IndexOf('&', offset);
                string part = separator < 0
                    ? query.Substring(offset)
                    : query.Substring(offset, separator - offset);

                int equals = part.IndexOf('=');
                if (equals >= 0)
                {
                    string key = part.Substring(0, equals);
                    if (!result.ContainsKey(key))
                    {
                        result.Add(key, part.Substring(equals + 1));
                    }
                }
                else if (part.Length > 0 && !result.ContainsKey(part))
                {
                    result.Add(part, string.Empty);
                }

                if (separator < 0)
                {
                    break;
                }

                offset = separator + 1;
            }

            return result;
        }

        /// <summary>
        /// Сериализует объект в компактный UTF-8 JSON без принудительного ASCII-escaping.
        /// </summary>
        internal static byte[] SerializeJson(object value)
        {
            return JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        }

        /// <summary>
        /// Сериализует объект в UTF-8 JSON и заполняет HTTP-ответ.
        /// </summary>
        internal static void SetJson(HttpResponse response, int statusCode, string reasonPhrase, object value)
        {
            response.StatusCode = statusCode;
            response.ReasonPhrase = reasonPhrase;
            response.Headers["Content-Type"] = "application/json; charset=utf-8";
            response.Body = SerializeJson(value);
        }
    }
}
