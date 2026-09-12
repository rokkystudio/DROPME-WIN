using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using DROPME.Utils;

namespace DROPME.Server
{
    /// <summary>
    /// Отвечает на UDP discovery datagram для поиска Windows-сервера в локальной сети.
    /// </summary>
    internal sealed class DiscoveryResponder : IDisposable
    {
        private const int MaxComputerNameLength = 15;

        private readonly object mutex_ = new object();
        private UdpClient? udpClient_;
        private Thread? thread_;
        private volatile bool running_;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetComputerName(StringBuilder buffer, ref uint size);

        /// <summary>
        /// Запускает UDP responder на порту протокола с SO_REUSEADDR.
        /// </summary>
        public void Start()
        {
            lock (mutex_)
            {
                if (running_)
                {
                    return;
                }

                try
                {
                    udpClient_ = new UdpClient();
                    udpClient_.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udpClient_.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.DiscoveryPort));
                    running_ = true;

                    thread_ = new Thread(RunLoop)
                    {
                        IsBackground = true,
                        Name = "DROPME UDP discovery"
                    };
                    thread_.Start();

                    Log.Info("UDP discovery responder listening on port " + Protocol.DiscoveryPort);
                }
                catch (SocketException exception)
                {
                    udpClient_?.Dispose();
                    udpClient_ = null;
                    running_ = false;
                    Log.Warn("UDP discovery bind failed on port " + Protocol.DiscoveryPort + ": " + exception.SocketErrorCode);
                }
            }
        }

        /// <summary>
        /// Останавливает responder, закрывает UDP socket и дожидается рабочего потока.
        /// </summary>
        public void Stop()
        {
            Thread? thread;
            lock (mutex_)
            {
                if (!running_)
                {
                    return;
                }

                running_ = false;
                udpClient_?.Close();
                udpClient_ = null;
                thread = thread_;
                thread_ = null;
            }

            thread?.Join();
        }

        /// <summary>
        /// Принимает только DROPME_DISCOVER_V1 и WIFIDROP_DISCOVER_V1 и отвечает параметрами TCP endpoint.
        /// </summary>
        private void RunLoop()
        {
            while (running_)
            {
                try
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] bytes = udpClient_!.Receive(ref remote);
                    string payload = Encoding.ASCII.GetString(bytes);
                    if (payload != "DROPME_DISCOVER_V1" && payload != "WIFIDROP_DISCOVER_V1")
                    {
                        continue;
                    }

                    Log.Info("UDP discovery request received from " + remote.Address);

                    byte[] responseBytes = UploadController.SerializeJson(new
                    {
                        app = "DROPME",
                        role = "windows-server",
                        protocolVersion = Protocol.ProtocolVersion,
                        deviceName = GetComputerNameUtf8(),
                        tcpPort = Protocol.TcpPort
                    });

                    udpClient_!.Send(responseBytes, responseBytes.Length, remote);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (!running_)
                    {
                        break;
                    }
                }
            }
        }

        private static string GetComputerNameUtf8()
        {
            StringBuilder computerName = new StringBuilder(MaxComputerNameLength + 1);
            uint size = (uint)computerName.Capacity;
            return GetComputerName(computerName, ref size) ? computerName.ToString() : "Windows PC";
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
