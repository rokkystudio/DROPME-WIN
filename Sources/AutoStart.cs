using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using DROPME.Utils;

namespace DROPME
{
    /// <summary>
    /// Управляет значением автозапуска DROPME в разделе HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
    /// </summary>
    internal sealed class AutoStart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "DROPME";
        private const string LegacyValueName = "WiFiDrop";
        private const int MaxPath = 260;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileName(IntPtr moduleHandle, StringBuilder fileName, uint size);

        /// <summary>
        /// Возвращает текущее состояние автозапуска по точному совпадению команды DROPME или legacy WiFiDrop.
        /// </summary>
        public bool IsEnabled()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            if (key == null)
            {
                return false;
            }

            string commandLine = BuildCommandLine();
            return QueryMatchingValue(key, ValueName, commandLine) ||
                   QueryMatchingValue(key, LegacyValueName, commandLine);
        }

        /// <summary>
        /// Включает или выключает автозапуск для текущего пользователя и создаёт Run key при его отсутствии.
        /// </summary>
        public bool SetEnabled(bool enabled)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
                if (key == null)
                {
                    return false;
                }

                if (enabled)
                {
                    key.SetValue(ValueName, BuildCommandLine(), RegistryValueKind.String);
                    key.DeleteValue(LegacyValueName, false);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                    key.DeleteValue(LegacyValueName, false);
                }

                return true;
            }
            catch (Exception exception)
            {
                Log.Error("Failed to update autostart registry value: " + exception.Message);
                return false;
            }
        }

        private static bool QueryMatchingValue(RegistryKey key, string valueName, string expectedCommandLine)
        {
            return key.GetValue(valueName) is string value &&
                   string.Equals(expectedCommandLine, value, StringComparison.Ordinal);
        }

        /// <summary>
        /// Формирует строку запуска с полным Win32-путём к исполняемому файлу и аргументом --tray.
        /// </summary>
        private static string BuildCommandLine()
        {
            StringBuilder modulePath = new StringBuilder(MaxPath);
            uint length = GetModuleFileName(IntPtr.Zero, modulePath, MaxPath);
            if (length == 0 || length == MaxPath)
            {
                Log.Error("GetModuleFileNameW failed while building autostart command");
                return string.Empty;
            }

            return "\"" + modulePath + "\" --tray";
        }
    }
}
