using System;

namespace DROPME.Server
{
    /// <summary>
    /// Содержит сетевые параметры протокола DROPME.
    /// </summary>
    internal static class Protocol
    {
        public const ushort DiscoveryPort = 49230;
        public const ushort TcpPort = 49231;
        public const int ProtocolVersion = 1;
        public static readonly TimeSpan ControlHeartbeatInterval = TimeSpan.FromMilliseconds(2000);
        public static readonly TimeSpan ControlDisconnectTimeout = TimeSpan.FromMilliseconds(6000);
    }
}
