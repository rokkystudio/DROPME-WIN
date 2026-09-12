using System;
using System.Windows.Controls;
using NeoUI;

namespace DROPME.Services
{
    /// <summary>
    /// Формирует меню выбора языка DROPME через общий NeoUI language-menu API.
    /// </summary>
    internal static class WpfLanguageMenuService
    {
        /// <summary>
        /// Создаёт меню системного, английского и русского языка с общими NeoUI-флагами.
        /// </summary>
        public static ContextMenu CreateLanguageMenu()
        {
            NeoLanguageMenuBuilder builder = new NeoLanguageMenuBuilder();

            builder.AddLanguage(
                LocalizationService.AutomaticLanguage,
                LocalizationService.SystemLanguageDisplayText(),
                GetLanguageFlagCountryCode(LocalizationService.DetectedLanguage),
                LocalizationService.IsAutomaticLanguageSelection,
                language => LocalizationService.CurrentLanguage = language);

            builder.AddSeparator();

            builder.AddLanguage(
                LocalizationService.EnglishLanguage,
                "English",
                "US",
                string.Equals(
                    LocalizationService.SelectedLanguage,
                    LocalizationService.EnglishLanguage,
                    StringComparison.Ordinal),
                language => LocalizationService.CurrentLanguage = language);

            builder.AddLanguage(
                LocalizationService.RussianLanguage,
                "Русский",
                "RU",
                string.Equals(
                    LocalizationService.SelectedLanguage,
                    LocalizationService.RussianLanguage,
                    StringComparison.Ordinal),
                language => LocalizationService.CurrentLanguage = language);

            return builder.Build();
        }

        /// <summary>
        /// Возвращает код флага фактически используемого языка, включая режим auto.
        /// </summary>
        public static string GetCurrentLanguageFlagCountryCode()
        {
            return GetLanguageFlagCountryCode(LocalizationService.CurrentLanguage);
        }

        private static string GetLanguageFlagCountryCode(string language)
        {
            return LocalizationService.IsRussianLanguage(language) ? "RU" : "US";
        }
    }
}
