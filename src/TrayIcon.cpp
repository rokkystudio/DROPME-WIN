#include "TrayIcon.h"

#include "resource.h"
#include "utils/Log.h"

#include <shellapi.h>
#include <windowsx.h>
#include <utility>

namespace {

constexpr wchar_t kWindowClassName[] = L"DROPMETrayWindow";

}  // namespace

bool TrayIcon::Initialize(HINSTANCE instanceHandle,
                          const std::wstring &tooltip,
                          ActionCallback openIncomingFolder,
                          ActionCallback showConnectedDevices,
                          ActionCallback toggleAutostart,
                          ActionCallback exitApplication,
                          ActionCallback trayDoubleClick,
                          QueryBoolCallback isAutostartEnabled,
                          QueryDevicesCallback queryDevices,
                          OpenDeviceCallback openDevice) {
    instanceHandle_ = instanceHandle;
    openIncomingFolder_ = std::move(openIncomingFolder);
    showConnectedDevices_ = std::move(showConnectedDevices);
    toggleAutostart_ = std::move(toggleAutostart);
    exitApplication_ = std::move(exitApplication);
    trayDoubleClick_ = std::move(trayDoubleClick);
    isAutostartEnabled_ = std::move(isAutostartEnabled);
    queryDevices_ = std::move(queryDevices);
    openDevice_ = std::move(openDevice);

    if (!CreateWindowClassAndHandle(instanceHandle)) {
        return false;
    }

    if (!LoadResources()) {
        return false;
    }

    return AddNotifyIcon(tooltip);
}

void TrayIcon::Shutdown() {
    if (windowHandle_) {
        NOTIFYICONDATAW notifyIconData{};
        notifyIconData.cbSize = sizeof(notifyIconData);
        notifyIconData.hWnd = windowHandle_;
        notifyIconData.uID = kTrayIconId;
        Shell_NotifyIconW(NIM_DELETE, &notifyIconData);
    }

    if (windowHandle_) {
        DestroyWindow(windowHandle_);
        windowHandle_ = nullptr;
    }

    if (deviceMenuBitmapHandle_) {
        DeleteObject(deviceMenuBitmapHandle_);
        deviceMenuBitmapHandle_ = nullptr;
    }

    if (exitMenuBitmapHandle_) {
        DeleteObject(exitMenuBitmapHandle_);
        exitMenuBitmapHandle_ = nullptr;
    }

    if (trayIconHandle_) {
        DestroyIcon(trayIconHandle_);
        trayIconHandle_ = nullptr;
    }

    if (appIconHandle_) {
        DestroyIcon(appIconHandle_);
        appIconHandle_ = nullptr;
    }

    if (deviceMenuIconHandle_) {
        DestroyIcon(deviceMenuIconHandle_);
        deviceMenuIconHandle_ = nullptr;
    }

    if (exitMenuIconHandle_) {
        DestroyIcon(exitMenuIconHandle_);
        exitMenuIconHandle_ = nullptr;
    }
}

LRESULT TrayIcon::WindowProc(HWND windowHandle, UINT message, WPARAM wParam, LPARAM lParam) {
    switch (message) {
        case WM_COMMAND:
            HandleCommand(LOWORD(wParam));
            return 0;
        case WM_DESTROY:
            return 0;
        default:
            break;
    }

    if (message == kTrayCallbackMessage) {
        const UINT trayEvent = LOWORD(static_cast<DWORD>(lParam));
        switch (trayEvent) {
            case WM_CONTEXTMENU:
            case WM_RBUTTONUP:
                ShowContextMenu(
                    POINT{GET_X_LPARAM(wParam), GET_Y_LPARAM(wParam)},
                    trayEvent == WM_CONTEXTMENU);
                return 0;
            case WM_LBUTTONDBLCLK:
                if (trayDoubleClick_) {
                    trayDoubleClick_();
                }
                return 0;
            default:
                return 0;
        }
    }

    return DefWindowProcW(windowHandle, message, wParam, lParam);
}

bool TrayIcon::CreateWindowClassAndHandle(HINSTANCE instanceHandle) {
    WNDCLASSEXW windowClass{};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.lpfnWndProc = &TrayIcon::StaticWindowProc;
    windowClass.hInstance = instanceHandle;
    windowClass.lpszClassName = kWindowClassName;

    if (!RegisterClassExW(&windowClass) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
        Log::Error("RegisterClassExW failed");
        return false;
    }

    windowHandle_ = CreateWindowExW(
        0,
        kWindowClassName,
        L"DROPME Tray Window",
        WS_OVERLAPPED,
        0,
        0,
        0,
        0,
        nullptr,
        nullptr,
        instanceHandle,
        this);

    if (!windowHandle_) {
        Log::Error("CreateWindowExW failed");
        return false;
    }

    return true;
}

bool TrayIcon::AddNotifyIcon(const std::wstring &tooltip) {
    NOTIFYICONDATAW notifyIconData{};
    notifyIconData.cbSize = sizeof(notifyIconData);
    notifyIconData.hWnd = windowHandle_;
    notifyIconData.uID = kTrayIconId;
    notifyIconData.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
    notifyIconData.uCallbackMessage = kTrayCallbackMessage;
    notifyIconData.hIcon = trayIconHandle_;
    wcsncpy_s(notifyIconData.szTip, tooltip.c_str(), _TRUNCATE);

    if (!Shell_NotifyIconW(NIM_ADD, &notifyIconData)) {
        Log::Error("Shell_NotifyIconW(NIM_ADD) failed");
        return false;
    }

    notifyIconData.uVersion = NOTIFYICON_VERSION_4;
    Shell_NotifyIconW(NIM_SETVERSION, &notifyIconData);
    return true;
}

void TrayIcon::ShowNotification(const std::wstring &title, const std::wstring &message, DWORD infoFlags) const {
    if (!windowHandle_) {
        return;
    }

    NOTIFYICONDATAW notifyIconData{};
    notifyIconData.cbSize = sizeof(notifyIconData);
    notifyIconData.hWnd = windowHandle_;
    notifyIconData.uID = kTrayIconId;
    notifyIconData.uFlags = NIF_INFO;
    notifyIconData.dwInfoFlags = infoFlags;
    notifyIconData.uTimeout = 5000;
    wcsncpy_s(notifyIconData.szInfoTitle, title.c_str(), _TRUNCATE);
    wcsncpy_s(notifyIconData.szInfo, message.c_str(), _TRUNCATE);
    Shell_NotifyIconW(NIM_MODIFY, &notifyIconData);
}

bool TrayIcon::LoadResources() {
    trayIconHandle_ = static_cast<HICON>(LoadImageW(
        instanceHandle_,
        MAKEINTRESOURCEW(IDI_APP_ICON),
        IMAGE_ICON,
        GetSystemMetrics(SM_CXSMICON),
        GetSystemMetrics(SM_CYSMICON),
        LR_DEFAULTCOLOR));
    appIconHandle_ = static_cast<HICON>(LoadImageW(
        instanceHandle_,
        MAKEINTRESOURCEW(IDI_APP_ICON),
        IMAGE_ICON,
        GetSystemMetrics(SM_CXICON),
        GetSystemMetrics(SM_CYICON),
        LR_DEFAULTCOLOR));
    deviceMenuIconHandle_ = static_cast<HICON>(LoadImageW(
        instanceHandle_,
        MAKEINTRESOURCEW(IDI_DEVICE_ICON),
        IMAGE_ICON,
        GetSystemMetrics(SM_CXSMICON),
        GetSystemMetrics(SM_CYSMICON),
        LR_DEFAULTCOLOR));
    exitMenuIconHandle_ = static_cast<HICON>(LoadImageW(
        instanceHandle_,
        MAKEINTRESOURCEW(IDI_EXIT_ICON),
        IMAGE_ICON,
        GetSystemMetrics(SM_CXSMICON),
        GetSystemMetrics(SM_CYSMICON),
        LR_DEFAULTCOLOR));

    if (!trayIconHandle_ || !appIconHandle_ || !deviceMenuIconHandle_ || !exitMenuIconHandle_) {
        Log::Error("Failed to load icon resources");
        return false;
    }

    deviceMenuBitmapHandle_ = CreateMenuBitmap(deviceMenuIconHandle_);
    exitMenuBitmapHandle_ = CreateMenuBitmap(exitMenuIconHandle_);
    if (!deviceMenuBitmapHandle_ || !exitMenuBitmapHandle_) {
        Log::Error("Failed to create menu bitmaps");
        return false;
    }

    SendMessageW(windowHandle_, WM_SETICON, ICON_SMALL, reinterpret_cast<LPARAM>(trayIconHandle_));
    SendMessageW(windowHandle_, WM_SETICON, ICON_BIG, reinterpret_cast<LPARAM>(appIconHandle_));
    return true;
}

void TrayIcon::ShowContextMenu(POINT anchorPoint, bool useAnchorPoint) {
    HMENU menuHandle = CreatePopupMenu();
    if (!menuHandle) {
        Log::Error("CreatePopupMenu failed");
        return;
    }

    deviceCommands_.clear();
    const bool autostartEnabled = isAutostartEnabled_ && isAutostartEnabled_();
    const wchar_t *autostartLabel = autostartEnabled ? L"Автозапуск: Вкл" : L"Автозапуск: Выкл";

    AppendMenuItem(menuHandle, kMenuOpenIncomingFolder, L"Открыть папку входящих", true, nullptr);
    AppendMenuItem(menuHandle, kMenuConnectedDevices, L"Подключенные устройства", true, nullptr);
    AppendMenuItem(menuHandle, kMenuToggleAutostart, autostartLabel, true, nullptr);

    const auto devices = queryDevices_ ? queryDevices_() : std::vector<TrayDeviceMenuItem>{};
    if (!devices.empty()) {
        AppendMenuW(menuHandle, MF_SEPARATOR, 0, nullptr);
        UINT commandId = kMenuDeviceBase;
        for (const TrayDeviceMenuItem &device : devices) {
            deviceCommands_.emplace(commandId, device.clientId);
            AppendMenuItem(menuHandle, commandId, device.displayName, device.canOpen, deviceMenuBitmapHandle_);
            ++commandId;
        }
    }

    AppendMenuW(menuHandle, MF_SEPARATOR, 0, nullptr);
    AppendMenuItem(menuHandle, kMenuExit, L"Выход", true, exitMenuBitmapHandle_);

    POINT cursorPosition = anchorPoint;
    if (!useAnchorPoint || cursorPosition.x < 0 || cursorPosition.y < 0) {
        GetCursorPos(&cursorPosition);
    }
    SetForegroundWindow(windowHandle_);
    TrackPopupMenu(menuHandle,
                   TPM_BOTTOMALIGN | TPM_LEFTALIGN | TPM_RIGHTBUTTON,
                   cursorPosition.x,
                   cursorPosition.y,
                   0,
                   windowHandle_,
                   nullptr);
    PostMessageW(windowHandle_, WM_NULL, 0, 0);
    DestroyMenu(menuHandle);
}

void TrayIcon::AppendMenuItem(HMENU menuHandle, UINT commandId, const std::wstring &text, bool enabled, HBITMAP bitmapHandle) const {
    MENUITEMINFOW menuItemInfo{};
    menuItemInfo.cbSize = sizeof(menuItemInfo);
    menuItemInfo.fMask = MIIM_ID | MIIM_STRING | MIIM_STATE;
    menuItemInfo.wID = commandId;
    menuItemInfo.dwTypeData = const_cast<wchar_t *>(text.c_str());
    menuItemInfo.fState = enabled ? MFS_ENABLED : MFS_DISABLED;

    if (bitmapHandle) {
        menuItemInfo.fMask |= MIIM_BITMAP;
        menuItemInfo.hbmpItem = bitmapHandle;
    }

    InsertMenuItemW(menuHandle, GetMenuItemCount(menuHandle), TRUE, &menuItemInfo);
}

HBITMAP TrayIcon::CreateMenuBitmap(HICON iconHandle) const {
    const int width = GetSystemMetrics(SM_CXSMICON);
    const int height = GetSystemMetrics(SM_CYSMICON);

    BITMAPV5HEADER bitmapHeader{};
    bitmapHeader.bV5Size = sizeof(bitmapHeader);
    bitmapHeader.bV5Width = width;
    bitmapHeader.bV5Height = -height;
    bitmapHeader.bV5Planes = 1;
    bitmapHeader.bV5BitCount = 32;
    bitmapHeader.bV5Compression = BI_BITFIELDS;
    bitmapHeader.bV5RedMask = 0x00FF0000;
    bitmapHeader.bV5GreenMask = 0x0000FF00;
    bitmapHeader.bV5BlueMask = 0x000000FF;
    bitmapHeader.bV5AlphaMask = 0xFF000000;

    void *bits = nullptr;
    HDC screenDeviceContext = GetDC(nullptr);
    const HBITMAP bitmapHandle = CreateDIBSection(
        screenDeviceContext,
        reinterpret_cast<BITMAPINFO *>(&bitmapHeader),
        DIB_RGB_COLORS,
        &bits,
        nullptr,
        0);
    if (!bitmapHandle) {
        ReleaseDC(nullptr, screenDeviceContext);
        return nullptr;
    }

    HDC memoryDeviceContext = CreateCompatibleDC(screenDeviceContext);
    ReleaseDC(nullptr, screenDeviceContext);
    if (!memoryDeviceContext) {
        DeleteObject(bitmapHandle);
        return nullptr;
    }

    HGDIOBJ oldBitmap = SelectObject(memoryDeviceContext, bitmapHandle);
    PatBlt(memoryDeviceContext, 0, 0, width, height, BLACKNESS);
    DrawIconEx(memoryDeviceContext, 0, 0, iconHandle, width, height, 0, nullptr, DI_NORMAL);
    SelectObject(memoryDeviceContext, oldBitmap);
    DeleteDC(memoryDeviceContext);
    return bitmapHandle;
}

void TrayIcon::HandleCommand(UINT commandId) {
    if (const auto deviceIterator = deviceCommands_.find(commandId); deviceIterator != deviceCommands_.end()) {
        if (openDevice_) {
            openDevice_(deviceIterator->second);
        }
        return;
    }

    switch (commandId) {
        case kMenuOpenIncomingFolder:
            if (openIncomingFolder_) {
                openIncomingFolder_();
            }
            break;
        case kMenuConnectedDevices:
            if (showConnectedDevices_) {
                showConnectedDevices_();
            }
            break;
        case kMenuToggleAutostart:
            if (toggleAutostart_) {
                toggleAutostart_();
            }
            break;
        case kMenuExit:
            if (exitApplication_) {
                exitApplication_();
            }
            break;
        default:
            break;
    }
}

LRESULT CALLBACK TrayIcon::StaticWindowProc(HWND windowHandle, UINT message, WPARAM wParam, LPARAM lParam) {
    TrayIcon *self = nullptr;
    if (message == WM_NCCREATE) {
        const auto *createStruct = reinterpret_cast<CREATESTRUCTW *>(lParam);
        self = static_cast<TrayIcon *>(createStruct->lpCreateParams);
        SetWindowLongPtrW(windowHandle, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
    } else {
        self = reinterpret_cast<TrayIcon *>(GetWindowLongPtrW(windowHandle, GWLP_USERDATA));
    }

    if (!self) {
        return DefWindowProcW(windowHandle, message, wParam, lParam);
    }

    return self->WindowProc(windowHandle, message, wParam, lParam);
}
