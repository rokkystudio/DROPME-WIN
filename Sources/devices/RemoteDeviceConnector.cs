using System;
using System.Net.Http;
using System.Threading;
using DROPME.Clients;
using DROPME.Services;
using DROPME.Utils;

namespace DROPME.Devices
{
    /// <summary>
    /// Открывает разрешённую файловую сессию с удалённым DROPME-устройством и монтирует её backend в Windows.
    /// </summary>
    internal sealed class RemoteDeviceConnector
    {
        private readonly ClientManager clientManager_;

        public RemoteDeviceConnector(ClientManager clientManager)
        {
            clientManager_ = clientManager;
        }

        public AndroidClient Connect(RemoteDevice device, Action? authorizationGranted = null)
        {
            if (!device.FileAccessAvailable || device.FilePort <= 0)
            {
                throw new InvalidOperationException("Remote device does not provide file access");
            }

            Authorize(device);
            authorizationGranted?.Invoke();

            string clientId = "remote-" + device.DeviceId;
            AndroidClient? previous = clientManager_.RemoveClient(clientId);
            if (previous != null)
            {
                DriveMountService.Unmount(previous);
            }

            AndroidClient client = new AndroidClient
            {
                ClientId = clientId,
                DeviceName = device.DeviceName,
                DeviceNumber = device.DeviceId,
                WebDavHost = device.Host,
                WebDavPort = device.FilePort,
                WebDavBasePath = "/",
                ReadOnly = false,
                MountReady = true,
                RemoteIp = device.Host,
                DriveName = device.DeviceName,
                SessionState = AndroidSessionState.Active,
                PersistentLocalConnection = true
            };

            DriveMountService.MountResult mountResult = DriveMountService.Mount(client);
            client.DriveLetter = mountResult.DriveLetter;
            client.DriveBackend = mountResult.Backend;
            client.MountError = mountResult.ErrorMessage;
            clientManager_.AddClient(client);
            return client;
        }

        private static void Authorize(RemoteDevice device)
        {
            using HttpClient client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(65)
            };
            using HttpRequestMessage request = new HttpRequestMessage(
                HttpMethod.Post,
                "http://" + device.Host + ":" + device.ControlPort + "/dropme/authorize?purpose=files");
            request.Headers.TryAddWithoutValidation("X-DROPME-Device-ID", AppSettings.DeviceId);
            request.Headers.TryAddWithoutValidation("X-DROPME-Device-Name", AppSettings.DeviceName);
            request.Content = new ByteArrayContent(Array.Empty<byte>());

            using HttpResponseMessage response = client.Send(request, CancellationToken.None);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    response.StatusCode == System.Net.HttpStatusCode.Forbidden
                        ? LocalizationService.Text("device_permission_denied")
                        : "Remote device denied file access: HTTP " + (int)response.StatusCode);
            }
        }
    }
}
