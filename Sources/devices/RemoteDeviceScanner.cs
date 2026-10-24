using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DROPME.Server;

namespace DROPME.Devices
{
    /// <summary>
    /// Ищет protocol v3 DROPME-устройства HTTP-проверкой адресов локальных IPv4-подсетей.
    /// Результаты объединяются по постоянному device ID, а IP используется только как текущий маршрут.
    /// </summary>
    internal sealed class RemoteDeviceScanner
    {
        private const int MaxScanHosts = 4096;
        private const int MaxParallelRequests = 32;
        private const int RememberedHostAttempts = 3;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(450);

        private readonly object rememberedHostsMutex_ = new object();
        private readonly HashSet<string> rememberedHosts_ = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Выполняет один полный scan. IP, на которых устройство уже находилось в текущем сеансе,
        /// проверяются несколько раз, но итоговый список содержит только устройства,
        /// подтвердившие присутствие именно в текущем scan.
        /// </summary>
        public IReadOnlyList<RemoteDevice> Scan(CancellationToken cancellationToken)
        {
            HashSet<string> localAddresses = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> candidates = BuildCandidates(localAddresses);
            HashSet<string> rememberedHosts = SnapshotRememberedHosts();
            candidates.UnionWith(rememberedHosts);
            ConcurrentDictionary<string, RemoteDevice> devices =
                new ConcurrentDictionary<string, RemoteDevice>(StringComparer.Ordinal);

            ParallelOptions options = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaxParallelRequests
            };

            try
            {
                Parallel.ForEach(candidates, options, host =>
                {
                    int attempts = rememberedHosts.Contains(host) ? RememberedHostAttempts : 1;
                    RemoteDevice? device = Probe(host, attempts, cancellationToken);
                    if (device != null)
                    {
                        RememberHost(host);
                        devices[device.DeviceId] = device;
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }

            return devices.Values
                .OrderBy(device => device.DeviceName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(device => device.DeviceId, StringComparer.Ordinal)
                .ToList();
        }

        private static HashSet<string> BuildCandidates(HashSet<string> localAddresses)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation addressInfo in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (addressInfo.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    IPAddress address = addressInfo.Address;
                    localAddresses.Add(address.ToString());

                    int prefixLength = addressInfo.PrefixLength;
                    if (prefixLength < 1 || prefixLength > 30)
                    {
                        continue;
                    }

                    uint ip = ToUInt32(address);
                    uint mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
                    uint network = ip & mask;
                    uint broadcast = network | ~mask;
                    ulong hostCount = (ulong)broadcast - network - 1UL;
                    if (hostCount > MaxScanHosts)
                    {
                        continue;
                    }

                    for (uint current = network + 1; current < broadcast; ++current)
                    {
                        string host = FromUInt32(current).ToString();
                        if (!localAddresses.Contains(host))
                        {
                            result.Add(host);
                        }
                    }
                }
            }

            foreach (string localAddress in localAddresses)
            {
                result.Remove(localAddress);
            }
            return result;
        }

        /// <summary>
        /// Проверяет один IP несколько раз. Повторы используются только для IP,
        /// которые уже подтверждались в текущем сеансе приложения.
        /// </summary>
        private static RemoteDevice? Probe(
            string host,
            int attempts,
            CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < attempts; ++attempt)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RemoteDevice? device = ProbeOnce(host, cancellationToken);
                if (device != null)
                {
                    return device;
                }
            }
            return null;
        }

        private static RemoteDevice? ProbeOnce(string host, CancellationToken cancellationToken)
        {
            using HttpClient client = new HttpClient
            {
                Timeout = RequestTimeout
            };

            try
            {
                using HttpRequestMessage request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "http://" + host + ":" + Protocol.TcpPort + "/dropme/info");
                using HttpResponseMessage response = client.Send(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                string json = response.Content.ReadAsStringAsync(cancellationToken)
                    .GetAwaiter()
                    .GetResult();
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;

                if (ReadString(root, "app") != "DROPME" ||
                    ReadString(root, "role") != "device" ||
                    ReadInt(root, "protocolVersion") != Protocol.ProtocolVersion)
                {
                    return null;
                }

                string deviceId = ReadString(root, "deviceId").ToLowerInvariant();
                if (!IsValidDeviceId(deviceId))
                {
                    return null;
                }

                return new RemoteDevice
                {
                    DeviceId = deviceId,
                    DeviceName = ReadString(root, "deviceName"),
                    DeviceType = ReadString(root, "deviceType"),
                    Host = host,
                    ControlPort = ReadInt(root, "tcpPort", Protocol.TcpPort),
                    FilePort = ReadInt(root, "filePort"),
                    FileAccessAvailable = ReadBool(root, "fileAccessAvailable"),
                    ShareAvailable = ReadBool(root, "shareAvailable")
                };
            }
            catch (Exception exception) when (
                exception is HttpRequestException ||
                exception is TaskCanceledException ||
                exception is OperationCanceledException ||
                exception is JsonException)
            {
                return null;
            }
        }

        private HashSet<string> SnapshotRememberedHosts()
        {
            lock (rememberedHostsMutex_)
            {
                return new HashSet<string>(rememberedHosts_, StringComparer.Ordinal);
            }
        }

        private void RememberHost(string host)
        {
            lock (rememberedHostsMutex_)
            {
                rememberedHosts_.Add(host);
            }
        }

        private static string ReadString(JsonElement root, string property)
        {
            return root.TryGetProperty(property, out JsonElement value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        private static int ReadInt(JsonElement root, string property, int defaultValue = 0)
        {
            return root.TryGetProperty(property, out JsonElement value) &&
                   value.TryGetInt32(out int result)
                ? result
                : defaultValue;
        }

        private static bool ReadBool(JsonElement root, string property)
        {
            return root.TryGetProperty(property, out JsonElement value) &&
                   value.ValueKind == JsonValueKind.True;
        }

        private static bool IsValidDeviceId(string value)
        {
            return value.Length == 32 && value.All(Uri.IsHexDigit);
        }

        private static uint ToUInt32(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return ((uint)bytes[0] << 24) |
                   ((uint)bytes[1] << 16) |
                   ((uint)bytes[2] << 8) |
                   bytes[3];
        }

        private static IPAddress FromUInt32(uint value)
        {
            return new IPAddress(new[]
            {
                (byte)(value >> 24),
                (byte)(value >> 16),
                (byte)(value >> 8),
                (byte)value
            });
        }
    }
}
