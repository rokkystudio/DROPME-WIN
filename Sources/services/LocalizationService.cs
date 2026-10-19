using System;
using System.Collections.Generic;
using System.Globalization;

namespace DROPME.Services
{
    /// <summary>
    /// Предоставляет русские и английские строки интерфейса DROPME, поддерживает автоматический выбор
    /// по системному языку Windows и сохраняет выбранный пользователем режим языка.
    /// </summary>
    internal static class LocalizationService
    {
        public const string AutomaticLanguage = "auto";
        public const string EnglishLanguage = "en";
        public const string RussianLanguage = "ru";

        private static readonly Dictionary<string, Dictionary<string, string>> Texts =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
            {
                [RussianLanguage] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["app_title"] = "DROPME",
                    ["status_title"] = "Сервер DROPME",
                    ["my_device"] = "Мое устройство",
                    ["my_storage"] = "Мои хранилища",
                    ["storage_user_profile"] = "Профиль пользователя",
                    ["storage_add_folder"] = "Добавить папку",
                    ["storage_unavailable"] = "Недоступно",
                    ["main_header_subtitle"] = "Устройства и файлы",
                    ["status_running"] = "Сервер запущен и ожидает устройства",
                    ["status_stopped"] = "Сервер остановлен",
                    ["server_start_failed"] = "Не удалось запустить локальный сервер DROPME.",
                    ["devices"] = "Подключенные устройства",
                    ["no_devices"] = "Активных Android-устройств нет",
                    ["open_incoming"] = "Открыть папку входящих",
                    ["autostart"] = "Автозапуск",
                    ["use_winfsp"] = "Использовать WinFsp",
                    ["winfsp_installed_version"] = "Установлен • версия {0}",
                    ["winfsp_not_installed"] = "Не установлен",
                    ["language"] = "Язык",
                    ["theme"] = "Тема",
                    ["close"] = "Скрыть в трей",
                    ["exit"] = "Выход",
                    ["system_language"] = "Системный язык - {0}",
                    ["tooltip_language"] = "Язык интерфейса",
                    ["tooltip_theme"] = "Сменить тему",
                    ["tooltip_close"] = "Скрыть окно",
                    ["device_open"] = "Открыть",
                    ["endpoint_ready"] = "Endpoint готов",
                    ["drive"] = "Диск",
                    ["yes"] = "да",
                    ["no"] = "нет",
                    ["incoming_open_failed"] = "Не удалось открыть папку входящих файлов.",
                    ["incoming_prepare_failed"] = "Не удалось подготовить папку входящих файлов.",
                    ["endpoint_not_ready"] = "Android-устройство подключено, но файловый endpoint на нём пока не запущен.",
                    ["client_path_unavailable"] = "Устройство подключено, но для него недоступен путь открытия.",
                    ["webclient_browser_fallback"] = "WebClient не удалось запустить из приложения. Устройство открыто через браузерный fallback.",
                    ["client_open_failed"] = "Не удалось открыть устройство ни через WebDAV, ни через браузер.",
                    ["webclient_and_browser_failed"] = "Не удалось запустить службу WebClient и открыть устройство в браузере.",
                    ["drive_open_failed"] = "Не удалось открыть смонтированный диск устройства.",
                    ["client_disconnected"] = "Устройство уже отключено.",
                    ["network_devices"] = "Устройства в сети",
                    ["network_scanning"] = "Сканирование локальной сети…",
                    ["network_scan_failed"] = "Не удалось просканировать локальную сеть",
                    ["network_devices_found"] = "Найдено устройств: {0}",
                    ["network_no_devices"] = "Устройства DROPME не обнаружены",
                    ["device_connect"] = "Подключить",
                    ["device_connecting"] = "Подключение к {0}…",
                    ["device_connected"] = "{0} подключено",
                    ["device_connect_failed"] = "Не удалось подключиться к устройству",
                    ["file_access_available"] = "файловый доступ доступен",
                    ["file_access_unavailable"] = "файловый доступ недоступен",
                    ["trusted_devices"] = "Известные устройства",
                    ["allowed_devices"] = "Разрешённые",
                    ["blocked_devices"] = "Заблокированные",
                    ["trust_list_empty"] = "Список пуст",
                    ["trust_allow"] = "Разрешить",
                    ["trust_block"] = "Заблокировать",
                    ["save_device_name"] = "Сохранить",
                    ["device_service_enabled"] = "Доступно другим устройствам",
                    ["device_name_required"] = "Введите имя устройства",
                    ["device_send_files"] = "Отправить файлы",
                    ["share_sending"] = "Отправка файлов на {0}…",
                    ["share_sent"] = "Файлы отправлены на {0}",
                    ["share_send_failed"] = "Не удалось отправить файлы",
                    ["autostart_failed"] = "Не удалось изменить автозапуск."
                },
                [EnglishLanguage] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["app_title"] = "DROPME",
                    ["status_title"] = "DROPME Server",
                    ["my_device"] = "My device",
                    ["my_storage"] = "My storage",
                    ["storage_user_profile"] = "User profile",
                    ["storage_add_folder"] = "Add folder",
                    ["storage_unavailable"] = "Unavailable",
                    ["main_header_subtitle"] = "Devices and files",
                    ["status_running"] = "Server is running and waiting for devices",
                    ["status_stopped"] = "Server is stopped",
                    ["server_start_failed"] = "Could not start the local DROPME server.",
                    ["devices"] = "Connected devices",
                    ["no_devices"] = "No active Android devices",
                    ["open_incoming"] = "Open incoming folder",
                    ["autostart"] = "Autostart",
                    ["use_winfsp"] = "Use WinFsp",
                    ["winfsp_installed_version"] = "Installed • version {0}",
                    ["winfsp_not_installed"] = "Not installed",
                    ["language"] = "Language",
                    ["theme"] = "Theme",
                    ["close"] = "Hide to tray",
                    ["exit"] = "Exit",
                    ["system_language"] = "System language - {0}",
                    ["tooltip_language"] = "Interface language",
                    ["tooltip_theme"] = "Switch theme",
                    ["tooltip_close"] = "Hide window",
                    ["device_open"] = "Open",
                    ["endpoint_ready"] = "Endpoint ready",
                    ["drive"] = "Drive",
                    ["yes"] = "yes",
                    ["no"] = "no",
                    ["incoming_open_failed"] = "Could not open the incoming files folder.",
                    ["incoming_prepare_failed"] = "Could not prepare the incoming files folder.",
                    ["endpoint_not_ready"] = "The Android device is connected, but its file endpoint is not running yet.",
                    ["client_path_unavailable"] = "The device is connected, but no path is available for opening it.",
                    ["webclient_browser_fallback"] = "WebClient could not be started. The device was opened using the browser fallback.",
                    ["client_open_failed"] = "Could not open the device through WebDAV or the browser.",
                    ["webclient_and_browser_failed"] = "Could not start WebClient or open the device in the browser.",
                    ["drive_open_failed"] = "Could not open the mounted device drive.",
                    ["client_disconnected"] = "The device has already disconnected.",
                    ["network_devices"] = "Devices on the network",
                    ["network_scanning"] = "Scanning local network…",
                    ["network_scan_failed"] = "Could not scan the local network",
                    ["network_devices_found"] = "Devices found: {0}",
                    ["network_no_devices"] = "No DROPME devices detected",
                    ["device_connect"] = "Connect",
                    ["device_connecting"] = "Connecting to {0}…",
                    ["device_connected"] = "{0} connected",
                    ["device_connect_failed"] = "Could not connect to device",
                    ["file_access_available"] = "file access available",
                    ["file_access_unavailable"] = "file access unavailable",
                    ["trusted_devices"] = "Known devices",
                    ["allowed_devices"] = "Allowed",
                    ["blocked_devices"] = "Blocked",
                    ["trust_list_empty"] = "List is empty",
                    ["trust_allow"] = "Allow",
                    ["trust_block"] = "Block",
                    ["save_device_name"] = "Save",
                    ["device_service_enabled"] = "Available to other devices",
                    ["device_name_required"] = "Enter a device name",
                    ["device_send_files"] = "Send files",
                    ["share_sending"] = "Sending files to {0}…",
                    ["share_sent"] = "Files sent to {0}",
                    ["share_send_failed"] = "Could not send files",
                    ["autostart_failed"] = "Could not update autostart."
                }
            };

        public static event EventHandler? LanguageChanged;

        public static string CurrentLanguage
        {
            get { return ResolveLanguage(AppSettings.Language); }
            set
            {
                string normalized = NormalizeLanguage(value);
                if (string.Equals(SelectedLanguage, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                AppSettings.Language = normalized;
                AppSettings.Save();
                LanguageChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        public static string SelectedLanguage => NormalizeLanguage(AppSettings.Language);

        public static string DetectedLanguage => ResolveLanguage(GetSystemLanguage());

        public static bool IsAutomaticLanguageSelection =>
            string.Equals(SelectedLanguage, AutomaticLanguage, StringComparison.Ordinal);

        /// <summary>
        /// Нормализует настройку языка в auto, ru или en.
        /// </summary>
        public static string NormalizeLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language) ||
                string.Equals(language, AutomaticLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return AutomaticLanguage;
            }

            return string.Equals(language, RussianLanguage, StringComparison.OrdinalIgnoreCase)
                ? RussianLanguage
                : EnglishLanguage;
        }

        /// <summary>
        /// Преобразует режим auto в фактический язык интерфейса по CurrentUICulture Windows.
        /// </summary>
        public static string ResolveLanguage(string language)
        {
            string normalized = NormalizeLanguage(language);
            if (string.Equals(normalized, AutomaticLanguage, StringComparison.Ordinal))
            {
                return string.Equals(GetSystemLanguage(), RussianLanguage, StringComparison.Ordinal)
                    ? RussianLanguage
                    : EnglishLanguage;
            }

            return normalized;
        }

        public static bool IsRussianLanguage(string language)
        {
            return string.Equals(ResolveLanguage(language), RussianLanguage, StringComparison.Ordinal);
        }

        /// <summary>
        /// Возвращает локализованную строку по ключу для фактически используемого языка.
        /// </summary>
        public static string Text(string key)
        {
            Dictionary<string, string> language = Texts[CurrentLanguage];
            return language.TryGetValue(key, out string? value) ? value : key;
        }

        public static string LanguageDisplayName(string language)
        {
            return IsRussianLanguage(language) ? "Русский" : "English";
        }

        public static string SystemLanguageDisplayText()
        {
            return string.Format(Text("system_language"), LanguageDisplayName(DetectedLanguage));
        }

        /// <summary>
        /// Определяет поддерживаемый язык по системной UI culture текущего Windows-сеанса.
        /// Русская culture выбирает ru, остальные culture используют en.
        /// </summary>
        private static string GetSystemLanguage()
        {
            return string.Equals(
                CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
                RussianLanguage,
                StringComparison.OrdinalIgnoreCase)
                ? RussianLanguage
                : EnglishLanguage;
        }
    }
}
