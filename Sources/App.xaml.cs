using System;
using System.Linq;
using System.Windows;
using DROPME.Clients;
using DROPME.Server;
using DROPME.Services;
using DROPME.Utils;

namespace DROPME
{
    /// <summary>
    /// Координирует настройки, сервер DROPME, главное XAML-окно и завершение приложения.
    /// </summary>
    public partial class App : Application
    {
        private ClientManager? clientManager_;
        private WifiDropServer? server_;
        private MainWindow? mainWindow_;

        private void App_OnStartup(object sender, StartupEventArgs e)
        {
            AppSettings.Load();
            WpfThemeService.Apply(this);

            Log.Initialize();
            Log.Info("DROPME starting");
            Log.Info("Drive backend: WebDAV redirector");
            WebClientService.EnsureRunning();

            clientManager_ = new ClientManager();
            server_ = new WifiDropServer(clientManager_);
            bool serverStarted = server_.Start();

            mainWindow_ = new MainWindow(clientManager_, server_, new AutoStart(), serverStarted);
            MainWindow = mainWindow_;

            if (!serverStarted)
            {
                Log.Error("Failed to start server");
                mainWindow_.ShowNotification(
                    LocalizationService.Text("server_start_failed"),
                    System.Windows.Forms.ToolTipIcon.Error);
                Shutdown(1);
                return;
            }

            bool startHidden = e.Args.Any(argument =>
                string.Equals(argument, "--tray", StringComparison.OrdinalIgnoreCase));

            if (!startHidden)
            {
                mainWindow_.Show();
            }
        }

        /// <summary>
        /// Останавливает сервер, размонтирует клиентские диски и завершает WPF application.
        /// </summary>
        public void ExitApplication()
        {
            Shutdown();
        }

        /// <summary>
        /// Освобождает UI, сервер, WebDAV mounts и логгер при завершении приложения.
        /// </summary>
        private void App_OnExit(object sender, ExitEventArgs e)
        {
            mainWindow_?.Dispose();
            mainWindow_ = null;

            server_?.Dispose();
            server_ = null;

            Log.Info("DROPME stopped");
            Log.Shutdown();
        }
    }
}
