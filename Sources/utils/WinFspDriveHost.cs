using DROPME.Clients;

namespace DROPME.Utils
{
    /// <summary>
    /// Представляет WinFsp backend в конфигурации, соответствующей текущей C++-сборке WIFIDROP_ENABLE_WINFSP=OFF.
    /// </summary>
    internal static class WinFspDriveHost
    {
        /// <summary>
        /// Возвращает отсутствие диска и сообщение о выключенной поддержке WinFsp, как non-WinFsp ветка исходного C++ кода.
        /// </summary>
        public static string? Mount(AndroidClient client, out string errorMessage)
        {
            errorMessage = "WinFsp support is not enabled in this build";
            return null;
        }

        /// <summary>
        /// Не выполняет действий, поскольку WinFsp backend не включён в текущей конфигурации сборки.
        /// </summary>
        public static void Unmount(AndroidClient client)
        {
        }
    }
}
