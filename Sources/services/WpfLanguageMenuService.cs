using System;

namespace DROPME.Services
{
    /// <summary>
    /// Переключает два поддерживаемых языка DROPME и возвращает флаг текущего языка.
    /// При двух вариантах отдельное popup-меню не используется.
    /// </summary>
    internal static class WpfLanguageMenuService
    {
        /// <summary>
        /// Переключает интерфейс между English и Русский.
        /// </summary>
        public static void ToggleLanguage()
        {
            LocalizationService.CurrentLanguage = string.Equals(
                LocalizationService.CurrentLanguage,
                LocalizationService.RussianLanguage,
                StringComparison.Ordinal)
                ? LocalizationService.EnglishLanguage
                : LocalizationService.RussianLanguage;
        }

        /// <summary>
        /// Возвращает код флага сохранённого языка приложения.
        /// </summary>
        public static string GetCurrentLanguageFlagCountryCode()
        {
            return LocalizationService.IsRussianLanguage(LocalizationService.CurrentLanguage)
                ? "RU"
                : "US";
        }
    }
}
