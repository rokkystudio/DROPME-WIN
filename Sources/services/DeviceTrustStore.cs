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
        public DeviceTrustDecision ShareDecision { get; set; }
        public DeviceTrustDecision FilesDecision { get; set; }
        public DateTime LastSeenUtc { get; set; }

        public DeviceTrustDecision AggregateDecision =>
            ShareDecision == DeviceTrustDecision.Allowed || FilesDecision == DeviceTrustDecision.Allowed
                ? DeviceTrustDecision.Allowed
                : ShareDecision == DeviceTrustDecision.Blocked || FilesDecision == DeviceTrustDecision.Blocked
                    ? DeviceTrustDecision.Blocked
                    : DeviceTrustDecision.Unknown;
    }

    /// <summary>
    /// Хранит постоянные решения отдельно для приёма файлов (share) и доступа к папкам (files).
    /// Старое единое allowed-решение мигрирует в Unknown для обоих прав, а старое blocked
    /// сохраняется как блокировка обоих прав.
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

        /// <summary>
        /// Возвращает эффективное решение для указанной операции.
        /// Полный доступ к папкам включает возможность принимать входящие файлы,
        /// но разрешение на приём файлов само по себе не даёт доступ к папкам.
        /// </summary>
        public DeviceTrustDecision GetDecision(string deviceId, string purpose)
        {
            lock (mutex_)
            {
                if (!devices_.TryGetValue(deviceId, out KnownDevice? device))
                {
                    return DeviceTrustDecision.Unknown;
                }

                if (purpose == "share")
                {
                    return device.FilesDecision == DeviceTrustDecision.Allowed
                        ? DeviceTrustDecision.Allowed
                        : device.ShareDecision;
                }

                return device.FilesDecision;
            }
        }

        public void Remember(
            string deviceId,
            string deviceName,
            string ip,
            string purpose,
            DeviceTrustDecision decision)
        {
            if (decision == DeviceTrustDecision.Unknown)
            {
                throw new ArgumentOutOfRangeException(nameof(decision));
            }

            lock (mutex_)
            {
                if (!devices_.TryGetValue(deviceId, out KnownDevice? device))
                {
                    device = new KnownDevice { DeviceId = deviceId };
                    devices_[deviceId] = device;
                }

                device.DeviceName = deviceName;
                device.LastIp = ip;
                device.LastSeenUtc = DateTime.UtcNow;
                if (purpose == "share")
                {
                    device.ShareDecision = decision;
                }
                else
                {
                    device.FilesDecision = decision;
                }
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

        public void BlockAll(string deviceId, string deviceName, string ip)
        {
            Remember(deviceId, deviceName, ip, "share", DeviceTrustDecision.Blocked);
            Remember(deviceId, deviceName, ip, "files", DeviceTrustDecision.Blocked);
        }

        /// <summary>
        /// Сбрасывает оба постоянных решения до Unknown, не выдавая ни одного разрешения.
        /// </summary>
        public void ClearDecisions(string deviceId)
        {
            lock (mutex_)
            {
                if (!devices_.TryGetValue(deviceId, out KnownDevice? device))
                {
                    return;
                }

                device.ShareDecision = DeviceTrustDecision.Unknown;
                device.FilesDecision = DeviceTrustDecision.Unknown;
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
                    .Where(device => device.AggregateDecision == decision)
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

                bool migrated = false;
                foreach (string line in File.ReadAllLines(StorePath))
                {
                    string[] fields = line.Split('	');
                    if (fields.Length != 5 && fields.Length != 6)
                    {
                        continue;
                    }

                    string deviceId = Unescape(fields[0]);
                    if (!IsValidDeviceId(deviceId))
                    {
                        continue;
                    }

                    DeviceTrustDecision shareDecision;
                    DeviceTrustDecision filesDecision;
                    DateTime lastSeen;
                    if (fields.Length == 5)
                    {
                        DeviceTrustDecision legacy = ParseDecision(fields[3]);
                        shareDecision = legacy == DeviceTrustDecision.Blocked
                            ? DeviceTrustDecision.Blocked
                            : DeviceTrustDecision.Unknown;
                        filesDecision = legacy == DeviceTrustDecision.Blocked
                            ? DeviceTrustDecision.Blocked
                            : DeviceTrustDecision.Unknown;
                        lastSeen = ParseDate(fields[4]);
                        migrated = true;
                    }
                    else
                    {
                        shareDecision = ParseDecision(fields[3]);
                        filesDecision = ParseDecision(fields[4]);
                        lastSeen = ParseDate(fields[5]);
                    }

                    devices_[deviceId] = new KnownDevice
                    {
                        DeviceId = deviceId,
                        DeviceName = Unescape(fields[1]),
                        LastIp = Unescape(fields[2]),
                        ShareDecision = shareDecision,
                        FilesDecision = filesDecision,
                        LastSeenUtc = lastSeen
                    };
                }

                if (migrated)
                {
                    SaveLocked();
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
                    FormatDecision(device.ShareDecision) + "	" +
                    FormatDecision(device.FilesDecision) + "	" +
                    device.LastSeenUtc.ToString("O", CultureInfo.InvariantCulture))
                .ToArray();
            File.WriteAllLines(StorePath, lines);
        }

        private static DeviceTrustDecision ParseDecision(string value) =>
            value == "allowed"
                ? DeviceTrustDecision.Allowed
                : value == "blocked"
                    ? DeviceTrustDecision.Blocked
                    : DeviceTrustDecision.Unknown;

        private static string FormatDecision(DeviceTrustDecision decision) =>
            decision == DeviceTrustDecision.Allowed
                ? "allowed"
                : decision == DeviceTrustDecision.Blocked
                    ? "blocked"
                    : "unknown";

        private static DateTime ParseDate(string value) =>
            DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTime parsed)
                ? parsed
                : DateTime.MinValue;

        private static KnownDevice Clone(KnownDevice device) =>
            new KnownDevice
            {
                DeviceId = device.DeviceId,
                DeviceName = device.DeviceName,
                LastIp = device.LastIp,
                ShareDecision = device.ShareDecision,
                FilesDecision = device.FilesDecision,
                LastSeenUtc = device.LastSeenUtc
            };

        private static string Escape(string value) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));

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
