#include "ConnectedDevicesWindow.h"

#include "utils/Log.h"

namespace {

constexpr wchar_t kConnectedDevicesWindowClassName[] = L"DROPMEConnectedDevicesWindow";
constexpr int kInitialWindowWidth = 720;
constexpr int kInitialWindowHeight = 480;

}  // namespace

bool ConnectedDevicesWindow::Initialize(HINSTANCE instanceHandle) {
    instanceHandle_ = instanceHandle;
    return CreateWindowClassAndHandle();
}

void ConnectedDevicesWindow::Shutdown() {
    if (windowHandle_) {
        DestroyWindow(windowHandle_);
        windowHandle_ = nullptr;
        contentHandle_ = nullptr;
    }
}

void ConnectedDevicesWindow::Show(const std::wstring &content) {
    if (!windowHandle_ || !contentHandle_) {
        return;
    }

    SetWindowTextW(contentHandle_, content.c_str());

    if (IsIconic(windowHandle_)) {
        ShowWindow(windowHandle_, SW_RESTORE);
    } else {
        ShowWindow(windowHandle_, SW_SHOWNORMAL);
    }

    SetForegroundWindow(windowHandle_);
}

LRESULT ConnectedDevicesWindow::WindowProc(HWND windowHandle, UINT message, WPARAM wParam, LPARAM lParam) {
    switch (message) {
        case WM_CREATE:
            return CreateContentControl(windowHandle) ? 0 : -1;
        case WM_SIZE:
            if (contentHandle_) {
                MoveWindow(contentHandle_, 0, 0, LOWORD(lParam), HIWORD(lParam), TRUE);
            }
            return 0;
        case WM_CLOSE:
            ShowWindow(windowHandle, SW_HIDE);
            return 0;
        case WM_DESTROY:
            contentHandle_ = nullptr;
            return 0;
        default:
            break;
    }

    return DefWindowProcW(windowHandle, message, wParam, lParam);
}

bool ConnectedDevicesWindow::CreateWindowClassAndHandle() {
    WNDCLASSEXW windowClass{};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.lpfnWndProc = &ConnectedDevicesWindow::StaticWindowProc;
    windowClass.hInstance = instanceHandle_;
    windowClass.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
    windowClass.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    windowClass.lpszClassName = kConnectedDevicesWindowClassName;

    if (!RegisterClassExW(&windowClass) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
        Log::Error("RegisterClassExW failed for connected devices window");
        return false;
    }

    windowHandle_ = CreateWindowExW(
        WS_EX_APPWINDOW,
        kConnectedDevicesWindowClassName,
        L"Подключенные устройства",
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT,
        CW_USEDEFAULT,
        kInitialWindowWidth,
        kInitialWindowHeight,
        nullptr,
        nullptr,
        instanceHandle_,
        this);

    if (!windowHandle_) {
        Log::Error("CreateWindowExW failed for connected devices window");
        return false;
    }

    if (!contentHandle_) {
        Log::Error("Failed to create connected devices content control");
        DestroyWindow(windowHandle_);
        windowHandle_ = nullptr;
        return false;
    }

    return true;
}

bool ConnectedDevicesWindow::CreateContentControl(HWND parentHandle) {
    contentHandle_ = CreateWindowExW(
        WS_EX_CLIENTEDGE,
        L"EDIT",
        L"",
        WS_CHILD | WS_VISIBLE | WS_VSCROLL | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL,
        0,
        0,
        0,
        0,
        parentHandle,
        nullptr,
        instanceHandle_,
        nullptr);

    if (!contentHandle_) {
        Log::Error("CreateWindowExW failed for connected devices edit control");
        return false;
    }

    SendMessageW(contentHandle_, WM_SETFONT, reinterpret_cast<WPARAM>(GetStockObject(DEFAULT_GUI_FONT)), TRUE);
    return true;
}

LRESULT CALLBACK ConnectedDevicesWindow::StaticWindowProc(HWND windowHandle, UINT message, WPARAM wParam, LPARAM lParam) {
    ConnectedDevicesWindow *self = nullptr;
    if (message == WM_NCCREATE) {
        const auto *createStruct = reinterpret_cast<CREATESTRUCTW *>(lParam);
        self = static_cast<ConnectedDevicesWindow *>(createStruct->lpCreateParams);
        SetWindowLongPtrW(windowHandle, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
    } else {
        self = reinterpret_cast<ConnectedDevicesWindow *>(GetWindowLongPtrW(windowHandle, GWLP_USERDATA));
    }

    if (!self) {
        return DefWindowProcW(windowHandle, message, wParam, lParam);
    }

    return self->WindowProc(windowHandle, message, wParam, lParam);
}
