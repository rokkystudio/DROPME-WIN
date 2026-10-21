using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace DROPME.Services
{
    /// <summary>
    /// Хранит пользовательские настройки DROPME и постоянную identity текущего устройства.
    /// </summary>
    internal static class AppSettings
    {
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DROPME");
        private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.ini");

        public static string Theme { get; set; } = "Light";
        public static string Language { get; set; } = string.Empty;
        public static bool UseWebInterface { get; set; }
        public static bool DeviceServiceEnabled { get; set; } = true;
        public static string DeviceName { get; set; } = Environment.MachineName;
        public static string PublishedStorageRootIds { get; set; } = "profile";
        public static string CustomStorageRoots { get; set; } = string.Empty;
        public static string IncomingDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        public static bool CreateIncomingDateFolders { get; set; } = true;
        public static string DeviceId { get; private set; } = string.Empty;

        /// <summary>
        /// Загружает поддерживаемые настройки и гарантирует наличие постоянного opaque ID устройства.
        /// </summary>
        public static void Load()
        {
            if (File.Exists(SettingsPath))
            {
                foreach (string line in File.ReadAllLines(SettingsPath))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();
                    if (string.Equals(key, "Theme", StringComparison.OrdinalIgnoreCase))
                    {
                        Theme = value;
                    }
                    else if (string.Equals(key, "Language", StringComparison.OrdinalIgnoreCase))
                    {
                        Language = value;
                    }
                    else if (string.Equals(key, "UseWebInterface", StringComparison.OrdinalIgnoreCase))
                    {
                        UseWebInterface = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (string.Equals(key, "DeviceServiceEnabled", StringComparison.OrdinalIgnoreCase))
                    {
                        DeviceServiceEnabled = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (string.Equals(key, "DeviceName", StringComparison.OrdinalIgnoreCase))
                    {
                        DeviceName = value;
                    }
                    else if (string.Equals(key, "PublishedStorageRootIds", StringComparison.OrdinalIgnoreCase))
                    {
                        PublishedStorageRootIds = value;
                    }
                    else if (string.Equals(key, "CustomStorageRoots", StringComparison.OrdinalIgnoreCase))
                    {
                        CustomStorageRoots = value;
                    }
                    else if (string.Equals(key, "IncomingDirectory", StringComparison.OrdinalIgnoreCase))
                    {
                        IncomingDirectory = value;
                    }
                    else if (string.Equals(key, "CreateIncomingDateFolders", StringComparison.OrdinalIgnoreCase))
                    {
                        CreateIncomingDateFolders = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (string.Equals(key, "DeviceId", StringComparison.OrdinalIgnoreCase))
                    {
                        DeviceId = value;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(DeviceName))
            {
                DeviceName = Environment.MachineName;
            }

            if (string.IsNullOrWhiteSpace(IncomingDirectory))
            {
                IncomingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }

            if (!LocalizationService.IsSupportedLanguage(Language))
            {
                Language = LocalizationService.DetectSystemLanguage();
                Save();
            }
            else
            {
                Language = LocalizationService.NormalizeLanguage(Language);
            }

            if (!IsValidDeviceId(DeviceId))
            {
                DeviceId = BuildDeviceId();
                Save();
            }
        }

        /// <summary>
        /// Записывает настройки и identity текущего устройства в профиль пользователя.
        /// </summary>
        public static void Save()
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllLines(SettingsPath, new[]
            {
                "Theme=" + Theme,
                "Language=" + Language,
                "UseWebInterface=" + (UseWebInterface ? "true" : "false"),
                "DeviceServiceEnabled=" + (DeviceServiceEnabled ? "true" : "false"),
                "DeviceName=" + DeviceName,
                "PublishedStorageRootIds=" + PublishedStorageRootIds,
                "CustomStorageRoots=" + CustomStorageRoots,
                "IncomingDirectory=" + IncomingDirectory,
                "CreateIncomingDateFolders=" + (CreateIncomingDateFolders ? "true" : "false"),
                "DeviceId=" + DeviceId
            });
        }

        private static bool IsValidDeviceId(string value)
        {
            if (value.Length != 32)
            {
                return false;
            }

            foreach (char symbol in value)
            {
                bool isDigit = symbol >= '0' && symbol <= '9';
                bool isHexLetter = symbol >= 'a' && symbol <= 'f';
                if (!isDigit && !isHexLetter)
                {
                    return false;
                }
            }

            return true;
        }

        private static string BuildDeviceId()
        {
            string source = ReadMachineGuid();
            if (source.Length == 0)
            {
                source = Guid.NewGuid().ToString("N");
            }

            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(source));
            StringBuilder result = new StringBuilder(32);
            for (int index = 0; index < 16; ++index)
            {
                result.Append(digest[index].ToString("x2"));
            }
            return result.ToString();
        }

        private static string ReadMachineGuid()
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                return key?.GetValue("MachineGuid")?.ToString()?.Trim() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
