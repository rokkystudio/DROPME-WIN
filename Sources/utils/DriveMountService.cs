using DROPME.Clients;

namespace DROPME.Utils
{
    /// <summary>
    /// Выбирает backend для публикации Android-хранилища как диска Windows.
    /// Основным backend является WinFsp; системный WebClient используется только как fallback.
    /// </summary>
    internal static class DriveMountService
    {
        internal sealed class MountResult
        {
            public string DriveLetter { get; set; } = string.Empty;
            public AndroidDriveBackend Backend { get; set; } = AndroidDriveBackend.None;
            public string ErrorMessage { get; set; } = string.Empty;
        }

        /// <summary>
        /// Сначала монтирует файловый API Android через WinFsp, затем при неуспехе пробует WebClient.
        /// </summary>
        public static MountResult Mount(AndroidClient client)
        {
            string? winFspDrive = WinFspDriveHost.Mount(client, out string winFspError);
            if (!string.IsNullOrEmpty(winFspDrive))
            {
                return new MountResult
                {
                    DriveLetter = winFspDrive,
                    Backend = AndroidDriveBackend.WinFsp
                };
            }

            bool webClientReady = WebClientService.EnsureRunning();
            if (!webClientReady)
            {
                return new MountResult
                {
                    ErrorMessage = BuildErrorMessage(winFspError: winFspError, webClientError: "Windows WebClient service is unavailable")
                };
            }

            WebDavDriveMapper.MountResult webDavResult = WebDavDriveMapper.Mount(client);
            if (!string.IsNullOrEmpty(webDavResult.DriveLetter))
            {
                return new MountResult
                {
                    DriveLetter = webDavResult.DriveLetter,
                    Backend = AndroidDriveBackend.WebClient
                };
            }

            return new MountResult
            {
                ErrorMessage = BuildErrorMessage(winFspError, webDavResult.ErrorMessage)
            };
        }

        /// <summary>
        /// Размонтирует клиент через тот backend, который создал его текущий диск.
        /// </summary>
        public static void Unmount(AndroidClient client)
        {
            switch (client.DriveBackend)
            {
                case AndroidDriveBackend.WinFsp:
                    WinFspDriveHost.Unmount(client);
                    break;

                case AndroidDriveBackend.WebClient:
                    WebDavDriveMapper.Unmount(client);
                    break;
            }
        }

        private static string BuildErrorMessage(string winFspError, string webClientError)
        {
            string result = string.Empty;
            if (!string.IsNullOrWhiteSpace(winFspError))
            {
                result = "WinFsp: " + winFspError;
            }
            if (!string.IsNullOrWhiteSpace(webClientError))
            {
                if (result.Length > 0)
                {
                    result += "; ";
                }
                result += "WebClient: " + webClientError;
            }
            return result;
        }
    }
}
