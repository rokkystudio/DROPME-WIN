#include "DesktopFolders.h"

#include <windows.h>
#include <shlobj.h>

#include <chrono>
#include <ctime>
#include <iomanip>
#include <sstream>
#include <stdexcept>

namespace {

std::filesystem::path GetKnownFolderPath(REFKNOWNFOLDERID folderId) {
    PWSTR rawPath = nullptr;
    const HRESULT result = SHGetKnownFolderPath(folderId, 0, nullptr, &rawPath);
    if (FAILED(result)) {
        throw std::runtime_error("SHGetKnownFolderPath failed");
    }

    std::filesystem::path path(rawPath);
    CoTaskMemFree(rawPath);
    return path;
}

std::wstring BuildTodayFolderName() {
    const auto now = std::chrono::system_clock::now();
    const std::time_t time = std::chrono::system_clock::to_time_t(now);
    std::tm localTime{};
    localtime_s(&localTime, &time);
    std::wstringstream stream;
    stream.fill(L'0');
    stream << L"DROPME "
           << std::setw(4) << (localTime.tm_year + 1900)
           << L"-" << std::setw(2) << (localTime.tm_mon + 1)
           << L"-" << std::setw(2) << localTime.tm_mday;
    return stream.str();
}

}  // namespace

std::filesystem::path DesktopFolders::EnsureIncomingFolder() {
    const auto path = GetKnownFolderPath(FOLDERID_Desktop) / BuildTodayFolderName();
    std::filesystem::create_directories(path);
    return path;
}

std::filesystem::path DesktopFolders::EnsureLogFolder() {
    const auto path = GetKnownFolderPath(FOLDERID_LocalAppData) / L"DROPME" / L"logs";
    std::filesystem::create_directories(path);
    return path;
}
