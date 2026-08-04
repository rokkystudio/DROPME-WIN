#pragma once

#include <chrono>
#include <filesystem>

/// Возвращает служебные директории DROPME в профиле текущего пользователя.
class DesktopFolders {
public:
    /// Возвращает путь к папке входящих файлов на рабочем столе и создаёт её при отсутствии.
    static std::filesystem::path EnsureIncomingFolder();

    /// Возвращает путь к папке входящих файлов для момента начала передачи.
    static std::filesystem::path EnsureIncomingFolderForTime(std::chrono::system_clock::time_point timePoint);

    /// Возвращает путь к директории логов DROPME и создаёт её при отсутствии.
    static std::filesystem::path EnsureLogFolder();
};
