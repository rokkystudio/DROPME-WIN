using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using DROPME.Services;
using DROPME.Utils;

namespace DROPME.Server
{
    /// <summary>
    /// Отвечает на UDP discovery datagram identity текущего DROPME-устройства.
    /// </summary>
    internal sealed class DiscoveryResponder : IDisposable
    {
        private readonly object mutex_ = new object();
        private UdpClient? udpClient_;
        private Thread? thread_;
        private volatile bool running_;

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
        /// Принимает discovery datagram и возвращает protocol v3 identity без привязки доверия к IP.
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
                        role = "device",
                        protocolVersion = Protocol.ProtocolVersion,
                        deviceId = AppSettings.DeviceId,
                        deviceName = AppSettings.DeviceName,
                        deviceType = "windows",
                        tcpPort = Protocol.TcpPort,
                        filePort = Protocol.TcpPort,
                        fileAccessAvailable = true,
                        shareAvailable = true
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

        public void Dispose()
        {
            Stop();
        }
    }
}
