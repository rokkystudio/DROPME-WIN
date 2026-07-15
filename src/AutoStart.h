#pragma once

#include <string>

/// Управляет значением автозапуска DROPME в разделе HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
class AutoStart {
public:
    /// Возвращает текущее состояние автозапуска.
    bool IsEnabled() const;

    /// Включает или выключает автозапуск для текущего пользователя.
    bool SetEnabled(bool enabled) const;

private:
    /// Формирует строку запуска с полным путём к исполняемому файлу и аргументом --tray.
    std::wstring BuildCommandLine() const;
};
