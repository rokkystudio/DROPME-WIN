using System;
using System.IO;

namespace DROPME.Utils
{
    /// <summary>
    /// Возвращает служебные директории DROPME в профиле текущего пользователя.
    /// </summary>
    internal static class DesktopFolders
    {
        /// <summary>
        /// Возвращает путь к папке входящих файлов для текущего времени.
        /// </summary>
        public static string EnsureIncomingFolder()
        {
            return EnsureIncomingFolderForTime(DateTime.Now);
        }

        /// <summary>
        /// Возвращает путь к папке входящих файлов вида "DROPME yyyy-MM-dd" на рабочем столе и создаёт её.
        /// </summary>
        public static string EnsureIncomingFolderForTime(DateTime localTime)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string path = Path.Combine(desktop, "DROPME " + localTime.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(path);
            return path;
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
