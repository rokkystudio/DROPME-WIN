#include "AutoStart.h"

#include "utils/Log.h"

#include <windows.h>

namespace {

constexpr wchar_t kRunKeyPath[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
constexpr wchar_t kValueName[] = L"DROPME";
constexpr wchar_t kLegacyValueName[] = L"WiFiDrop";

bool QueryMatchingValue(HKEY keyHandle, const wchar_t *valueName, const std::wstring &expectedCommandLine) {
    DWORD valueType = 0;
    wchar_t valueBuffer[32768]{};
    DWORD valueSize = sizeof(valueBuffer);
    const LONG result = RegQueryValueExW(
        keyHandle,
        valueName,
        nullptr,
        &valueType,
        reinterpret_cast<LPBYTE>(valueBuffer),
        &valueSize);
    return result == ERROR_SUCCESS &&
        valueType == REG_SZ &&
        expectedCommandLine == valueBuffer;
}

bool DeleteValueIfExists(HKEY keyHandle, const wchar_t *valueName) {
    const LONG result = RegDeleteValueW(keyHandle, valueName);
    return result == ERROR_SUCCESS || result == ERROR_FILE_NOT_FOUND;
}

}  // namespace

bool AutoStart::IsEnabled() const {
    HKEY keyHandle = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kRunKeyPath, 0, KEY_READ, &keyHandle) != ERROR_SUCCESS) {
        return false;
    }

    const std::wstring commandLine = BuildCommandLine();
    const bool enabled =
        QueryMatchingValue(keyHandle, kValueName, commandLine) ||
        QueryMatchingValue(keyHandle, kLegacyValueName, commandLine);
    RegCloseKey(keyHandle);
    return enabled;
}

bool AutoStart::SetEnabled(bool enabled) const {
    HKEY keyHandle = nullptr;
    const LONG openResult = RegCreateKeyExW(
        HKEY_CURRENT_USER,
        kRunKeyPath,
        0,
        nullptr,
        0,
        KEY_SET_VALUE | KEY_QUERY_VALUE,
        nullptr,
        &keyHandle,
        nullptr);
    if (openResult != ERROR_SUCCESS) {
        return false;
    }

    LONG result = ERROR_SUCCESS;
    if (enabled) {
        const std::wstring commandLine = BuildCommandLine();
        result = RegSetValueExW(
            keyHandle,
            kValueName,
            0,
            REG_SZ,
            reinterpret_cast<const BYTE *>(commandLine.c_str()),
            static_cast<DWORD>((commandLine.size() + 1) * sizeof(wchar_t)));
        if (result == ERROR_SUCCESS && !DeleteValueIfExists(keyHandle, kLegacyValueName)) {
            result = ERROR_ACCESS_DENIED;
        }
    } else {
        const bool deletedPrimary = DeleteValueIfExists(keyHandle, kValueName);
        const bool deletedLegacy = DeleteValueIfExists(keyHandle, kLegacyValueName);
        if (!deletedPrimary || !deletedLegacy) {
            result = ERROR_ACCESS_DENIED;
        }
    }

    RegCloseKey(keyHandle);
    return result == ERROR_SUCCESS;
}

std::wstring AutoStart::BuildCommandLine() const {
    wchar_t modulePath[MAX_PATH]{};
    constexpr DWORD modulePathCapacity = static_cast<DWORD>(sizeof(modulePath) / sizeof(modulePath[0]));
    const DWORD length = GetModuleFileNameW(nullptr, modulePath, modulePathCapacity);
    if (length == 0 || length == modulePathCapacity) {
        Log::Error("GetModuleFileNameW failed while building autostart command");
        return L"";
    }

    return L"\"" + std::wstring(modulePath) + L"\" --tray";
}
