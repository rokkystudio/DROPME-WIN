using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DROPME.Services
{
    internal readonly struct DeviceApprovalResult
    {
        public DeviceApprovalResult(bool allowed, bool remember)
        {
            Allowed = allowed;
            Remember = remember;
        }

        public bool Allowed { get; }
        public bool Remember { get; }
    }

    /// <summary>
    /// Показывает синхронный WPF-диалог первого входящего запроса с именем, IP и opaque ID устройства.
    /// </summary>
    internal static class DeviceApprovalService
    {
        public static DeviceApprovalResult Request(
            string deviceName,
            string ip,
            string deviceId,
            string purpose,
            string itemName)
        {
            if (Application.Current?.Dispatcher == null)
            {
                return new DeviceApprovalResult(false, false);
            }

            return Application.Current.Dispatcher.Invoke(() =>
            {
                bool allowed = false;
                bool remember = false;

                Window dialog = new Window
                {
                    Title = "DROPME",
                    Width = 440,
                    SizeToContent = SizeToContent.Height,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ShowInTaskbar = true,
                    Background = (Brush)Application.Current.Resources["BackgroundBrush"]
                };

                StackPanel content = new StackPanel
                {
                    Margin = new Thickness(20)
                };

                content.Children.Add(new TextBlock
                {
                    Text = purpose == "share"
                        ? deviceName + " хочет отправить файлы"
                        : deviceName + " хочет получить доступ к файлам",
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                content.Children.Add(new TextBlock
                {
                    Text = "IP: " + ip + Environment.NewLine + "ID: " + deviceId,
                    Margin = new Thickness(0, 12, 0, 0),
                    Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
                    TextWrapping = TextWrapping.Wrap
                });

                if (!string.IsNullOrWhiteSpace(itemName))
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = "Объект: " + itemName,
                        Margin = new Thickness(0, 8, 0, 0),
                        TextWrapping = TextWrapping.Wrap
                    });
                }

                CheckBox rememberCheckBox = new CheckBox
                {
                    Content = "Запомнить решение",
                    Margin = new Thickness(0, 16, 0, 0),
                    Style = Application.Current.TryFindResource("OptionCheckBoxStyle") as Style
                };
                content.Children.Add(rememberCheckBox);

                Grid buttons = new Grid
                {
                    Margin = new Thickness(0, 18, 0, 0)
                };
                buttons.ColumnDefinitions.Add(new ColumnDefinition());
                buttons.ColumnDefinitions.Add(new ColumnDefinition());

                Button denyButton = new Button
                {
                    Content = "Отклонить",
                    Margin = new Thickness(0, 0, 6, 0),
                    MinHeight = 38,
                    Style = Application.Current.TryFindResource("ActionButtonStyle") as Style
                };
                denyButton.Click += (_, _) =>
                {
                    allowed = false;
                    remember = rememberCheckBox.IsChecked == true;
                    dialog.DialogResult = false;
                };
                buttons.Children.Add(denyButton);

                Button allowButton = new Button
                {
                    Content = "Разрешить",
                    Margin = new Thickness(6, 0, 0, 0),
                    MinHeight = 38,
                    Style = Application.Current.TryFindResource("ActionButtonStyle") as Style
                };
                allowButton.Click += (_, _) =>
                {
                    allowed = true;
                    remember = rememberCheckBox.IsChecked == true;
                    dialog.DialogResult = true;
                };
                Grid.SetColumn(allowButton, 1);
                buttons.Children.Add(allowButton);

                content.Children.Add(buttons);
                dialog.Content = content;
                dialog.ShowDialog();

                return new DeviceApprovalResult(allowed, remember);
            });
        }
    }
}
