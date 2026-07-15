#pragma once

#include <string>

/// Пишет текстовый лог DROPME в %LOCALAPPDATA%\DROPME\logs\dropme.log.
class Log {
public:
    /// Подготавливает директорию логов и файл журнала.
    static void Initialize();

    /// Завершает работу логгера.
    static void Shutdown();

    /// Записывает информационное сообщение.
    static void Info(const std::string &message);

    /// Записывает предупреждение.
    static void Warn(const std::string &message);

    /// Записывает ошибку.
    static void Error(const std::string &message);

private:
    /// Записывает одну строку в лог с указанным уровнем.
    static void WriteLine(const char *level, const std::string &message);
};
