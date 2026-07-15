#include "App.h"

#include "AutoStart.h"
#include "clients/AndroidClient.h"
#include "clients/ClientManager.h"
#include "ConnectedDevicesWindow.h"
#include "server/WifiDropServer.h"
#include "TrayIcon.h"
#include "utils/DesktopFolders.h"
#include "utils/Log.h"
#include "utils/Utf.h"
#include "utils/WebClientService.h"

#include <shellapi.h>

#include <algorithm>
#include <exception>
#include <sstream>
#include <vector>

namespace {

bool CanOpenClientLocation(const AndroidClient &client) {
    return !client.driveLetter.empty() || (client.mountReady && !client.webDavHost.empty() && client.webDavPort > 0);
}

std::wstring BuildClientOpenPath(const AndroidClient &client) {
    if (!client.driveLetter.empty()) {
        return Utf::Utf8ToWide(client.driveLetter) + L":\\";
    }

    if (client.webDavHost.empty() || client.webDavPort <= 0) {
        return L"";
    }

    std::wstring basePath = Utf::Utf8ToWide(client.webDavBasePath);
    std::replace(basePath.begin(), basePath.end(), L'/', L'\\');
    while (!basePath.empty() && basePath.front() == L'\\') {
        basePath.erase(basePath.begin());
    }
    while (!basePath.empty() && basePath.back() == L'\\') {
        basePath.pop_back();
    }

    std::wstring uncPath = L"\\\\";
    uncPath += Utf::Utf8ToWide(client.webDavHost);
    uncPath += L"@";
    uncPath += std::to_wstring(client.webDavPort);
    uncPath += L"\\DavWWWRoot\\";
    if (!basePath.empty()) {
        uncPath += basePath;
        if (uncPath.back() != L'\\') {
            uncPath += L'\\';
        }
    }

    return uncPath;
}

std::wstring BuildClientBrowserUrl(const AndroidClient &client) {
    if (client.webDavHost.empty() || client.webDavPort <= 0) {
        return L"";
    }

    std::wstring url = L"http://";
    url += Utf::Utf8ToWide(client.webDavHost);
    url += L":";
    url += std::to_wstring(client.webDavPort);

    std::wstring basePath = Utf::Utf8ToWide(client.webDavBasePath);
    if (basePath.empty()) {
        url += L"/";
        return url;
    }

    if (basePath.front() != L'/') {
        url += L"/";
    }
    url += basePath;
    if (url.back() != L'/') {
        url += L"/";
    }
    return url;
}

bool TryOpenPath(const std::wstring &path, const char *logContext) {
    Log::Info(std::string("Opening path [") + logContext + "]: " + Utf::WideToUtf8(path));
    const auto result = reinterpret_cast<INT_PTR>(
        ShellExecuteW(nullptr, L"open", path.c_str(), nullptr, nullptr, SW_SHOWNORMAL));
    if (result > 32) {
        return true;
    }

    Log::Error(std::string("ShellExecuteW failed for ") + logContext + " with code " + std::to_string(result));
    return false;
}

DWORD NotificationFlagsFromMessageBoxFlags(UINT flags) {
    if ((flags & MB_ICONERROR) == MB_ICONERROR) {
        return NIIF_ERROR;
    }
    if ((flags & MB_ICONWARNING) == MB_ICONWARNING) {
        return NIIF_WARNING;
    }
    return NIIF_INFO;
}

std::wstring BuildConnectedDevicesText(const std::vector<AndroidClient> &clients) {
    if (clients.empty()) {
        return L"Активных Android-устройств нет.";
    }

    std::wstringstream stream;
    stream << L"Активные устройства: " << clients.size() << L"\r\n\r\n";
    for (const AndroidClient &client : clients) {
        stream << L"Имя: " << client.driveName << L"\r\n";
        stream << L"Client ID: " << Utf::Utf8ToWide(client.clientId) << L"\r\n";
        stream << L"IP: " << Utf::Utf8ToWide(client.remoteIp) << L"\r\n";
        stream << L"Android endpoint: " << Utf::Utf8ToWide(client.webDavHost) << L":" << client.webDavPort << L"\r\n";
        stream << L"Endpoint готов: " << (client.mountReady ? L"да" : L"нет") << L"\r\n";
        stream << L"Буква диска: ";
        if (client.driveLetter.empty()) {
            stream << L"не назначена";
        } else {
            stream << Utf::Utf8ToWide(client.driveLetter);
        }
        if (!client.mountError.empty()) {
            stream << L"\r\nПричина mount error: " << Utf::Utf8ToWide(client.mountError);
        }
        stream << L"\r\n\r\n";
    }

    stream << L"Открытие из трея использует смонтированный диск устройства.";
    return stream.str();
}

}  // namespace

App::~App() = default;

int App::Run(HINSTANCE instanceHandle, int) {
    instanceHandle_ = instanceHandle;
    if (!Initialize(instanceHandle)) {
        Shutdown();
        return 1;
    }

    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }

    Shutdown();
    return static_cast<int>(message.wParam);
}

bool App::Initialize(HINSTANCE instanceHandle) {
    Log::Initialize();
    Log::Info("DROPME starting");
#if WIFIDROP_ENABLE_WINFSP
    Log::Info("Drive backend: WinFsp");
#else
    Log::Info("Drive backend: WebDAV redirector");
    WebClientService::EnsureRunning();
#endif

    autoStart_ = std::make_unique<AutoStart>();
    clientManager_ = std::make_unique<ClientManager>();
    connectedDevicesWindow_ = std::make_unique<ConnectedDevicesWindow>();
    server_ = std::make_unique<WifiDropServer>(*clientManager_);

    if (!connectedDevicesWindow_->Initialize(instanceHandle)) {
        Log::Error("Failed to initialize connected devices window");
        ShowMessage(L"DROPME", L"Не удалось создать окно подключенных устройств.", MB_OK | MB_ICONERROR);
        return false;
    }

    trayIcon_ = std::make_unique<TrayIcon>();
    if (!trayIcon_->Initialize(
            instanceHandle,
            L"DROPME",
            [this]() { OpenIncomingFolder(); },
            [this]() { ShowConnectedDevices(); },
            [this]() { ToggleAutostart(); },
            [this]() { ExitApplication(); },
            [this]() { HandleTrayDoubleClick(); },
            [this]() { return autoStart_->IsEnabled(); },
            [this]() { return BuildTrayDeviceMenuItems(); },
            [this](const std::string &clientId) { OpenClientDrive(clientId); })) {
        Log::Error("Failed to initialize tray icon");
        ShowMessage(L"DROPME", L"Не удалось создать иконку в системном трее.", MB_OK | MB_ICONERROR);
        return false;
    }

    if (!server_->Start()) {
        Log::Error("Failed to start server");
        ShowMessage(L"DROPME", L"Не удалось запустить локальный сервер DROPME.", MB_OK | MB_ICONERROR);
        return false;
    }

    return true;
}

void App::Shutdown() {
    if (server_) {
        server_->Stop();
    }
    if (connectedDevicesWindow_) {
        connectedDevicesWindow_->Shutdown();
    }
    if (trayIcon_) {
        trayIcon_->Shutdown();
    }
    Log::Info("DROPME stopped");
    Log::Shutdown();
}

void App::OpenIncomingFolder() {
    try {
        const auto folder = DesktopFolders::EnsureIncomingFolder();
        const auto result = reinterpret_cast<INT_PTR>(
            ShellExecuteW(nullptr, L"open", folder.c_str(), nullptr, nullptr, SW_SHOWNORMAL));
        if (result <= 32) {
            Log::Error("ShellExecuteW failed for incoming folder");
            ShowMessage(L"DROPME", L"Не удалось открыть папку входящих файлов.", MB_OK | MB_ICONERROR);
        }
    } catch (const std::exception &exception) {
        Log::Error(std::string("Failed to open incoming folder: ") + exception.what());
        ShowMessage(L"DROPME", L"Не удалось подготовить папку входящих файлов.", MB_OK | MB_ICONERROR);
    }
}

void App::ShowConnectedDevices() {
    const auto clients = clientManager_->ListClients();
    if (connectedDevicesWindow_) {
        connectedDevicesWindow_->Show(BuildConnectedDevicesText(clients));
    }
}

std::vector<TrayDeviceMenuItem> App::BuildTrayDeviceMenuItems() const {
    const auto clients = clientManager_->ListClients();
    std::vector<TrayDeviceMenuItem> menuItems;
    menuItems.reserve(clients.size());
    for (const AndroidClient &client : clients) {
        menuItems.push_back({
            .clientId = client.clientId,
            .displayName = client.driveName,
            .canOpen = CanOpenClientLocation(client),
        });
    }
    return menuItems;
}

void App::OpenClientDrive(const std::string &clientId) {
    const auto clients = clientManager_->ListClients();
    for (const AndroidClient &client : clients) {
        if (client.clientId != clientId) {
            continue;
        }

        if (client.driveLetter.empty()) {
            if (!client.mountReady) {
                ShowMessage(L"DROPME",
                            L"Android-устройство подключено, но файловый endpoint на нём пока не запущен.",
                            MB_OK | MB_ICONINFORMATION);
                return;
            }

#if WIFIDROP_ENABLE_WINFSP
            ShowMessage(L"DROPME",
                        !client.mountError.empty()
                            ? Utf::Utf8ToWide(client.mountError)
                            : L"Не удалось смонтировать WinFsp-диск устройства.",
                        MB_OK | MB_ICONERROR);
            return;
#else
            const bool webClientReady = WebClientService::EnsureRunning();
            const std::wstring webDavPath = BuildClientOpenPath(client);
            const std::wstring browserUrl = BuildClientBrowserUrl(client);
            if (webDavPath.empty() && browserUrl.empty()) {
                ShowMessage(L"DROPME",
                            L"Устройство подключено, но для него недоступен путь открытия.",
                            MB_OK | MB_ICONINFORMATION);
                return;
            }

            if (!webDavPath.empty() && TryOpenPath(webDavPath, "client WebDAV path")) {
                return;
            }

            if (!browserUrl.empty() && TryOpenPath(browserUrl, "client browser URL")) {
                if (!client.mountError.empty()) {
                    ShowMessage(L"DROPME",
                                Utf::Utf8ToWide(client.mountError),
                                MB_OK | MB_ICONWARNING);
                } else if (!webClientReady) {
                    ShowMessage(L"DROPME",
                                L"WebClient не удалось запустить из приложения. Устройство открыто через браузерный fallback.",
                                MB_OK | MB_ICONINFORMATION);
                }
                return;
            }

            ShowMessage(L"DROPME",
                        !client.mountError.empty()
                            ? Utf::Utf8ToWide(client.mountError)
                            : webClientReady
                            ? L"Не удалось открыть устройство ни через WebDAV, ни через браузер."
                            : L"Не удалось запустить службу WebClient и открыть устройство в браузере.",
                        MB_OK | MB_ICONERROR);
            return;
#endif
        }

        const std::wstring rootPath = BuildClientOpenPath(client);
        if (!TryOpenPath(rootPath, "client drive")) {
            ShowMessage(L"DROPME", L"Не удалось открыть смонтированный диск устройства.", MB_OK | MB_ICONERROR);
        }
        return;
    }

    ShowMessage(L"DROPME", L"Устройство уже отключено.", MB_OK | MB_ICONINFORMATION);
}

void App::ToggleAutostart() {
    const bool enabled = autoStart_->IsEnabled();
    const bool targetState = !enabled;
    if (!autoStart_->SetEnabled(targetState)) {
        Log::Error("Failed to update autostart registry value");
        ShowMessage(L"DROPME", L"Не удалось изменить автозапуск.", MB_OK | MB_ICONERROR);
        return;
    }

    Log::Info(targetState ? "Autostart enabled" : "Autostart disabled");
}

void App::HandleTrayDoubleClick() {
    const auto clients = clientManager_->ListClients();
    if (clients.empty()) {
        OpenIncomingFolder();
        return;
    }

    if (clients.size() == 1U) {
        OpenClientDrive(clients.front().clientId);
        return;
    }

    Log::Info("Multiple clients are connected; tray double click keeps the devices window hidden");
}

void App::ShowMessage(const std::wstring &title, const std::wstring &message, UINT flags) const {
    if (trayIcon_) {
        trayIcon_->ShowNotification(title, message, NotificationFlagsFromMessageBoxFlags(flags));
        return;
    }
    MessageBoxW(nullptr, message.c_str(), title.c_str(), flags);
}

void App::ExitApplication() {
    PostQuitMessage(0);
}
