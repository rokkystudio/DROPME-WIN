using System;
using System.IO;
using DROPME.Services;

namespace DROPME.Utils
{
    /// <summary>
    /// Возвращает служебные директории DROPME в профиле текущего пользователя.
    /// </summary>
    internal static class DesktopFolders
    {
        /// <summary>
        /// Возвращает активную директорию входящих файлов по текущим настройкам и создаёт её.
        /// </summary>
        public static string EnsureIncomingFolder()
        {
            return EnsureIncomingFolderForTime(DateTime.Now);
        }

        /// <summary>
        /// Возвращает активную директорию входящих файлов для указанного локального времени.
        /// При включённой группировке создаёт подкаталог "DROPME yyyy-MM-dd",
        /// иначе использует выбранную директорию напрямую.
        /// </summary>
        public static string EnsureIncomingFolderForTime(DateTime localTime)
        {
            string baseDirectory = AppSettings.IncomingDirectory;
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }

            string path = AppSettings.CreateIncomingDateFolders
                ? Path.Combine(baseDirectory, "DROPME " + localTime.ToString("yyyy-MM-dd"))
                : baseDirectory;

            Directory.CreateDirectory(path);
            return Path.GetFullPath(path);
        }

        /// <summary>
        /// Возвращает путь к %LOCALAPPDATA%\DROPME\logs и создаёт каталог.
        /// </summary>
        public static string EnsureLogFolder()
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DROPME",
                "logs");
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
