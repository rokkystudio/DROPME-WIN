using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

            if (!request.Headers.ContainsKey("content-length"))
            {
                Log.Warn("Upload request without Content-Length");
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

            try
            {
                string folder = ResolveIncomingFolder(request);
                string filePath = FileName.MakeUniquePath(folder, validation.SanitizedName);

                using FileStream output = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                output.Write(request.Body, 0, request.Body.Length);
                output.Flush();

                Log.Info("Upload saved: " + filePath);
                SetJson(response, 200, "OK", new
                {
                    ok = true,
                    savedPath = filePath
                });
                return true;
            }
            catch (Exception exception)
            {
                Log.Error("Upload handling failed: " + exception.Message);
                SetJson(response, 500, "Internal Server Error", new
                {
                    ok = false,
                    error = "Failed to save file"
                });
                return true;
            }
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
