using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DROPME.Clients;
using DROPME.Server;
using DROPME.Services;
using DROPME.Utils;
using FlagsPack;
using NeoUI;
using Forms = System.Windows.Forms;

namespace DROPME
{
    /// <summary>
    /// Отображает состояние DROPME, подключенные Android-устройства и команды управления приложением.
    /// </summary>
    public partial class MainWindow : Window, IDisposable
    {
        private readonly ClientManager clientManager_;
        private readonly WifiDropServer server_;
        private readonly AutoStart autoStart_;
        private readonly DispatcherTimer refreshTimer_;
        private readonly Forms.NotifyIcon notifyIcon_;
        private readonly NeoTrayIconInteraction trayInteraction_;
        private bool closeApplication_;
        private bool disposed_;
        private string deviceMenuSignature_ = string.Empty;

        internal MainWindow(ClientManager clientManager, WifiDropServer server, AutoStart autoStart, bool serverStarted)
        {
            clientManager_ = clientManager;
            server_ = server;
            autoStart_ = autoStart;

            InitializeComponent();

            notifyIcon_ = new Forms.NotifyIcon
            {
                Text = "DROPME",
                Visible = true,
                Icon = LoadNotifyIcon()
            };
            trayInteraction_ = new NeoTrayIconInteraction(notifyIcon_);
            trayInteraction_.LeftDoubleClick += NotifyIcon_OnLeftDoubleClick;

            refreshTimer_ = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            refreshTimer_.Tick += RefreshTimer_OnTick;
            refreshTimer_.Start();

            ThemeService.ThemeChanged += ThemeService_ThemeChanged;
            LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;

            WpfThemeService.Apply(Application.Current);
            ApplyLocalization();
            ApplyThemeState();
            RefreshDevices();
            RebuildNotifyMenu();

            if (!serverStarted)
            {
                ServerDescriptionText.Text = LocalizationService.Text("status_stopped");
            }
        }

        private static System.Drawing.Icon LoadNotifyIcon()
        {
            string? processPath = Process.GetCurrentProcess().MainModule?.FileName;
            System.Drawing.Icon? icon = processPath == null ? null : System.Drawing.Icon.ExtractAssociatedIcon(processPath);
            return icon ?? System.Drawing.SystemIcons.Application;
        }

        private void RefreshTimer_OnTick(object? sender, EventArgs e)
        {
            RefreshDevices();
        }

        /// <summary>
        /// Перестраивает визуальный список по snapshot из ClientManager.
        /// </summary>
        private void RefreshDevices()
        {
            List<AndroidClient> clients = clientManager_.ListClients();
            string deviceMenuSignature = BuildDeviceMenuSignature(clients);
            if (!string.Equals(deviceMenuSignature_, deviceMenuSignature, StringComparison.Ordinal))
            {
                deviceMenuSignature_ = deviceMenuSignature;
                RebuildNotifyMenu();
            }

            DevicesPanel.Children.Clear();

            if (clients.Count == 0)
            {
                DevicesPanel.Children.Add(new TextBlock
                {
                    Text = LocalizationService.Text("no_devices"),
                    Foreground = (System.Windows.Media.Brush)Application.Current.Resources["MutedTextBrush"],
                    Margin = new Thickness(2, 8, 2, 8)
                });
                return;
            }

            foreach (AndroidClient client in clients)
            {
                Border card = new Border
                {
                    Background = (System.Windows.Media.Brush)Application.Current.Resources["SurfaceBrush"],
                    BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["BorderBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                Grid grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                StackPanel text = new StackPanel();
                text.Children.Add(new TextBlock
                {
                    Text = client.DriveName,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 13
                });
                text.Children.Add(new TextBlock
                {
                    Text = client.RemoteIp + "  •  " +
                           LocalizationService.Text("endpoint_ready") + ": " +
                           (client.MountReady ? LocalizationService.Text("yes") : LocalizationService.Text("no")) +
                           "  •  " + LocalizationService.Text("drive") + ": " +
                           (client.DriveLetter.Length == 0 ? "—" : client.DriveLetter + ":"),
                    Foreground = (System.Windows.Media.Brush)Application.Current.Resources["MutedTextBrush"],
                    Margin = new Thickness(0, 4, 0, 0),
                    FontSize = 11
                });

                if (client.MountError.Length > 0)
                {
                    text.Children.Add(new TextBlock
                    {
                        Text = client.MountError,
                        Foreground = (System.Windows.Media.Brush)Application.Current.Resources["MutedTextBrush"],
                        Margin = new Thickness(0, 4, 0, 0),
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap
                    });
                }

                grid.Children.Add(text);

                Button open = new Button
                {
                    Content = LocalizationService.Text("device_open"),
                    Style = (Style)FindResource("ActionButtonStyle"),
                    Tag = client.ClientId,
                    IsEnabled = CanOpenClientLocation(client),
                    Margin = new Thickness(12, 0, 0, 0)
                };
                open.Click += DeviceOpenButton_OnClick;
                Grid.SetColumn(open, 1);
                grid.Children.Add(open);

                card.Child = grid;
                DevicesPanel.Children.Add(card);
            }
        }

        /// <summary>
        /// Формирует стабильную сигнатуру состояния устройств, влияющего на tray menu.
        /// </summary>
        private static string BuildDeviceMenuSignature(List<AndroidClient> clients)
        {
            System.Text.StringBuilder signature = new System.Text.StringBuilder();
            foreach (AndroidClient client in clients)
            {
                signature.Append(client.ClientId).Append('|')
                    .Append(client.DriveName).Append('|')
                    .Append(client.DriveLetter).Append('|')
                    .Append(client.MountReady ? '1' : '0').Append('|')
                    .Append(client.WebDavHost).Append('|')
                    .Append(client.WebDavPort).Append(';');
            }
            return signature.ToString();
        }

        private void DeviceOpenButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string clientId)
            {
                OpenClientDrive(clientId);
            }
        }

        private static bool CanOpenClientLocation(AndroidClient client)
        {
            return client.DriveLetter.Length > 0 ||
                   (client.MountReady && client.WebDavHost.Length > 0 && client.WebDavPort > 0);
        }

        /// <summary>
        /// Открывает mounted drive либо WebDAV/browser path выбранного клиента и показывает tray-уведомление при недоступном пути.
        /// </summary>
        private void OpenClientDrive(string clientId)
        {
            foreach (AndroidClient client in clientManager_.ListClients())
            {
                if (client.ClientId != clientId)
                {
                    continue;
                }

                if (client.DriveLetter.Length > 0)
                {
                    if (!TryOpen(client.DriveLetter + ":\\", "client drive"))
                    {
                        ShowNotification(LocalizationService.Text("drive_open_failed"), Forms.ToolTipIcon.Error);
                    }
                    return;
                }

                if (!client.MountReady)
                {
                    ShowNotification(LocalizationService.Text("endpoint_not_ready"), Forms.ToolTipIcon.Info);
                    return;
                }

                bool webClientReady = WebClientService.EnsureRunning();
                string webDavPath = BuildClientOpenPath(client);
                string browserUrl = BuildClientBrowserUrl(client);

                if (webDavPath.Length == 0 && browserUrl.Length == 0)
                {
                    ShowNotification(LocalizationService.Text("client_path_unavailable"), Forms.ToolTipIcon.Info);
                    return;
                }

                if (webDavPath.Length > 0 && TryOpen(webDavPath, "client WebDAV path"))
                {
                    return;
                }

                if (browserUrl.Length > 0 && TryOpen(browserUrl, "client browser URL"))
                {
                    if (client.MountError.Length > 0)
                    {
                        ShowNotification(client.MountError, Forms.ToolTipIcon.Warning);
                    }
                    else if (!webClientReady)
                    {
                        ShowNotification(LocalizationService.Text("webclient_browser_fallback"), Forms.ToolTipIcon.Info);
                    }
                    return;
                }

                if (client.MountError.Length > 0)
                {
                    ShowNotification(client.MountError, Forms.ToolTipIcon.Error);
                }
                else
                {
                    ShowNotification(
                        LocalizationService.Text(webClientReady ? "client_open_failed" : "webclient_and_browser_failed"),
                        Forms.ToolTipIcon.Error);
                }
                return;
            }

            ShowNotification(LocalizationService.Text("client_disconnected"), Forms.ToolTipIcon.Info);
        }

        private static string BuildClientOpenPath(AndroidClient client)
        {
            if (client.DriveLetter.Length > 0)
            {
                return client.DriveLetter + ":\\";
            }
            if (client.WebDavHost.Length == 0 || client.WebDavPort <= 0)
            {
                return string.Empty;
            }

            string basePath = client.WebDavBasePath.Replace('/', '\\').Trim('\\');
            string path = "\\\\" + client.WebDavHost + "@" + client.WebDavPort + "\\DavWWWRoot\\";
            if (basePath.Length > 0)
            {
                path += basePath + "\\";
            }
            return path;
        }

        private static string BuildClientBrowserUrl(AndroidClient client)
        {
            if (client.WebDavHost.Length == 0 || client.WebDavPort <= 0)
            {
                return string.Empty;
            }

            string basePath = client.WebDavBasePath;
            string url = "http://" + client.WebDavHost + ":" + client.WebDavPort;
            if (basePath.Length == 0)
            {
                return url + "/";
            }
            if (!basePath.StartsWith("/", StringComparison.Ordinal))
            {
                url += "/";
            }
            url += basePath;
            if (!url.EndsWith("/", StringComparison.Ordinal))
            {
                url += "/";
            }
            return url;
        }

        private static bool TryOpen(string target, string logContext)
        {
            try
            {
                Log.Info("Opening path [" + logContext + "]: " + target);
                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception exception)
            {
                Log.Error("Shell open failed for " + logContext + ": " + exception.Message);
                return false;
            }
        }

        /// <summary>
        /// Подготавливает дневную папку входящих и открывает её через Windows shell.
        /// </summary>
        private void OpenIncomingFolder()
        {
            try
            {
                string folder = DesktopFolders.EnsureIncomingFolder();
                if (!TryOpen(folder, "incoming folder"))
                {
                    ShowNotification(LocalizationService.Text("incoming_open_failed"), Forms.ToolTipIcon.Error);
                }
            }
            catch (Exception exception)
            {
                Log.Error("Failed to open incoming folder: " + exception.Message);
                ShowNotification(LocalizationService.Text("incoming_prepare_failed"), Forms.ToolTipIcon.Error);
            }
        }

        /// <summary>
        /// Applies the requested Windows autostart state and restores the checkbox to the actual
        /// registry state if the update fails.
        /// </summary>
        private void SetAutostart(bool enabled)
        {
            if (!autoStart_.SetEnabled(enabled))
            {
                Log.Error("Failed to update autostart registry value");
                ShowNotification(LocalizationService.Text("autostart_failed"), Forms.ToolTipIcon.Error);
                UpdateAutostartCheckBox();
                return;
            }

            Log.Info(enabled ? "Autostart enabled" : "Autostart disabled");
            UpdateAutostartCheckBox();
        }

        private void OpenIncomingButton_OnClick(object sender, RoutedEventArgs e)
        {
            OpenIncomingFolder();
        }

        private void AutostartCheckBox_OnClick(object sender, RoutedEventArgs e)
        {
            SetAutostart(AutostartCheckBox.IsChecked == true);
        }

        /// <summary>
        /// Открывает меню выбора системного, английского или русского языка с флагами.
        /// </summary>
        private void LanguageButton_OnClick(object sender, RoutedEventArgs e)
        {
            TitleBar.OpenLanguageMenu(WpfLanguageMenuService.CreateLanguageMenu());
        }

        private void ThemeButton_OnClick(object sender, RoutedEventArgs e)
        {
            ThemeService.ToggleTheme();
        }

        private void CloseButton_OnClick(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void Window_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!closeApplication_)
            {
                e.Cancel = true;
                Hide();
            }
        }

        /// <summary>
        /// Handles a left-button tray double click: with no clients opens incoming files, with one
        /// client opens that device, and with multiple clients leaves selection to the tray menu.
        /// </summary>
        private void NotifyIcon_OnLeftDoubleClick(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(delegate
            {
                List<AndroidClient> clients = clientManager_.ListClients();
                if (clients.Count == 0)
                {
                    OpenIncomingFolder();
                    return;
                }

                if (clients.Count == 1)
                {
                    OpenClientDrive(clients[0].ClientId);
                    return;
                }

                Log.Info("Multiple clients are connected; tray double click keeps the devices window hidden");
            });
        }

        private void ThemeService_ThemeChanged(object? sender, EventArgs e)
        {
            WpfThemeService.Apply(Application.Current);
            ApplyThemeState();
            RefreshDevices();
            RebuildNotifyMenu();
        }

        private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
        {
            ApplyLocalization();
            RefreshDevices();
            RebuildNotifyMenu();
        }

        /// <summary>
        /// Применяет локализованные строки к основной панели и обновляет графические кнопки заголовка.
        /// </summary>
        private void ApplyLocalization()
        {
            TitleBar.TitleText = LocalizationService.Text("app_title");
            TitleBar.SubtitleText = server_.IsRunning
                ? LocalizationService.Text("status_running")
                : LocalizationService.Text("status_stopped");
            ServerTitleText.Text = LocalizationService.Text("status_title");
            ServerDescriptionText.Text = server_.IsRunning
                ? LocalizationService.Text("status_running")
                : LocalizationService.Text("status_stopped");
            DevicesTitleText.Text = LocalizationService.Text("devices");
            OpenIncomingButton.Content = LocalizationService.Text("open_incoming");
            UpdateAutostartCheckBox();
            UpdateHeaderButtons();
        }

        /// <summary>
        /// Обновляет иконки языка и текущей темы после изменения темы.
        /// </summary>
        private void ApplyThemeState()
        {
            UpdateHeaderButtons();
        }

        /// <summary>
        /// Показывает флаг фактического языка, иконку текущей Light/Dark темы и локализованные подсказки.
        /// </summary>
        private void UpdateHeaderButtons()
        {
            TitleBar.LanguageToolTip = LocalizationService.Text("tooltip_language");
            TitleBar.ThemeToolTip = LocalizationService.Text("tooltip_theme");
            TitleBar.CloseToolTip = LocalizationService.Text("tooltip_close");

            TitleBar.LanguageIconSource =
                CountryFlags.Load(WpfLanguageMenuService.GetCurrentLanguageFlagCountryCode());

            string themePath = NeoThemePalettes.IsDarkTheme(ThemeService.CurrentTheme)
                ? NeoAssetPaths.ThemeDark
                : NeoAssetPaths.ThemeLight;
            TitleBar.ThemeIconSource = NeoAssetService.LoadImage(themePath);
        }

        private void UpdateAutostartCheckBox()
        {
            AutostartCheckBox.Content = LocalizationService.Text("autostart");
            AutostartCheckBox.IsChecked = autoStart_.IsEnabled();
        }

        /// <summary>
        /// Создаёт локализованное tray menu через общий NeoUI builder.
        /// </summary>
        private void RebuildNotifyMenu()
        {
            List<AndroidClient> clients = clientManager_.ListClients();
            Forms.ContextMenuStrip menu = NeoTrayMenuBuilder.Create(builder =>
            {
                builder.AddItem(LocalizationService.Text("open_incoming"), delegate
                {
                    Dispatcher.Invoke(OpenIncomingFolder);
                });

                builder.AddItem(LocalizationService.Text("devices"), delegate
                {
                    Dispatcher.Invoke(ShowMainWindow);
                });

                if (clients.Count > 0)
                {
                    builder.AddSeparator();
                    foreach (AndroidClient client in clients)
                    {
                        AndroidClient current = client;
                        builder.AddItem(
                            current.DriveName,
                            delegate
                            {
                                Dispatcher.Invoke(delegate { OpenClientDrive(current.ClientId); });
                            },
                            CanOpenClientLocation(current));
                    }
                }

                builder.AddSeparator();
                builder.AddItem(LocalizationService.Text("exit"), delegate
                {
                    Dispatcher.Invoke(CloseApplication);
                });
            });

            Forms.ContextMenuStrip? previous = notifyIcon_.ContextMenuStrip;
            notifyIcon_.ContextMenuStrip = menu;
            previous?.Dispose();
        }

        /// <summary>
        /// Показывает и активирует основную XAML-панель подключенных устройств.
        /// </summary>
        private void ShowMainWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        /// <summary>
        /// Показывает системное tray-уведомление DROPME.
        /// </summary>
        internal void ShowNotification(string message, Forms.ToolTipIcon icon)
        {
            notifyIcon_.ShowBalloonTip(5000, "DROPME", message, icon);
        }

        private void CloseApplication()
        {
            if (closeApplication_)
            {
                return;
            }

            closeApplication_ = true;
            Close();
            Application.Current.Shutdown();
        }

        public void Dispose()
        {
            if (disposed_)
            {
                return;
            }
            disposed_ = true;

            refreshTimer_.Stop();
            refreshTimer_.Tick -= RefreshTimer_OnTick;
            ThemeService.ThemeChanged -= ThemeService_ThemeChanged;
            LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;

            trayInteraction_.Dispose();
            notifyIcon_.Visible = false;
            notifyIcon_.ContextMenuStrip?.Dispose();
            notifyIcon_.Dispose();
        }
    }
}
