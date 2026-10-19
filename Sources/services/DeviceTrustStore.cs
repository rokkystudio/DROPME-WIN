using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DROPME.Services
{
    internal enum DeviceTrustDecision
    {
        Unknown,
        Allowed,
        Blocked
    }

    internal sealed class KnownDevice
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string LastIp { get; set; } = string.Empty;
        public DeviceTrustDecision Decision { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }

    /// <summary>
    /// Хранит постоянные решения по известным DROPME-устройствам, привязанные к opaque device ID.
    /// Имя и IP сохраняются только как последние отображаемые значения.
    /// </summary>
    internal sealed class DeviceTrustStore
    {
        private static readonly string StorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DROPME",
            "devices.tsv");

        private readonly object mutex_ = new object();
        private readonly Dictionary<string, KnownDevice> devices_ =
            new Dictionary<string, KnownDevice>(StringComparer.Ordinal);

        public DeviceTrustStore()
        {
            Load();
        }

        public DeviceTrustDecision GetDecision(string deviceId)
        {
            lock (mutex_)
            {
                return devices_.TryGetValue(deviceId, out KnownDevice? device)
                    ? device.Decision
                    : DeviceTrustDecision.Unknown;
            }
        }

        public void Remember(
            string deviceId,
            string deviceName,
            string ip,
            DeviceTrustDecision decision)
        {
            if (decision == DeviceTrustDecision.Unknown)
            {
                throw new ArgumentOutOfRangeException(nameof(decision));
            }

            lock (mutex_)
            {
                devices_[deviceId] = new KnownDevice
                {
                    DeviceId = deviceId,
                    DeviceName = deviceName,
                    LastIp = ip,
                    Decision = decision,
                    LastSeenUtc = DateTime.UtcNow
                };
                SaveLocked();
            }
        }

        public void Touch(string deviceId, string deviceName, string ip)
        {
            lock (mutex_)
            {
                if (!devices_.TryGetValue(deviceId, out KnownDevice? device))
                {
                    return;
                }

                device.DeviceName = deviceName;
                device.LastIp = ip;
                device.LastSeenUtc = DateTime.UtcNow;
                SaveLocked();
            }
        }

        public void Forget(string deviceId)
        {
            lock (mutex_)
            {
                if (devices_.Remove(deviceId))
                {
                    SaveLocked();
                }
            }
        }

        public List<KnownDevice> List(DeviceTrustDecision decision)
        {
            lock (mutex_)
            {
                return devices_.Values
                    .Where(device => device.Decision == decision)
                    .OrderByDescending(device => device.LastSeenUtc)
                    .Select(Clone)
                    .ToList();
            }
        }

        private void Load()
        {
            lock (mutex_)
            {
                devices_.Clear();
                if (!File.Exists(StorePath))
                {
                    return;
                }

                foreach (string line in File.ReadAllLines(StorePath))
                {
                    string[] fields = line.Split('	');
                    if (fields.Length != 5)
                    {
                        continue;
                    }

                    string deviceId = Unescape(fields[0]);
                    if (!IsValidDeviceId(deviceId))
                    {
                        continue;
                    }

                    DeviceTrustDecision decision = fields[3] == "allowed"
                        ? DeviceTrustDecision.Allowed
                        : fields[3] == "blocked"
                            ? DeviceTrustDecision.Blocked
                            : DeviceTrustDecision.Unknown;
                    if (decision == DeviceTrustDecision.Unknown)
                    {
                        continue;
                    }

                    DateTime lastSeen = DateTime.TryParse(
                        fields[4],
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out DateTime parsed)
                        ? parsed
                        : DateTime.MinValue;

                    devices_[deviceId] = new KnownDevice
                    {
                        DeviceId = deviceId,
                        DeviceName = Unescape(fields[1]),
                        LastIp = Unescape(fields[2]),
                        Decision = decision,
                        LastSeenUtc = lastSeen
                    };
                }
            }
        }

        private void SaveLocked()
        {
            string? directory = Path.GetDirectoryName(StorePath);
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
            }

            string[] lines = devices_.Values
                .OrderBy(device => device.DeviceId, StringComparer.Ordinal)
                .Select(device =>
                    Escape(device.DeviceId) + "	" +
                    Escape(device.DeviceName) + "	" +
                    Escape(device.LastIp) + "	" +
                    (device.Decision == DeviceTrustDecision.Allowed ? "allowed" : "blocked") + "	" +
                    device.LastSeenUtc.ToString("O", CultureInfo.InvariantCulture))
                .ToArray();
            File.WriteAllLines(StorePath, lines);
        }

        private static KnownDevice Clone(KnownDevice device)
        {
            return new KnownDevice
            {
                DeviceId = device.DeviceId,
                DeviceName = device.DeviceName,
                LastIp = device.LastIp,
                Decision = device.Decision,
                LastSeenUtc = device.LastSeenUtc
            };
        }

        private static string Escape(string value)
        {
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
        }

        private static string Unescape(string value)
        {
            try
            {
                return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch (FormatException)
            {
                return string.Empty;
            }
        }
        private static bool IsValidDeviceId(string value)
        {
            if (value.Length != 32)
            {
                return false;
            }

            foreach (char character in value)
            {
                if (!Uri.IsHexDigit(character))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
