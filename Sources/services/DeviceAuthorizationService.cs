using System;
using System.Collections.Generic;
using DROPME.Server;

namespace DROPME.Services
{
    /// <summary>
    /// Проверяет постоянное доверие и короткие разрешённые сессии для входящих DROPME-запросов.
    /// </summary>
    internal sealed class DeviceAuthorizationService
    {
        private const string DeviceIdHeader = "x-dropme-device-id";
        private const string DeviceNameHeader = "x-dropme-device-name";
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);

        private readonly object mutex_ = new object();
        private readonly DeviceTrustStore trustStore_ = new DeviceTrustStore();
        private readonly Dictionary<string, DateTime> sessions_ =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);

        public DeviceTrustStore TrustStore => trustStore_;

        public bool Authorize(
            HttpRequest request,
            string purpose,
            string itemName,
            bool promptIfUnknown)
        {
            if (!TryGetRequester(request, out string deviceId, out string deviceName))
            {
                return false;
            }

            DeviceTrustDecision decision = trustStore_.GetDecision(deviceId);
            if (decision == DeviceTrustDecision.Allowed)
            {
                trustStore_.Touch(deviceId, deviceName, request.RemoteIp);
                RememberSession(deviceId, purpose);
                return true;
            }
            if (decision == DeviceTrustDecision.Blocked)
            {
                trustStore_.Touch(deviceId, deviceName, request.RemoteIp);
                return false;
            }

            if (HasSession(deviceId, purpose))
            {
                return true;
            }

            if (!promptIfUnknown)
            {
                return false;
            }

            DeviceApprovalResult result = DeviceApprovalService.Request(
                deviceName,
                request.RemoteIp,
                deviceId,
                purpose,
                itemName);
            if (result.Remember)
            {
                trustStore_.Remember(
                    deviceId,
                    deviceName,
                    request.RemoteIp,
                    result.Allowed ? DeviceTrustDecision.Allowed : DeviceTrustDecision.Blocked);
            }
            if (result.Allowed)
            {
                RememberSession(deviceId, purpose);
            }
            return result.Allowed;
        }

        private bool HasSession(string deviceId, string purpose)
        {
            string key = deviceId + ":" + purpose;
            lock (mutex_)
            {
                if (!sessions_.TryGetValue(key, out DateTime expiresUtc))
                {
                    return false;
                }
                if (expiresUtc < DateTime.UtcNow)
                {
                    sessions_.Remove(key);
                    return false;
                }
                return true;
            }
        }

        private void RememberSession(string deviceId, string purpose)
        {
            lock (mutex_)
            {
                sessions_[deviceId + ":" + purpose] = DateTime.UtcNow + SessionLifetime;
            }
        }

        private static bool TryGetRequester(
            HttpRequest request,
            out string deviceId,
            out string deviceName)
        {
            request.Headers.TryGetValue(DeviceIdHeader, out string? rawId);
            request.Headers.TryGetValue(DeviceNameHeader, out string? rawName);
            deviceId = (rawId ?? string.Empty).Trim().ToLowerInvariant();
            deviceName = (rawName ?? string.Empty).Trim();
            if (deviceId.Length != 32 || deviceName.Length == 0)
            {
                return false;
            }

            foreach (char character in deviceId)
            {
                if (!Uri.IsHexDigit(character))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
