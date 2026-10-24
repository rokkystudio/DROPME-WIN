using System;
using System.Collections.Generic;
using System.Threading;
using DROPME.Server;

namespace DROPME.Services
{
    /// <summary>
    /// Проверяет постоянное доверие и короткие разрешённые сессии для входящих DROPME-запросов.
    /// Одновременно допускает только одно пользовательское подтверждение и временно привязывает
    /// один IP-адрес к одному device ID, чтобы запросы не могли маскироваться под разные устройства.
    /// </summary>
    internal sealed class DeviceAuthorizationService
    {
        private const string DeviceIdHeader = "x-dropme-device-id";
        private const string DeviceNameHeader = "x-dropme-device-name";
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan IpBindingLifetime = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan ApprovalQueueWait = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ApprovalWaitTimeout = TimeSpan.FromSeconds(65);

        private sealed class IpBinding
        {
            public string DeviceId { get; init; } = string.Empty;
            public DateTime LastSeenUtc { get; set; }
        }

        private sealed class PendingApproval
        {
            public ManualResetEventSlim Completed { get; } = new ManualResetEventSlim(false);
            public bool Allowed { get; set; }
        }

        private readonly object mutex_ = new object();
        private readonly DeviceTrustStore trustStore_ = new DeviceTrustStore();
        private readonly Dictionary<string, DateTime> sessions_ =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Dictionary<string, IpBinding> ipBindings_ =
            new Dictionary<string, IpBinding>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PendingApproval> pendingApprovals_ =
            new Dictionary<string, PendingApproval>(StringComparer.Ordinal);
        private readonly SemaphoreSlim approvalUiGate_ = new SemaphoreSlim(1, 1);

        public DeviceTrustStore TrustStore => trustStore_;

        /// <summary>
        /// Проверяет доверие, краткосрочную сессию и при необходимости синхронно запрашивает
        /// одно пользовательское подтверждение. Повторные одинаковые запросы ожидают уже открытый
        /// диалог вместо создания новых окон.
        /// </summary>
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

            if (!TryBindIdentity(request.RemoteIp, deviceId))
            {
                return false;
            }

            DeviceTrustDecision decision = trustStore_.GetDecision(deviceId);
            if (decision == DeviceTrustDecision.Allowed)
            {
                trustStore_.Touch(deviceId, deviceName, request.RemoteIp);
                RememberSession(deviceId, request.RemoteIp, purpose);
                return true;
            }
            if (decision == DeviceTrustDecision.Blocked)
            {
                trustStore_.Touch(deviceId, deviceName, request.RemoteIp);
                return false;
            }

            if (HasSession(deviceId, request.RemoteIp, purpose))
            {
                return true;
            }

            if (!promptIfUnknown)
            {
                return false;
            }

            return RequestApproval(
                deviceId,
                deviceName,
                request.RemoteIp,
                purpose,
                itemName);
        }

        /// <summary>
        /// На короткое время закрепляет IP за первым увиденным device ID.
        /// Другой ID с того же IP отклоняется до истечения привязки.
        /// </summary>
        private bool TryBindIdentity(string remoteIp, string deviceId)
        {
            if (string.IsNullOrWhiteSpace(remoteIp))
            {
                return false;
            }

            DateTime now = DateTime.UtcNow;
            lock (mutex_)
            {
                List<string> expired = new List<string>();
                foreach (KeyValuePair<string, IpBinding> pair in ipBindings_)
                {
                    if (now - pair.Value.LastSeenUtc >= IpBindingLifetime)
                    {
                        expired.Add(pair.Key);
                    }
                }
                foreach (string key in expired)
                {
                    ipBindings_.Remove(key);
                }

                if (ipBindings_.TryGetValue(remoteIp, out IpBinding? binding))
                {
                    if (!string.Equals(binding.DeviceId, deviceId, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    binding.LastSeenUtc = now;
                    return true;
                }

                ipBindings_[remoteIp] = new IpBinding
                {
                    DeviceId = deviceId,
                    LastSeenUtc = now
                };
                return true;
            }
        }

        /// <summary>
        /// Объединяет одинаковые pending-запросы и не создаёт очередь из множества диалогов.
        /// Если другой запрос уже держит UI подтверждения дольше двух секунд, новый запрос отклоняется.
        /// </summary>
        private bool RequestApproval(
            string deviceId,
            string deviceName,
            string remoteIp,
            string purpose,
            string itemName)
        {
            string key = deviceId + ":" + remoteIp + ":" + purpose;
            PendingApproval state;
            bool owner;

            lock (mutex_)
            {
                if (pendingApprovals_.TryGetValue(key, out PendingApproval? existing))
                {
                    state = existing;
                    owner = false;
                }
                else
                {
                    state = new PendingApproval();
                    pendingApprovals_[key] = state;
                    owner = true;
                }
            }

            if (!owner)
            {
                return state.Completed.Wait(ApprovalWaitTimeout) && state.Allowed;
            }

            bool gateTaken = false;
            try
            {
                gateTaken = approvalUiGate_.Wait(ApprovalQueueWait);
                if (!gateTaken)
                {
                    state.Allowed = false;
                    return false;
                }

                DeviceApprovalResult result = DeviceApprovalService.Request(
                    deviceName,
                    remoteIp,
                    deviceId,
                    purpose,
                    itemName);

                if (result.Remember)
                {
                    trustStore_.Remember(
                        deviceId,
                        deviceName,
                        remoteIp,
                        result.Allowed
                            ? DeviceTrustDecision.Allowed
                            : DeviceTrustDecision.Blocked);
                }

                state.Allowed = result.Allowed;
                if (result.Allowed)
                {
                    RememberSession(deviceId, remoteIp, purpose);
                }
                return result.Allowed;
            }
            finally
            {
                state.Completed.Set();
                lock (mutex_)
                {
                    pendingApprovals_.Remove(key);
                }
                if (gateTaken)
                {
                    approvalUiGate_.Release();
                }
            }
        }

        private bool HasSession(string deviceId, string remoteIp, string purpose)
        {
            string key = deviceId + ":" + remoteIp + ":" + purpose;
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

        private void RememberSession(string deviceId, string remoteIp, string purpose)
        {
            lock (mutex_)
            {
                sessions_[deviceId + ":" + remoteIp + ":" + purpose] =
                    DateTime.UtcNow + SessionLifetime;
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
