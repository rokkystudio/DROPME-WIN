#pragma once

#include <filesystem>

/// Возвращает служебные директории DROPME в профиле текущего пользователя.
class DesktopFolders {
public:
    /// Возвращает путь к папке входящих файлов на рабочем столе и создаёт её при отсутствии.
    static std::filesystem::path EnsureIncomingFolder();

    /// Возвращает путь к директории логов DROPME и создаёт её при отсутствии.
    static std::filesystem::path EnsureLogFolder();
};
