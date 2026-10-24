using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DROPME.Clients;
using DROPME.Devices;
using DROPME.Services;
using DROPME.Utils;
using Microsoft.Win32;

namespace DROPME
{
    public partial class MainWindow
    {
        private const int RemoteDeviceMissesBeforeRemoval = 6;

        private sealed class RemoteDeviceCacheEntry
        {
            public RemoteDevice Device { get; set; } = null!;
            public int ConsecutiveMisses { get; set; }
        }

        private readonly RemoteDeviceScanner remoteDeviceScanner_ = new RemoteDeviceScanner();
        private readonly RemoteDeviceShareClient remoteDeviceShareClient_ = new RemoteDeviceShareClient();
        private readonly Dictionary<string, RemoteDeviceCacheEntry> remoteDeviceCache_ =
            new Dictionary<string, RemoteDeviceCacheEntry>(StringComparer.Ordinal);
        private RemoteDeviceConnector? remoteDeviceConnector_;
        private List<RemoteDevice> remoteDevices_ = new List<RemoteDevice>();
        private CancellationTokenSource? remoteScanCancellation_;
        private DateTime nextRemoteScanUtc_ = DateTime.MinValue;
        private bool remoteScanInProgress_;

        /// <summary>
        /// Подготавливает редактируемую identity Windows-устройства, списки доверия и периодический поиск устройств.
        /// </summary>
        private void InitializeDeviceDiscovery()
        {
            remoteDeviceConnector_ = new RemoteDeviceConnector(clientManager_);
            DeviceNameTextBox.Text = AppSettings.DeviceName;
            DeviceServiceCheckBox.IsChecked = AppSettings.DeviceServiceEnabled;
            RefreshLocalDeviceIdentity();
            RefreshTrustLists();
            StartRemoteScanIfDue(force: true);
        }

        /// <summary>
        /// Запускает один фоновый поиск protocol v3 устройств не чаще заданного интервала.
        /// </summary>
        private void StartRemoteScanIfDue(bool force = false)
        {
            if (remoteScanInProgress_ || (!force && DateTime.UtcNow < nextRemoteScanUtc_))
            {
                return;
            }

            remoteScanInProgress_ = true;
            nextRemoteScanUtc_ = DateTime.UtcNow.AddSeconds(10);
            NetworkScanStatusText.Text = LocalizationService.Text("network_scanning");

            remoteScanCancellation_?.Cancel();
            remoteScanCancellation_?.Dispose();
            remoteScanCancellation_ = new CancellationTokenSource();
            CancellationToken token = remoteScanCancellation_.Token;

            Task.Run(() => remoteDeviceScanner_.Scan(token), token)
                .ContinueWith(task =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        remoteScanInProgress_ = false;
                        if (task.IsCanceled || token.IsCancellationRequested)
                        {
                            return;
                        }

                        if (task.IsFaulted)
                        {
                            NetworkScanStatusText.Text = LocalizationService.Text("network_scan_failed");
                            return;
                        }

                        ApplyRemoteScanResult(task.Result);
                        NetworkScanStatusText.Text = string.Format(
                            LocalizationService.Text("network_devices_found"),
                            remoteDevices_.Count);
                        RenderNetworkDevices();
                    });
                }, CancellationToken.None);
        }

        /// <summary>
        /// Обновляет session-кэш устройств по результату полного scan.
        /// Найденные устройства обновляются сразу, временно пропавшие сохраняются до нескольких
        /// последовательных неудачных проходов, чтобы единичный timeout не вызывал мерцание UI.
        /// </summary>
        private void ApplyRemoteScanResult(IReadOnlyList<RemoteDevice> scannedDevices)
        {
            HashSet<string> seenDeviceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (RemoteDevice device in scannedDevices)
            {
                seenDeviceIds.Add(device.DeviceId);
                if (!remoteDeviceCache_.TryGetValue(device.DeviceId, out RemoteDeviceCacheEntry? entry))
                {
                    entry = new RemoteDeviceCacheEntry();
                    remoteDeviceCache_[device.DeviceId] = entry;
                }

                entry.Device = device;
                entry.ConsecutiveMisses = 0;
            }

            List<string> removeIds = new List<string>();
            foreach (KeyValuePair<string, RemoteDeviceCacheEntry> pair in remoteDeviceCache_)
            {
                if (seenDeviceIds.Contains(pair.Key))
                {
                    continue;
                }

                if (clientManager_.Contains(RemoteClientId(pair.Key)))
                {
                    pair.Value.ConsecutiveMisses = 0;
                    continue;
                }

                pair.Value.ConsecutiveMisses++;
                if (pair.Value.ConsecutiveMisses >= RemoteDeviceMissesBeforeRemoval)
                {
                    removeIds.Add(pair.Key);
                }
            }

            foreach (string deviceId in removeIds)
            {
                remoteDeviceCache_.Remove(deviceId);
            }

            remoteDevices_ = remoteDeviceCache_.Values
                .Select(entry => entry.Device)
                .OrderBy(device => device.DeviceName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(device => device.DeviceId, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Показывает найденные устройства с именем, текущим IP и постоянным opaque ID.
        /// </summary>
        private void RenderNetworkDevices()
        {
            NetworkDevicesPanel.Children.Clear();
            if (remoteDevices_.Count == 0)
            {
                NetworkDevicesPanel.Children.Add(CreateMutedText(LocalizationService.Text("network_no_devices")));
                return;
            }

            foreach (RemoteDevice device in remoteDevices_)
            {
                Border card = CreateDeviceCard();
                StackPanel content = new StackPanel();

                content.Children.Add(new TextBlock
                {
                    Text = device.DeviceName.Length > 0 ? device.DeviceName : device.Host,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                content.Children.Add(new TextBlock
                {
                    Text = "IP: " + device.Host,
                    Foreground = MutedBrush(),
                    Margin = new Thickness(0, 4, 0, 0),
                    FontSize = 11
                });
                content.Children.Add(new TextBlock
                {
                    Text = "ID: " + device.DeviceId,
                    Foreground = MutedBrush(),
                    Margin = new Thickness(0, 2, 0, 0),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                });
                content.Children.Add(new TextBlock
                {
                    Text = device.DeviceType + "  •  " +
                           (device.FileAccessAvailable
                               ? LocalizationService.Text("file_access_available")
                               : LocalizationService.Text("file_access_unavailable")),
                    Foreground = MutedBrush(),
                    Margin = new Thickness(0, 3, 0, 0),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                });

                WrapPanel actions = new WrapPanel
                {
                    Margin = new Thickness(0, 10, 0, -6)
                };

                bool connected = clientManager_.Contains(RemoteClientId(device.DeviceId));
                Button connectButton = new Button
                {
                    Content = LocalizationService.Text(connected ? "device_open" : "device_connect"),
                    Style = (Style)FindResource("ActionButtonStyle"),
                    Margin = new Thickness(0, 0, 8, 6),
                    IsEnabled = connected || device.FileAccessAvailable
                };
                RemoteDevice current = device;
                connectButton.Click += (_, _) => OpenOrConnectRemoteDevice(current);
                actions.Children.Add(connectButton);

                Button shareButton = new Button
                {
                    Content = LocalizationService.Text("device_send_files"),
                    Style = (Style)FindResource("ActionButtonStyle"),
                    Margin = new Thickness(0, 0, 8, 6),
                    IsEnabled = device.ShareAvailable
                };
                shareButton.Click += (_, _) => SendFilesToRemoteDevice(current);
                actions.Children.Add(shareButton);

                content.Children.Add(actions);
                card.Child = content;
                NetworkDevicesPanel.Children.Add(card);
            }
        }
        /// <summary>
        /// Открывает уже подключённое устройство или показывает окно ожидания подтверждения,
        /// запрашивает разрешение и монтирует файловую сессию.
        /// </summary>
        private async void OpenOrConnectRemoteDevice(RemoteDevice device)
        {
            string clientId = RemoteClientId(device.DeviceId);
            if (clientManager_.Contains(clientId))
            {
                OpenClientDrive(clientId);
                return;
            }

            if (remoteDeviceConnector_ == null)
            {
                return;
            }

            NetworkScanStatusText.Text = string.Format(
                LocalizationService.Text("device_connecting"),
                device.DeviceName);

            using CancellationTokenSource cancellation = new CancellationTokenSource();
            Window waitingDialog = CreateRemoteApprovalWaitingDialog(device.DeviceName, cancellation);
            waitingDialog.Show();
            RemoteDeviceConnector connector = remoteDeviceConnector_;
            try
            {
                AndroidClient client = await Task.Run(() =>
                    connector.Connect(
                        device,
                        cancellation.Token,
                        () => Dispatcher.BeginInvoke(new Action(() => CloseApprovalWaitingDialog(waitingDialog)))));

                RefreshDevices();
                RenderNetworkDevices();
                NetworkScanStatusText.Text = string.Format(
                    LocalizationService.Text("device_connected"),
                    device.DeviceName);
                OpenClientDrive(client.ClientId);
            }
            catch (OperationCanceledException)
            {
                NetworkScanStatusText.Text = LocalizationService.Text("device_operation_cancelled");
            }
            catch (Exception exception)
            {
                ShowNotification(exception.Message, System.Windows.Forms.ToolTipIcon.Error);
                NetworkScanStatusText.Text = LocalizationService.Text("device_connect_failed");
            }
            finally
            {
                CloseApprovalWaitingDialog(waitingDialog);
            }
        }

        /// <summary>
        /// Выбирает локальные файлы, показывает ожидание подтверждения и после разрешения
        /// отправляет выбранные файлы короткой Share-сессией.
        /// </summary>
        private async void SendFilesToRemoteDevice(RemoteDevice device)
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Multiselect = true,
                CheckFileExists = true,
                Title = LocalizationService.Text("device_send_files")
            };
            if (dialog.ShowDialog(this) != true || dialog.FileNames.Length == 0)
            {
                return;
            }

            string[] fileNames = dialog.FileNames;
            NetworkScanStatusText.Text = string.Format(
                LocalizationService.Text("share_sending"),
                device.DeviceName);

            using CancellationTokenSource cancellation = new CancellationTokenSource();
            Window waitingDialog = CreateRemoteApprovalWaitingDialog(device.DeviceName, cancellation);
            waitingDialog.Show();
            try
            {
                await Task.Run(() => remoteDeviceShareClient_.Send(
                    device,
                    fileNames,
                    cancellation.Token,
                    () => Dispatcher.BeginInvoke(new Action(() => CloseApprovalWaitingDialog(waitingDialog)))));

                NetworkScanStatusText.Text = string.Format(
                    LocalizationService.Text("share_sent"),
                    device.DeviceName);
            }
            catch (OperationCanceledException)
            {
                NetworkScanStatusText.Text = LocalizationService.Text("device_operation_cancelled");
            }
            catch (Exception exception)
            {
                ShowNotification(exception.Message, System.Windows.Forms.ToolTipIcon.Error);
                NetworkScanStatusText.Text = LocalizationService.Text("share_send_failed");
            }
            finally
            {
                CloseApprovalWaitingDialog(waitingDialog);
            }
        }

        /// <summary>
        /// Создаёт окно ожидания подтверждения. Кнопка отмены и системный крестик
        /// отменяют сетевой запрос через переданный CancellationTokenSource.
        /// </summary>
        private Window CreateRemoteApprovalWaitingDialog(
            string deviceName,
            CancellationTokenSource cancellation)
        {
            StackPanel content = new StackPanel
            {
                Margin = new Thickness(20)
            };
            content.Children.Add(new TextBlock
            {
                Text = string.Format(
                    LocalizationService.Text("device_waiting_approval"),
                    deviceName),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new TextBlock
            {
                Text = LocalizationService.Text("device_waiting_approval_hint"),
                Margin = new Thickness(0, 8, 0, 14),
                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new ProgressBar
            {
                IsIndeterminate = true,
                Height = 4
            });

            Button cancelButton = new Button
            {
                Content = LocalizationService.Text("cancel"),
                Style = (Style)FindResource("ActionButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0),
                MinWidth = 96
            };
            content.Children.Add(cancelButton);

            Window dialog = new Window
            {
                Owner = this,
                Title = "DROPME",
                Width = 390,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Content = content,
                Background = (Brush)Application.Current.Resources["BackgroundBrush"]
            };

            cancelButton.Click += (_, _) =>
            {
                cancellation.Cancel();
                CloseApprovalWaitingDialog(dialog);
            };
            dialog.Closing += (_, _) =>
            {
                if (dialog.Tag is true)
                {
                    return;
                }

                cancellation.Cancel();
                dialog.Tag = true;
            };
            return dialog;
        }

        /// <summary>
        /// Закрывает окно ожидания программно, не инициируя отмену уже разрешённой операции.
        /// </summary>
        private static void CloseApprovalWaitingDialog(Window dialog)
        {
            if (!dialog.IsVisible)
            {
                return;
            }

            dialog.Tag = true;
            dialog.Close();
        }
        /// <summary>
        /// Показывает кнопку сохранения только когда введённое имя отличается от сохранённого.
        /// </summary>
        private void DeviceNameTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSaveDeviceNameVisibility();
        }

        private void UpdateSaveDeviceNameVisibility()
        {
            if (SaveDeviceNameButton == null || DeviceNameTextBox == null)
            {
                return;
            }

            string editedName = DeviceNameTextBox.Text.Trim();
            SaveDeviceNameButton.Visibility =
                string.Equals(editedName, AppSettings.DeviceName, StringComparison.Ordinal)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
        }

        /// <summary>
        /// Сохраняет новое отображаемое имя без изменения device ID.
        /// </summary>
        private void SaveDeviceNameButton_OnClick(object sender, RoutedEventArgs e)
        {
            string name = DeviceNameTextBox.Text.Trim();
            if (name.Length == 0)
            {
                ShowNotification(LocalizationService.Text("device_name_required"), System.Windows.Forms.ToolTipIcon.Warning);
                return;
            }

            AppSettings.DeviceName = name;
            AppSettings.Save();
            RefreshLocalDeviceIdentity();
            UpdateSaveDeviceNameVisibility();
            StartRemoteScanIfDue(force: true);
        }

        /// <summary>
        /// Включает или выключает входящую сетевую доступность Windows-устройства.
        /// </summary>
        private void DeviceServiceCheckBox_OnClick(object sender, RoutedEventArgs e)
        {
            bool enabled = DeviceServiceCheckBox.IsChecked == true;
            if (enabled)
            {
                if (!server_.Start())
                {
                    DeviceServiceCheckBox.IsChecked = false;
                    AppSettings.DeviceServiceEnabled = false;
                    AppSettings.Save();
                    ShowNotification(LocalizationService.Text("server_start_failed"), System.Windows.Forms.ToolTipIcon.Error);
                    RefreshLocalDeviceIdentity();
                    return;
                }
            }
            else
            {
                server_.Stop();
            }

            AppSettings.DeviceServiceEnabled = enabled;
            AppSettings.Save();
            RefreshLocalDeviceIdentity();
            ApplyLocalization();
        }

        /// <summary>
        /// Обновляет имя, текущий локальный IPv4 и opaque ID этого устройства.
        /// </summary>
        private void RefreshLocalDeviceIdentity()
        {
            DeviceServiceCheckBox.IsChecked = AppSettings.DeviceServiceEnabled;
            if (!DeviceNameTextBox.IsKeyboardFocusWithin)
            {
                DeviceNameTextBox.Text = AppSettings.DeviceName;
            }

            string ip = GetPrimaryLocalIpv4();
            LocalDeviceIdText.Text = "ID: " + AppSettings.DeviceId;
            LocalDeviceIpText.Text = "IP: " + (ip.Length == 0 ? "—" : ip);
            UpdateSaveDeviceNameVisibility();
        }

        /// <summary>
        /// Отображает сохранённые разрешённые и заблокированные устройства по дате последней активности.
        /// </summary>
        private void RefreshTrustLists()
        {
            RenderTrustSection(
                AllowedDevicesPanel,
                server_.TrustStore.List(DeviceTrustDecision.Allowed),
                DeviceTrustDecision.Blocked);
            RenderTrustSection(
                BlockedDevicesPanel,
                server_.TrustStore.List(DeviceTrustDecision.Blocked),
                DeviceTrustDecision.Allowed);
        }

        private void RenderTrustSection(
            StackPanel panel,
            List<KnownDevice> devices,
            DeviceTrustDecision replacementDecision)
        {
            panel.Children.Clear();
            if (devices.Count == 0)
            {
                panel.Children.Add(CreateMutedText(LocalizationService.Text("trust_list_empty")));
                return;
            }

            foreach (KnownDevice device in devices)
            {
                Border card = CreateDeviceCard();
                Grid grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                StackPanel details = new StackPanel();
                details.Children.Add(new TextBlock
                {
                    Text = device.DeviceName,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12
                });
                details.Children.Add(new TextBlock
                {
                    Text = "IP: " + (device.LastIp.Length == 0 ? "—" : device.LastIp) +
                           "  •  ID: " + device.DeviceId,
                    Foreground = MutedBrush(),
                    Margin = new Thickness(0, 3, 0, 0),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap
                });
                details.Children.Add(new TextBlock
                {
                    Text = device.LastSeenUtc.ToLocalTime().ToString("g"),
                    Foreground = MutedBrush(),
                    Margin = new Thickness(0, 2, 0, 0),
                    FontSize = 10
                });
                grid.Children.Add(details);

                Button change = new Button
                {
                    Content = LocalizationService.Text(
                        replacementDecision == DeviceTrustDecision.Allowed
                            ? "trust_allow"
                            : "trust_block"),
                    Style = (Style)FindResource("ActionButtonStyle"),
                    Margin = new Thickness(12, 0, 0, 0)
                };
                KnownDevice current = device;
                change.Click += (_, _) =>
                {
                    server_.TrustStore.Remember(
                        current.DeviceId,
                        current.DeviceName,
                        current.LastIp,
                        replacementDecision);
                    RefreshTrustLists();
                };
                Grid.SetColumn(change, 1);
                grid.Children.Add(change);

                card.Child = grid;
                panel.Children.Add(card);
            }
        }

        private static string GetPrimaryLocalIpv4()
        {
            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                IPInterfaceProperties properties = networkInterface.GetIPProperties();
                if (!properties.GatewayAddresses.Any(gateway =>
                        gateway.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return address.Address.ToString();
                    }
                }
            }
            return string.Empty;
        }

        private static string RemoteClientId(string deviceId)
        {
            return "remote-" + deviceId;
        }

        private Border CreateDeviceCard()
        {
            return new Border
            {
                Background = (Brush)Application.Current.Resources["SurfaceBrush"],
                BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 7)
            };
        }

        private static TextBlock CreateMutedText(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = MutedBrush(),
                Margin = new Thickness(2, 6, 2, 6)
            };
        }

        private static Brush MutedBrush()
        {
            return (Brush)Application.Current.Resources["MutedTextBrush"];
        }

        /// <summary>
        /// Останавливает фоновый поиск перед закрытием окна.
        /// </summary>
        private void DisposeDeviceDiscovery()
        {
            remoteScanCancellation_?.Cancel();
            remoteScanCancellation_?.Dispose();
            remoteScanCancellation_ = null;
        }
    }
}
