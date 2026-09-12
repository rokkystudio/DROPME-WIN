using System;
using NeoUI;

namespace DROPME.Services
{
    /// <summary>
    /// Управляет выбранной темой приложения и уведомляет WPF-слой о её переключении.
    /// </summary>
    internal static class ThemeService
    {
        public const string LightTheme = NeoThemePalettes.LightTheme;
        public const string DarkTheme = NeoThemePalettes.DarkTheme;

        public static event EventHandler? ThemeChanged;

        public static string CurrentTheme
        {
            get { return NeoThemePalettes.NormalizeTheme(AppSettings.Theme); }
            set
            {
                string normalized = NeoThemePalettes.NormalizeTheme(value);
                if (string.Equals(CurrentTheme, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                AppSettings.Theme = normalized;
                AppSettings.Save();
                ThemeChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Переключает Light/Dark тему.
        /// </summary>
        public static void ToggleTheme()
        {
            CurrentTheme = NeoThemePalettes.IsDarkTheme(CurrentTheme) ? LightTheme : DarkTheme;
        }
    }
}
