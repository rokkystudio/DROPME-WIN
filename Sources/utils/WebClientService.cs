using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace DROPME.Utils
{
    /// <summary>
    /// Управляет системной службой WebClient и параметрами HTTP WebDAV в Windows.
    /// </summary>
    internal static class WebClientService
    {
        internal enum HttpBasicOverHttpStatus
        {
            Enabled,
            Updated,
            AccessDenied,
            Failed
        }

        private const string WebClientServiceName = "WebClient";
        private const string WebClientParametersKey = @"SYSTEM\CurrentControlSet\Services\WebClient\Parameters";
        private const string BasicAuthLevelValue = "BasicAuthLevel";
        private const string ServerNotFoundCacheLifetimeValue = "ServerNotFoundCacheLifeTimeInSec";

        private const uint ScManagerConnect = 0x0001;
        private const uint ServiceQueryStatus = 0x0004;
        private const uint ServiceStart = 0x0010;
        private const uint ServiceStop = 0x0020;
        private const uint ServiceControlStop = 0x00000001;
        private const uint ScStatusProcessInfo = 0;
        private const uint ServiceStopped = 0x00000001;
        private const uint ServiceStartPending = 0x00000002;
        private const uint ServiceStopPending = 0x00000003;
        private const uint ServiceRunning = 0x00000004;
        private const int ErrorServiceAlreadyRunning = 1056;
        private const int ErrorServiceNotActive = 1062;

        private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatusProcess
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
            public uint ProcessId;
            public uint ServiceFlags;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr scm, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(
            IntPtr service,
            uint infoLevel,
            out ServiceStatusProcess buffer,
            uint bufferSize,
            out uint bytesNeeded);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool StartService(IntPtr service, int argumentCount, IntPtr arguments);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ControlService(
            IntPtr service,
            uint control,
            out ServiceStatus serviceStatus);

        [DllImport("advapi32.dll")]
        private static extern bool CloseServiceHandle(IntPtr handle);

        /// <summary>
        /// Пытается убедиться, что служба WebClient запущена.
        /// Возвращает true, если служба уже работала или была успешно запущена.
        /// </summary>
        public static bool EnsureRunning()
        {
            IntPtr scm = OpenSCManager(null, null, ScManagerConnect);
            if (scm == IntPtr.Zero)
            {
                Log.Warn("OpenSCManagerW failed for WebClient with error " + Marshal.GetLastWin32Error());
                return false;
            }

            try
            {
                IntPtr queryHandle = OpenService(scm, WebClientServiceName, ServiceQueryStatus);
                if (queryHandle != IntPtr.Zero)
                {
                    try
                    {
                        if (QueryRunningState(queryHandle, out ServiceStatusProcess status) &&
                            status.CurrentState == ServiceRunning)
                        {
                            return true;
                        }
                    }
                    finally
                    {
                        CloseServiceHandle(queryHandle);
                    }
                }

                IntPtr service = OpenService(scm, WebClientServiceName, ServiceQueryStatus | ServiceStart);
                if (service == IntPtr.Zero)
                {
                    Log.Warn("OpenServiceW(WebClient) failed with error " + Marshal.GetLastWin32Error());
                    return false;
                }

                try
                {
                    if (QueryRunningState(service, out ServiceStatusProcess status) &&
                        status.CurrentState == ServiceRunning)
                    {
                        return true;
                    }

                    if (!StartService(service, 0, IntPtr.Zero))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != ErrorServiceAlreadyRunning)
                        {
                            Log.Warn("StartServiceW(WebClient) failed with error " + error);
                            return false;
                        }
                    }

                    bool started = WaitForState(
                        service,
                        ServiceRunning,
                        ServiceStartPending,
                        "Timed out while waiting for WebClient service to start");

                    if (started)
                    {
                        Log.Info("WebClient service is running");
                    }

                    return started;
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }

        /// <summary>
        /// Проверяет BasicAuthLevel и ServerNotFoundCacheLifeTimeInSec.
        /// При необходимости записывает значения 2 и 0 и перезапускает WebClient.
        /// </summary>
        public static HttpBasicOverHttpStatus EnsureHttpBasicOverHttpEnabled()
        {
            try
            {
                using RegistryKey? queryKey = Registry.LocalMachine.OpenSubKey(WebClientParametersKey, false);
                if (queryKey == null)
                {
                    Log.Warn("RegOpenKeyExW(WebClient Parameters, query) failed");
                    return HttpBasicOverHttpStatus.Failed;
                }

                if (!(queryKey.GetValue(BasicAuthLevelValue) is int basicAuthLevel))
                {
                    Log.Warn("RegQueryValueExW(BasicAuthLevel) failed");
                    return HttpBasicOverHttpStatus.Failed;
                }

                if (!(queryKey.GetValue(ServerNotFoundCacheLifetimeValue) is int serverNotFoundCacheLifetime))
                {
                    Log.Warn("RegQueryValueExW(ServerNotFoundCacheLifeTimeInSec) failed");
                    return HttpBasicOverHttpStatus.Failed;
                }

                if (basicAuthLevel >= 2 && serverNotFoundCacheLifetime == 0)
                {
                    Log.Info("WebClient registry already allows HTTP WebDAV authentication and disables negative server cache");
                    return HttpBasicOverHttpStatus.Enabled;
                }
            }
            catch (UnauthorizedAccessException)
            {
                return HttpBasicOverHttpStatus.AccessDenied;
            }
            catch (Exception exception)
            {
                Log.Warn("Reading WebClient registry failed: " + exception.Message);
                return HttpBasicOverHttpStatus.Failed;
            }

            try
            {
                using RegistryKey? updateKey = Registry.LocalMachine.OpenSubKey(WebClientParametersKey, true);
                if (updateKey == null)
                {
                    Log.Warn("RegOpenKeyExW(WebClient Parameters, update) failed");
                    return HttpBasicOverHttpStatus.Failed;
                }

                updateKey.SetValue(BasicAuthLevelValue, 2, RegistryValueKind.DWord);
                updateKey.SetValue(ServerNotFoundCacheLifetimeValue, 0, RegistryValueKind.DWord);
            }
            catch (UnauthorizedAccessException)
            {
                return HttpBasicOverHttpStatus.AccessDenied;
            }
            catch (Exception exception)
            {
                Log.Warn("Updating WebClient registry failed: " + exception.Message);
                return HttpBasicOverHttpStatus.Failed;
            }

            Log.Info("Updated WebClient registry for HTTP WebDAV authentication and disabled negative server cache");
            if (!RestartWebClientService())
            {
                Log.Warn("WebClient registry was updated but WebClient could not be restarted automatically");
                return HttpBasicOverHttpStatus.AccessDenied;
            }

            Log.Info("WebClient registry was updated and WebClient was restarted");
            return HttpBasicOverHttpStatus.Updated;
        }

        private static bool RestartWebClientService()
        {
            IntPtr scm = OpenSCManager(null, null, ScManagerConnect);
            if (scm == IntPtr.Zero)
            {
                Log.Warn("OpenSCManagerW failed while restarting WebClient with error " + Marshal.GetLastWin32Error());
                return false;
            }

            try
            {
                IntPtr service = OpenService(
                    scm,
                    WebClientServiceName,
                    ServiceQueryStatus | ServiceStart | ServiceStop);

                if (service == IntPtr.Zero)
                {
                    Log.Warn("OpenServiceW(WebClient) failed while restarting with error " + Marshal.GetLastWin32Error());
                    return false;
                }

                try
                {
                    if (QueryRunningState(service, out ServiceStatusProcess status) &&
                        status.CurrentState != ServiceStopped)
                    {
                        if (!ControlService(service, ServiceControlStop, out _))
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (error != ErrorServiceNotActive)
                            {
                                Log.Warn("ControlService(WebClient, STOP) failed with error " + error);
                                return false;
                            }
                        }
                        else if (!WaitForState(
                                     service,
                                     ServiceStopped,
                                     ServiceStopPending,
                                     "Timed out while waiting for WebClient service to stop"))
                        {
                            return false;
                        }
                    }

                    if (!StartService(service, 0, IntPtr.Zero))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != ErrorServiceAlreadyRunning)
                        {
                            Log.Warn("StartServiceW(WebClient) failed after configuration update with error " + error);
                            return false;
                        }
                    }

                    return WaitForState(
                        service,
                        ServiceRunning,
                        ServiceStartPending,
                        "Timed out while waiting for WebClient service to start");
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }

        /// <summary>
        /// Ожидает переход службы в целевое состояние, измеряя timeout монотонным Stopwatch.
        /// </summary>
        private static bool WaitForState(
            IntPtr service,
            uint targetState,
            uint pendingState,
            string timeoutMessage)
        {
            long startTimestamp = Stopwatch.GetTimestamp();
            long timeoutTicks = (long)(StateTimeout.TotalSeconds * Stopwatch.Frequency);

            while (Stopwatch.GetTimestamp() - startTimestamp < timeoutTicks)
            {
                if (!QueryRunningState(service, out ServiceStatusProcess status))
                {
                    return false;
                }

                if (status.CurrentState == targetState)
                {
                    return true;
                }

                if (status.CurrentState != pendingState)
                {
                    return false;
                }

                Thread.Sleep(PollInterval);
            }

            Log.Warn(timeoutMessage);
            return false;
        }

        private static bool QueryRunningState(IntPtr service, out ServiceStatusProcess status)
        {
            uint size = (uint)Marshal.SizeOf<ServiceStatusProcess>();
            bool ok = QueryServiceStatusEx(service, ScStatusProcessInfo, out status, size, out _);
            if (!ok)
            {
                Log.Warn("QueryServiceStatusEx(WebClient) failed with error " + Marshal.GetLastWin32Error());
            }

            return ok;
        }
    }
}
