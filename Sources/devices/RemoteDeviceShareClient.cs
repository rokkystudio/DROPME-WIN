using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DROPME.Services;

namespace DROPME.Devices
{
    /// <summary>
    /// Выполняет короткую Share-сессию Windows -> DROPME device: разрешение, передача файлов, завершение.
    /// </summary>
    internal sealed class RemoteDeviceShareClient
    {
        public void Send(
            RemoteDevice device,
            IReadOnlyList<string> filePaths,
            Action? authorizationGranted = null)
        {
            if (!device.ShareAvailable)
            {
                throw new InvalidOperationException("Remote device does not accept Share transfers");
            }
            if (filePaths.Count == 0)
            {
                return;
            }

            Authorize(device, Path.GetFileName(filePaths[0]));
            authorizationGranted?.Invoke();
            foreach (string filePath in filePaths)
            {
                SendFile(device, filePath);
            }
        }

        private static void Authorize(RemoteDevice device, string itemName)
        {
            using HttpClient client = CreateClient(TimeSpan.FromSeconds(65));
            string url =
                "http://" + device.Host + ":" +
                device.ControlPort.ToString(CultureInfo.InvariantCulture) +
                "/dropme/authorize?purpose=share&name=" +
                Uri.EscapeDataString(itemName);

            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new ByteArrayContent(Array.Empty<byte>());
            using HttpResponseMessage response = client.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    response.StatusCode == System.Net.HttpStatusCode.Forbidden
                        ? LocalizationService.Text("device_permission_denied")
                        : "Remote device denied Share: HTTP " + (int)response.StatusCode);
            }
        }

        private static void SendFile(RemoteDevice device, string filePath)
        {
            FileInfo file = new FileInfo(filePath);
            if (!file.Exists)
            {
                throw new FileNotFoundException("File not found", filePath);
            }

            string sha256 = ComputeSha256(filePath);
            string url =
                "http://" + device.Host + ":" +
                device.ControlPort.ToString(CultureInfo.InvariantCulture) +
                "/dropme/upload?name=" +
                Uri.EscapeDataString(file.Name);

            using HttpClient client = CreateClient(TimeSpan.FromHours(2));
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Put, url);
            request.Headers.ExpectContinue = false;
            request.Headers.TryAddWithoutValidation("X-DROPME-SHA256", sha256);

            using FileStream input = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                1024 * 1024,
                FileOptions.SequentialScan);
            using StreamContent content = new StreamContent(input, 64 * 1024);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = file.Length;
            request.Content = content;

            using HttpResponseMessage response =
                client.Send(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    "Share upload failed with HTTP " + (int)response.StatusCode);
            }

            if (!TryHeader(response, "X-DROPME-Length", out string storedLengthText) ||
                !long.TryParse(
                    storedLengthText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long storedLength) ||
                storedLength != file.Length ||
                !TryHeader(response, "X-DROPME-SHA256", out string storedSha256) ||
                !string.Equals(storedSha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Remote device did not verify the transferred file");
            }
        }

        private static HttpClient CreateClient(TimeSpan timeout)
        {
            HttpClientHandler handler = new HttpClientHandler
            {
                UseProxy = false
            };
            HttpClient client = new HttpClient(handler, true)
            {
                Timeout = timeout
            };
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("DROPME-Windows", "1.0"));
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-DROPME-Device-ID",
                AppSettings.DeviceId);
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-DROPME-Device-Name",
                AppSettings.DeviceName);
            return client;
        }

        private static bool TryHeader(
            HttpResponseMessage response,
            string name,
            out string value)
        {
            value = string.Empty;
            if (!response.Headers.TryGetValues(name, out IEnumerable<string>? values))
            {
                return false;
            }

            foreach (string item in values)
            {
                value = item.Trim();
                return value.Length > 0;
            }
            return false;
        }

        private static string ComputeSha256(string filePath)
        {
            using FileStream input = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                1024 * 1024,
                FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        }
    }
}
