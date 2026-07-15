#include "App.h"

#include <windows.h>

namespace {

int RunDropMe(HINSTANCE instanceHandle, int showCommand) {
    App app;
    return app.Run(instanceHandle, showCommand);
}

}  // namespace

int WINAPI wWinMain(HINSTANCE instanceHandle, HINSTANCE, PWSTR, int showCommand) {
    return RunDropMe(instanceHandle, showCommand);
}

int main() {
    return RunDropMe(GetModuleHandleW(nullptr), SW_HIDE);
}
