using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using DROPME.Clients;
using DROPME.Utils;

namespace DROPME.Server
{
    /// <summary>
    /// Принимает TCP HTTP-запросы DROPME, маршрутизирует control/upload endpoints и обслуживает timeout клиентов.
    /// </summary>
    internal sealed class WifiDropServer : IDisposable
    {
        private const int MaxHeaderSize = 64 * 1024;

        private readonly ClientManager clientManager_;
        private readonly UploadController uploadController_;
        private readonly ControlServer controlServer_;
        private readonly DiscoveryResponder discoveryResponder_;
        private readonly object activeSocketsMutex_ = new object();
        private readonly HashSet<TcpClient> activeSockets_ = new HashSet<TcpClient>();

        private TcpListener? listener_;
        private Thread? acceptThread_;
        private Thread? maintenanceThread_;
        private volatile bool running_;

        public WifiDropServer(ClientManager clientManager)
        {
            clientManager_ = clientManager;
            uploadController_ = new UploadController();
            controlServer_ = new ControlServer(clientManager);
            discoveryResponder_ = new DiscoveryResponder();
        }

        public bool IsRunning => running_;

        /// <summary>
        /// Запускает TCP listener с exclusive address use, UDP discovery responder и maintenance loop.
        /// </summary>
        public bool Start()
        {
            if (running_)
            {
                return true;
            }

            try
            {
                listener_ = new TcpListener(IPAddress.Any, Protocol.TcpPort);
                listener_.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
                listener_.Start();

                running_ = true;
                discoveryResponder_.Start();

                acceptThread_ = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "DROPME TCP accept"
                };
                maintenanceThread_ = new Thread(MaintenanceLoop)
                {
                    IsBackground = true,
                    Name = "DROPME client maintenance"
                };

                acceptThread_.Start();
                maintenanceThread_.Start();

                Log.Info("TCP server listening on port " + Protocol.TcpPort);
                return true;
            }
            catch (SocketException exception)
            {
                running_ = false;
                listener_?.Stop();
                listener_ = null;
                Log.Error("Failed to start TCP server on port " + Protocol.TcpPort + ": " + exception.SocketErrorCode);
                return false;
            }
        }

        /// <summary>
        /// Останавливает discovery/TCP listener, закрывает активные подключения и размонтирует все клиентские диски.
        /// </summary>
        public void Stop()
        {
            if (!running_)
            {
                return;
            }

            running_ = false;
            discoveryResponder_.Stop();

            try
            {
                listener_?.Stop();
            }
            catch (SocketException)
            {
            }
            listener_ = null;

            CloseActiveClientSockets();

            acceptThread_?.Join();
            maintenanceThread_?.Join();
            acceptThread_ = null;
            maintenanceThread_ = null;

            foreach (AndroidClient client in clientManager_.Clear())
            {
                WebDavDriveMapper.Unmount(client);
            }
        }

        private void AcceptLoop()
        {
            while (running_)
            {
                TcpClient clientSocket;
                try
                {
                    clientSocket = listener_!.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    if (running_)
                    {
                        Log.Warn("AcceptTcpClient failed");
                    }
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                lock (activeSocketsMutex_)
                {
                    activeSockets_.Add(clientSocket);
                }

                string remoteIp = "unknown";
                if (clientSocket.Client.RemoteEndPoint is IPEndPoint remote)
                {
                    remoteIp = remote.Address.ToString();
                }

                Thread thread = new Thread(() => HandleClient(clientSocket, remoteIp))
                {
                    IsBackground = true,
                    Name = "DROPME client " + remoteIp
                };
                thread.Start();
            }
        }

        /// <summary>
        /// Разбирает один HTTP-запрос и передаёт его control или upload обработчику.
        /// </summary>
        private void HandleClient(TcpClient clientSocket, string remoteIp)
        {
            try
            {
                HttpRequest request = new HttpRequest
                {
                    RemoteIp = remoteIp
                };

                if (!ReceiveHttpRequest(clientSocket, request))
                {
                    Log.Warn("Failed to parse HTTP request");
                    clientSocket.Close();
                    return;
                }

                HttpResponse response = new HttpResponse();
                ControlRequestDisposition disposition = controlServer_.HandleRequest(request, clientSocket, response);

                if (disposition == ControlRequestDisposition.NotHandled)
                {
                    if (!uploadController_.HandleRequest(request, response))
                    {
                        response.StatusCode = 404;
                        response.ReasonPhrase = "Not Found";
                        response.Headers["Content-Type"] = "text/plain; charset=utf-8";
                        response.Body = Encoding.UTF8.GetBytes("Not Found");
                    }

                    SendResponse(clientSocket, response);
                    clientSocket.Close();
                }
                else if (disposition == ControlRequestDisposition.ResponseReady)
                {
                    SendResponse(clientSocket, response);
                    clientSocket.Close();
                }
            }
            finally
            {
                lock (activeSocketsMutex_)
                {
                    activeSockets_.Remove(clientSocket);
                }
            }
        }

        /// <summary>
        /// Удаляет клиентов, не проявлявших активность в течение control disconnect timeout.
        /// </summary>
        private void MaintenanceLoop()
        {
            while (running_)
            {
                Thread.Sleep(Protocol.ControlHeartbeatInterval);
                foreach (AndroidClient client in clientManager_.RemoveInactive(Protocol.ControlDisconnectTimeout))
                {
                    WebDavDriveMapper.Unmount(client);
                    Log.Info("Client disconnected by timeout: " + client.ClientId);
                }
            }
        }

        /// <summary>
        /// Читает request line, headers и тело запроса с лимитом заголовка 64 KiB.
        /// Разбор request line принимает произвольные whitespace-разделители и игнорирует дополнительные токены после HTTP version.
        /// </summary>
        private static bool ReceiveHttpRequest(TcpClient clientSocket, HttpRequest request)
        {
            NetworkStream stream = clientSocket.GetStream();
            using MemoryStream received = new MemoryStream();
            byte[] chunk = new byte[4096];
            int headerEnd = -1;

            while (headerEnd < 0)
            {
                int count;
                try
                {
                    count = stream.Read(chunk, 0, chunk.Length);
                }
                catch (IOException)
                {
                    return false;
                }

                if (count <= 0)
                {
                    return false;
                }

                received.Write(chunk, 0, count);
                if (received.Length > MaxHeaderSize)
                {
                    return false;
                }

                headerEnd = FindHeaderEnd(received.GetBuffer(), (int)received.Length);
            }

            byte[] all = received.ToArray();
            string headerBlock = Encoding.ASCII.GetString(all, 0, headerEnd);
            string[] lines = headerBlock.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
            {
                return false;
            }

            string[] requestLine = lines[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (requestLine.Length < 3)
            {
                return false;
            }

            request.Method = requestLine[0];
            request.Target = requestLine[1];
            request.HttpVersion = requestLine[2];

            int queryPosition = request.Target.IndexOf('?');
            if (queryPosition >= 0)
            {
                request.Path = request.Target.Substring(0, queryPosition);
                request.Query = request.Target.Substring(queryPosition + 1);
            }
            else
            {
                request.Path = request.Target;
                request.Query = string.Empty;
            }

            for (int index = 1; index < lines.Length; ++index)
            {
                int separator = lines[index].IndexOf(':');
                if (separator < 0)
                {
                    continue;
                }

                string key = lines[index].Substring(0, separator).ToLowerInvariant();
                string value = lines[index].Substring(separator + 1).TrimStart();
                if (!request.Headers.ContainsKey(key))
                {
                    request.Headers.Add(key, value);
                }
            }

            int bodyOffset = headerEnd + 4;
            using MemoryStream body = new MemoryStream();
            if (all.Length > bodyOffset)
            {
                body.Write(all, bodyOffset, all.Length - bodyOffset);
            }

            if (request.Headers.TryGetValue("content-length", out string? contentLengthText))
            {
                if (!ulong.TryParse(contentLengthText, out ulong parsedBodySize) ||
                    parsedBodySize > int.MaxValue)
                {
                    return false;
                }

                int expectedBodySize = (int)parsedBodySize;
                while (body.Length < expectedBodySize)
                {
                    int count;
                    try
                    {
                        count = stream.Read(
                            chunk,
                            0,
                            (int)Math.Min(chunk.Length, expectedBodySize - body.Length));
                    }
                    catch (IOException)
                    {
                        return false;
                    }

                    if (count <= 0)
                    {
                        return false;
                    }

                    body.Write(chunk, 0, count);
                }

                byte[] bodyBytes = body.ToArray();
                if (bodyBytes.Length != expectedBodySize)
                {
                    Array.Resize(ref bodyBytes, expectedBodySize);
                }

                request.Body = bodyBytes;
                return true;
            }

            if (request.Method == "PUT" || request.Method == "POST")
            {
                for (;;)
                {
                    int count;
                    try
                    {
                        count = stream.Read(chunk, 0, chunk.Length);
                    }
                    catch (IOException)
                    {
                        return false;
                    }

                    if (count == 0)
                    {
                        break;
                    }

                    body.Write(chunk, 0, count);
                }
            }

            request.Body = body.ToArray();
            return true;
        }

        private static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int index = 0; index <= length - 4; ++index)
            {
                if (buffer[index] == 13 &&
                    buffer[index + 1] == 10 &&
                    buffer[index + 2] == 13 &&
                    buffer[index + 3] == 10)
                {
                    return index;
                }
            }

            return -1;
        }

        /// <summary>
        /// Отправляет HTTP response полностью и возвращает false при закрытом или ошибочном socket.
        /// </summary>
        private static bool SendResponse(TcpClient clientSocket, HttpResponse response)
        {
            try
            {
                StringBuilder headers = new StringBuilder();
                headers.Append("HTTP/1.1 ")
                    .Append(response.StatusCode)
                    .Append(' ')
                    .Append(response.ReasonPhrase)
                    .Append("\r\n");

                foreach (KeyValuePair<string, string> header in response.Headers)
                {
                    headers.Append(header.Key)
                        .Append(": ")
                        .Append(header.Value)
                        .Append("\r\n");
                }

                headers.Append("Content-Length: ")
                    .Append(response.Body.Length)
                    .Append("\r\n");
                headers.Append("Connection: close\r\n\r\n");

                NetworkStream stream = clientSocket.GetStream();
                byte[] headerBytes = Encoding.ASCII.GetBytes(headers.ToString());
                stream.Write(headerBytes, 0, headerBytes.Length);
                stream.Write(response.Body, 0, response.Body.Length);
                stream.Flush();
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        private void CloseActiveClientSockets()
        {
            lock (activeSocketsMutex_)
            {
                foreach (TcpClient client in activeSockets_)
                {
                    try
                    {
                        client.Client.Shutdown(SocketShutdown.Both);
                    }
                    catch (SocketException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }

                    client.Close();
                }

                activeSockets_.Clear();
            }
        }

        public void Dispose()
        {
            Stop();
            discoveryResponder_.Dispose();
        }
    }
}
