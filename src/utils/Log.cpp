#include "Log.h"

#include "DesktopFolders.h"

#include <windows.h>

#include <fstream>
#include <iomanip>
#include <sstream>
#include <mutex>

namespace {

std::mutex g_logMutex;
std::ofstream g_logFile;

std::string BuildTimestamp() {
    SYSTEMTIME localTime{};
    GetLocalTime(&localTime);
    std::ostringstream stream;
    stream.fill('0');
    stream << std::setw(4) << localTime.wYear
           << "-" << std::setw(2) << localTime.wMonth
           << "-" << std::setw(2) << localTime.wDay
           << " " << std::setw(2) << localTime.wHour
           << ":" << std::setw(2) << localTime.wMinute
           << ":" << std::setw(2) << localTime.wSecond
           << "." << std::setw(3) << localTime.wMilliseconds;
    return stream.str();
}

}  // namespace

void Log::Initialize() {
    std::lock_guard lock(g_logMutex);
    if (g_logFile.is_open()) {
        return;
    }

    const auto logPath = DesktopFolders::EnsureLogFolder() / L"dropme.log";
    g_logFile.open(logPath, std::ios::app | std::ios::binary);
}

void Log::Shutdown() {
    std::lock_guard lock(g_logMutex);
    if (g_logFile.is_open()) {
        g_logFile.flush();
        g_logFile.close();
    }
}

void Log::Info(const std::string &message) {
    WriteLine("INFO", message);
}

void Log::Warn(const std::string &message) {
    WriteLine("WARN", message);
}

void Log::Error(const std::string &message) {
    WriteLine("ERROR", message);
}

void Log::WriteLine(const char *level, const std::string &message) {
    std::lock_guard lock(g_logMutex);
    if (!g_logFile.is_open()) {
        return;
    }

    const std::string line = "[" + BuildTimestamp() + "] [" + level + "] " + message + "\n";
    g_logFile << line;
    g_logFile.flush();
    OutputDebugStringA(line.c_str());
}
