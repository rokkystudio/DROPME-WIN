using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DROPME.Utils
{
    /// <summary>
    /// Пишет текстовый лог DROPME в %LOCALAPPDATA%\DROPME\logs\dropme.log.
    /// </summary>
    internal static class Log
    {
        private static readonly object Mutex = new object();
        private static StreamWriter? writer_;

        /// <summary>
        /// Подготавливает директорию логов и открывает файл журнала в режиме append.
        /// </summary>
        public static void Initialize()
        {
            lock (Mutex)
            {
                if (writer_ != null)
                {
                    return;
                }

                string logPath = Path.Combine(DesktopFolders.EnsureLogFolder(), "dropme.log");
                writer_ = new StreamWriter(
                    new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read),
                    new UTF8Encoding(false));
                writer_.AutoFlush = true;
            }
        }

        /// <summary>
        /// Завершает работу логгера и закрывает файл.
        /// </summary>
        public static void Shutdown()
        {
            lock (Mutex)
            {
                writer_?.Flush();
                writer_?.Dispose();
                writer_ = null;
            }
        }

        public static void Info(string message) { WriteLine("INFO", message); }
        public static void Warn(string message) { WriteLine("WARN", message); }
        public static void Error(string message) { WriteLine("ERROR", message); }

        /// <summary>
        /// Записывает одну строку с локальным timestamp и указанным уровнем.
        /// </summary>
        private static void WriteLine(string level, string message)
        {
            lock (Mutex)
            {
                if (writer_ == null)
                {
                    return;
                }

                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] [" + level + "] " + message;
                writer_.WriteLine(line);
                Debug.WriteLine(line);
            }
        }
    }
}
