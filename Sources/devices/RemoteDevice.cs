namespace DROPME.Devices
{
    /// <summary>
    /// Описывает найденное DROPME-устройство в текущей локальной сети.
    /// </summary>
    internal sealed class RemoteDevice
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string DeviceType { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public int ControlPort { get; set; }
        public int FilePort { get; set; }
        public bool FileAccessAvailable { get; set; }
        public bool ShareAvailable { get; set; }
    }
}
