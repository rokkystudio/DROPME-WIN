using System;
using System.Runtime.InteropServices;
using DROPME.Clients;

namespace DROPME.Utils
{
    /// <summary>
    /// Монтирует WebDAV endpoint Android-устройства во временную букву диска Windows.
    /// </summary>
    internal static class WebDavDriveMapper
    {
        internal sealed class MountResult
        {
            public string? DriveLetter { get; set; }
            public string ErrorMessage { get; set; } = string.Empty;
            public uint ErrorCode { get; set; }
        }

        private const uint ResourceTypeDisk = 1;
        private const uint ConnectTemporary = 0x00000004;
        private const uint ConnectUpdateProfile = 0x00000001;
        private const uint ErrorAlreadyAssigned = 85;
        private const uint ErrorDeviceAlreadyRemembered = 1202;
        private const uint ErrorBadNetName = 67;
        private const uint ErrorNotConnected = 2250;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NetResource
        {
            public uint Scope;
            public uint Type;
            public uint DisplayType;
            public uint Usage;
            public string? LocalName;
            public string? RemoteName;
            public string? Comment;
            public string? Provider;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern uint WNetAddConnection2(ref NetResource netResource, string? password, string? username, uint flags);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern uint WNetCancelConnection2(string name, uint flags, bool force);

        /// <summary>
        /// Пытается смонтировать WebDAV в первую свободную букву от Z до D.
        /// </summary>
        public static MountResult Mount(AndroidClient client)
        {
            if (string.IsNullOrEmpty(client.WebDavHost) || client.WebDavPort <= 0)
            {
                return new MountResult { ErrorMessage = "WebDAV endpoint is incomplete" };
            }

            string remoteName = BuildRemoteName(client);
            for (;;)
            {
                string? localName = FindFreeDriveLetter();
                if (localName == null)
                {
                    return new MountResult { ErrorMessage = "No free drive letters are available" };
                }

                NetResource resource = new NetResource
                {
                    Type = ResourceTypeDisk,
                    LocalName = localName,
                    RemoteName = remoteName
                };

                Log.Info("Attempting WebDAV drive mount: " + localName + " -> " + remoteName);
                uint result = WNetAddConnection2(ref resource, null, null, ConnectTemporary);
                if (result == 0)
                {
                    string driveLetter = localName.TrimEnd(':');
                    Log.Info("WebDAV drive mounted: " + driveLetter);
                    return new MountResult { DriveLetter = driveLetter };
                }

                if (result == ErrorAlreadyAssigned || result == ErrorDeviceAlreadyRemembered)
                {
                    Log.Warn("Drive letter collision while mounting WebDAV, trying another letter. Error=" + result);
                    continue;
                }

                string message = "WebDAV mount failed with error " + result;
                if (result == ErrorBadNetName)
                {
                    message += ". Windows WebClient did not accept the Android WebDAV endpoint as a valid network location.";
                }
                Log.Warn(message);
                return new MountResult { ErrorMessage = message, ErrorCode = result };
            }
        }

        /// <summary>
        /// Размонтирует ранее выданную букву диска клиента.
        /// </summary>
        public static void Unmount(AndroidClient client)
        {
            if (string.IsNullOrEmpty(client.DriveLetter))
            {
                return;
            }

            uint result = WNetCancelConnection2(client.DriveLetter + ":", ConnectUpdateProfile, true);
            if (result == 0 || result == ErrorNotConnected)
            {
                Log.Info("WebDAV drive unmounted: " + client.DriveLetter);
                return;
            }

            Log.Warn("WebDAV drive unmount failed for " + client.DriveLetter + " with error " + result);
        }

        private static string BuildRemoteName(AndroidClient client)
        {
            return "http://" + client.WebDavHost + "@" + client.WebDavPort + "/";
        }

        private static string? FindFreeDriveLetter()
        {
            uint logicalDrives = GetLogicalDrives();
            for (char letter = 'Z'; letter >= 'D'; --letter)
            {
                uint bit = 1u << (letter - 'A');
                if ((logicalDrives & bit) == 0)
                {
                    return letter + ":";
                }
            }
            return null;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetLogicalDrives();
    }
}
