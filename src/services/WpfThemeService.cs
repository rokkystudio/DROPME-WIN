using System;
using System.Windows;
using System.Windows.Media;
using NeoUI;

namespace DROPME.Services
{
    /// <summary>
    /// Применяет каноническую палитру NeoUI к динамическим WPF-ресурсам приложения.
    /// </summary>
    internal static class WpfThemeService
    {
        public static void Apply(Application application)
        {
            NeoThemePalette palette = NeoThemePalettes.Get(ThemeService.CurrentTheme);

            SetBrush(application, "BackgroundBrush", palette.Background);
            SetBrush(application, "SurfaceBrush", palette.Surface);
            SetBrush(application, "RaisedSurfaceBrush", palette.SurfaceRaised);
            SetBrush(application, "TextBrush", palette.Text);
            SetBrush(application, "MutedTextBrush", palette.MutedText);
            SetBrush(application, "SoftTextBrush", palette.SoftText);
            SetBrush(application, "DisabledTextBrush", palette.DisabledText);
            SetBrush(application, "BorderBrush", palette.Border);
            SetBrush(application, "PrimaryBrush", palette.Primary);
            SetBrush(application, "DangerBrush", palette.Error);
            SetBrush(application, "SuccessBrush", palette.Success);
            SetBrush(application, "TitleBarBackgroundBrush", palette.TitleBarBackground);
            SetBrush(application, "TitleBarBorderBrush", palette.TitleBarBorder);
            SetBrush(application, "ButtonHoverBackgroundBrush", WithAlpha(palette.Primary, 0x14));
            SetBrush(application, "ButtonPressedBackgroundBrush", WithAlpha(palette.Primary, 0x26));
        }

        private static void SetBrush(Application application, string key, string value)
        {
            application.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        }

        private static string WithAlpha(string value, byte alpha)
        {
            string rgb = value.StartsWith("#", StringComparison.Ordinal) ? value.Substring(1) : value;
            if (rgb.Length == 8)
            {
                rgb = rgb.Substring(2);
            }
            return "#" + alpha.ToString("X2") + rgb;
        }
    }
}
