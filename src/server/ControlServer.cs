using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using DROPME.Clients;
using DROPME.Utils;

namespace DROPME.Server
{
    internal enum ControlRequestDisposition
    {
        NotHandled,
        ResponseReady,
        ResponseSentDirectly
    }

    /// <summary>
    /// Обрабатывает control endpoints DROPME и управляет жизненным циклом Android-сессий.
    /// Поля connect JSON извлекаются независимо, как в исходном C++ обработчике.
    /// </summary>
    internal sealed class ControlServer
    {
        private const int MaxComputerNameLength = 15;

        private readonly ClientManager clientManager_;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetComputerName(StringBuilder buffer, ref uint size);

        public ControlServer(ClientManager clientManager)
        {
            clientManager_ = clientManager;
        }

        /// <summary>
        /// Маршрутизирует control-запрос и либо формирует обычный ответ, либо удерживает session-соединение.
        /// </summary>
        public ControlRequestDisposition HandleRequest(HttpRequest request, TcpClient clientSocket, HttpResponse response)
        {
            if (request.Method == "GET" && MatchesPath(request.Path, "/info"))
            {
                HandleInfoRequest(response);
                return ControlRequestDisposition.ResponseReady;
            }

            if (request.Method == "POST" && MatchesPath(request.Path, "/client/connect"))
            {
                HandleConnectRequest(request, response);
                return ControlRequestDisposition.ResponseReady;
            }

            if (request.Method == "GET" &&
                (request.Path.StartsWith("/dropme/client/session/", StringComparison.Ordinal) ||
                 request.Path.StartsWith("/wifidrop/client/session/", StringComparison.Ordinal)))
            {
                return HandleSessionRequest(request, clientSocket);
            }

            return ControlRequestDisposition.NotHandled;
        }

        private static bool MatchesPath(string path, string suffix)
        {
            return path == "/dropme" + suffix || path == "/wifidrop" + suffix;
        }

        /// <summary>
        /// Формирует GET /info с параметрами текущего сервера.
        /// </summary>
        private static void HandleInfoRequest(HttpResponse response)
        {
            Log.Info("HTTP discovery request received");
            UploadController.SetJson(response, 200, "OK", new
            {
                app = "DROPME",
                role = "windows-server",
                protocolVersion = Protocol.ProtocolVersion,
                deviceName = GetComputerNameUtf8(),
                tcpPort = Protocol.TcpPort,
                udpPort = Protocol.DiscoveryPort
            });
        }

        /// <summary>
        /// Принимает параметры Android-клиента, выполняет WebDAV mount и регистрирует pending session.
        /// Отсутствующие поля получают те же значения по умолчанию, что и в исходном C++ коде.
        /// </summary>
        private void HandleConnectRequest(HttpRequest request, HttpResponse response)
        {
            string body = Utf.Utf8ToString(request.Body);

            int protocolVersion = ExtractJsonInt(body, "protocolVersion", -1);
            if (protocolVersion != Protocol.ProtocolVersion)
            {
                UploadController.SetJson(response, 400, "Bad Request", new
                {
                    accepted = false,
                    error = "Unsupported protocolVersion"
                });
                return;
            }

            AndroidClient client = new AndroidClient
            {
                ClientId = GenerateClientId(),
                DeviceName = ExtractJsonString(body, "deviceName"),
                DeviceNumber = ExtractJsonString(body, "deviceNumber"),
                WebDavHost = ExtractJsonString(body, "webDavHost"),
                WebDavPort = ExtractJsonInt(body, "webDavPort", 0),
                WebDavBasePath = ExtractJsonString(body, "webDavBasePath"),
                ReadOnly = ExtractJsonBool(body, "readOnly", false),
                MountReady = ExtractJsonBool(body, "mountReady", false),
                RemoteIp = request.RemoteIp,
                SessionState = AndroidSessionState.Pending
            };

            bool hasWebDavEndpoint = client.WebDavHost.Length > 0 && client.WebDavPort > 0;
            if (!hasWebDavEndpoint)
            {
                client.MountReady = false;
            }
            else if (!client.MountReady)
            {
                client.MountReady = true;
            }

            client.DriveName = AndroidClient.BuildDriveName(client.DeviceName, client.DeviceNumber);

            if (client.MountReady)
            {
                WebDavDriveMapper.MountResult mountResult = WebDavDriveMapper.Mount(client);
                if (mountResult.DriveLetter != null)
                {
                    client.DriveLetter = mountResult.DriveLetter;
                    client.MountError = string.Empty;
                }
                else
                {
                    client.DriveLetter = string.Empty;
                    if (client.MountError.Length == 0)
                    {
                        client.MountError = mountResult.ErrorMessage;
                    }
                }
            }
            else
            {
                client.DriveLetter = string.Empty;
                if (client.MountError.Length == 0)
                {
                    client.MountError = string.Empty;
                }
            }

            clientManager_.AddClient(client);
            if (client.DriveLetter.Length > 0)
            {
                OpenMountedDriveInExplorer(client);
            }

            Log.Info(
                "Android client connected: " + client.ClientId + " (" + client.RemoteIp + "), webDav=" +
                client.WebDavHost + ":" + client.WebDavPort +
                ", mountReady=" + (client.MountReady ? "true" : "false") +
                ", driveLetter=" + (client.DriveLetter.Length == 0 ? "<none>" : client.DriveLetter) +
                ", mountError=" + (client.MountError.Length == 0 ? "<none>" : client.MountError));

            UploadController.SetJson(response, 200, "OK", new
            {
                accepted = true,
                clientId = client.ClientId,
                driveLetter = client.DriveLetter,
                driveName = client.DriveName,
                mountReady = client.MountReady,
                mountError = client.MountError
            });
        }

        /// <summary>
        /// Удерживает persistent session, отправляет heartbeat каждые две секунды и завершает её после шести секунд без успешной отправки.
        /// Для измерения timeout используется монотонный Stopwatch.
        /// </summary>
        private ControlRequestDisposition HandleSessionRequest(HttpRequest request, TcpClient clientSocket)
        {
            string clientId = ExtractSessionClientId(request.Path);
            if (clientId.Length == 0)
            {
                return ControlRequestDisposition.NotHandled;
            }

            NetworkStream stream = clientSocket.GetStream();
            if (!clientManager_.MarkSessionStarted(clientId))
            {
                byte[] body = UploadController.SerializeJson(new
                {
                    accepted = false,
                    error = "Unknown clientId"
                });

                string headers =
                    "HTTP/1.1 404 Not Found\r\n" +
                    "Content-Type: application/json; charset=utf-8\r\n" +
                    "Content-Length: " + body.Length + "\r\n" +
                    "Connection: close\r\n\r\n";

                SendAll(stream, Encoding.ASCII.GetBytes(headers));
                SendAll(stream, body);
                clientSocket.Close();
                return ControlRequestDisposition.ResponseSentDirectly;
            }

            Log.Info("Android session started: " + clientId);

            byte[] responseHeaders = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Cache-Control: no-cache\r\n" +
                "Connection: keep-alive\r\n\r\n");

            if (!SendAll(stream, responseHeaders))
            {
                RemoveAndUnmount(clientId);
                clientSocket.Close();
                return ControlRequestDisposition.ResponseSentDirectly;
            }

            long lastSuccessfulSend = Stopwatch.GetTimestamp();
            long disconnectTimeoutTicks = (long)(Protocol.ControlDisconnectTimeout.TotalSeconds * Stopwatch.Frequency);

            while (true)
            {
                byte[] heartbeatBody = UploadController.SerializeJson(new
                {
                    heartbeat = true,
                    clientId = clientId
                });

                byte[] heartbeat = new byte[heartbeatBody.Length + 1];
                Buffer.BlockCopy(heartbeatBody, 0, heartbeat, 0, heartbeatBody.Length);
                heartbeat[heartbeat.Length - 1] = (byte)'\n';

                if (!SendAll(stream, heartbeat))
                {
                    if (Stopwatch.GetTimestamp() - lastSuccessfulSend >= disconnectTimeoutTicks)
                    {
                        break;
                    }
                }
                else
                {
                    lastSuccessfulSend = Stopwatch.GetTimestamp();
                    clientManager_.TouchClient(clientId);
                }

                Thread.Sleep(Protocol.ControlHeartbeatInterval);
                if (!clientManager_.Contains(clientId))
                {
                    break;
                }
            }

            RemoveAndUnmount(clientId);
            Log.Info("Android session closed: " + clientId);
            try
            {
                clientSocket.Client.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
            }
            clientSocket.Close();
            return ControlRequestDisposition.ResponseSentDirectly;
        }

        private void RemoveAndUnmount(string clientId)
        {
            AndroidClient? removed = clientManager_.RemoveClient(clientId);
            if (removed != null)
            {
                WebDavDriveMapper.Unmount(removed);
            }
        }

        private static string ExtractSessionClientId(string path)
        {
            const string sessionSuffix = "/client/session/";

            if (path.StartsWith("/dropme", StringComparison.Ordinal))
            {
                int offset = "/dropme".Length + sessionSuffix.Length;
                return path.Length > offset ? path.Substring(offset) : string.Empty;
            }

            if (path.StartsWith("/wifidrop", StringComparison.Ordinal))
            {
                int offset = "/wifidrop".Length + sessionSuffix.Length;
                return path.Length > offset ? path.Substring(offset) : string.Empty;
            }

            return string.Empty;
        }

        private static string GenerateClientId()
        {
            const string digits = "0123456789abcdef";
            int[] groups = { 8, 4, 4, 4, 12 };
            StringBuilder value = new StringBuilder(36);
            byte[] random = new byte[36];
            RandomNumberGenerator.Fill(random);
            int randomIndex = 0;

            for (int groupIndex = 0; groupIndex < groups.Length; ++groupIndex)
            {
                if (groupIndex > 0)
                {
                    value.Append('-');
                }

                for (int index = 0; index < groups[groupIndex]; ++index)
                {
                    value.Append(digits[random[randomIndex++] & 0x0F]);
                }
            }

            return value.ToString();
        }

        private static void OpenMountedDriveInExplorer(AndroidClient client)
        {
            if (client.DriveLetter.Length == 0)
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = client.DriveLetter + ":\\",
                    UseShellExecute = true
                });
                Log.Info("Mounted drive auto-opened in Explorer: " + client.DriveLetter);
            }
            catch (Exception exception)
            {
                Log.Warn("Could not auto-open mounted drive " + client.DriveLetter + " in Explorer. " + exception.Message);
            }
        }

        private static bool SendAll(NetworkStream stream, byte[] bytes)
        {
            try
            {
                int offset = 0;
                while (offset < bytes.Length)
                {
                    stream.Write(bytes, offset, bytes.Length - offset);
                    offset = bytes.Length;
                }
                stream.Flush();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string GetComputerNameUtf8()
        {
            StringBuilder computerName = new StringBuilder(MaxComputerNameLength + 1);
            uint size = (uint)computerName.Capacity;
            return GetComputerName(computerName, ref size) ? computerName.ToString() : "Windows PC";
        }

        private static string ExtractJsonString(string json, string fieldName)
        {
            string pattern = "\"" + Regex.Escape(fieldName) + "\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"";
            Match match = Regex.Match(json, pattern, RegexOptions.CultureInvariant);
            return match.Success && match.Groups.Count >= 2
                ? Utf.JsonUnescape(match.Groups[1].Value)
                : string.Empty;
        }

        private static int ExtractJsonInt(string json, string fieldName, int defaultValue)
        {
            string pattern = "\"" + Regex.Escape(fieldName) + "\"\\s*:\\s*([0-9]+)";
            Match match = Regex.Match(json, pattern, RegexOptions.CultureInvariant);
            return match.Success && match.Groups.Count >= 2
                ? int.Parse(match.Groups[1].Value)
                : defaultValue;
        }

        private static bool ExtractJsonBool(string json, string fieldName, bool defaultValue)
        {
            string pattern = "\"" + Regex.Escape(fieldName) + "\"\\s*:\\s*(true|false)";
            Match match = Regex.Match(json, pattern, RegexOptions.CultureInvariant);
            return match.Success && match.Groups.Count >= 2
                ? match.Groups[1].Value == "true"
                : defaultValue;
        }
    }
}
