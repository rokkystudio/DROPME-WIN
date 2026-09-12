using System;
using System.IO;

namespace DROPME.Services
{
    /// <summary>
    /// Хранит пользовательские настройки темы и режима языка в профиле текущего пользователя.
    /// </summary>
    internal static class AppSettings
    {
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DROPME");
        private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.ini");

        public static string Theme { get; set; } = "Light";
        public static string Language { get; set; } = LocalizationService.AutomaticLanguage;

        /// <summary>
        /// Загружает поддерживаемые настройки из текстового файла.
        /// </summary>
        public static void Load()
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }

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
            }
        }

        /// <summary>
        /// Записывает текущие значения темы и режима языка в профиль пользователя.
        /// </summary>
        public static void Save()
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllLines(SettingsPath, new[]
            {
                "Theme=" + Theme,
                "Language=" + Language
            });
        }
    }
}
