using System.Diagnostics;

namespace DROPME.Clients
{
    /// <summary>
    /// Описывает состояние persistent session Android-клиента.
    /// </summary>
    internal enum AndroidSessionState
    {
        Pending,
        Active
    }

    /// <summary>
    /// Хранит параметры подключенного Android-устройства и его control session.
    /// </summary>
    internal sealed class AndroidClient
    {
        /// <summary>
        /// Формирует отображаемое имя диска по правилам протокола DROPME.
        /// </summary>
        public static string BuildDriveName(string deviceName, string deviceNumber)
        {
            string name = string.IsNullOrEmpty(deviceName) ? "Unknown Android" : deviceName;
            string number = string.IsNullOrEmpty(deviceNumber) ? "UNKNOWN" : deviceNumber;
            return name + " #" + number;
        }

        public string ClientId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string DeviceNumber { get; set; } = string.Empty;
        public string WebDavHost { get; set; } = string.Empty;
        public int WebDavPort { get; set; }
        public string WebDavBasePath { get; set; } = string.Empty;
        public bool ReadOnly { get; set; }
        public bool MountReady { get; set; }
        public string RemoteIp { get; set; } = string.Empty;
        public string DriveLetter { get; set; } = string.Empty;
        public string MountError { get; set; } = string.Empty;
        public string DriveName { get; set; } = string.Empty;
        public AndroidSessionState SessionState { get; set; } = AndroidSessionState.Pending;
        public long LastActivityTimestamp { get; set; } = Stopwatch.GetTimestamp();

        /// <summary>
        /// Создаёт независимую копию состояния клиента для безопасного чтения вне блокировки менеджера.
        /// </summary>
        public AndroidClient Clone()
        {
            return (AndroidClient)MemberwiseClone();
        }
    }
}
