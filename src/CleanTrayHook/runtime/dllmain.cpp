#include <windows.h>

#include "Activation.h"
#include "TestHooks.h"
#include "core/AppKey.h"

namespace ct {
HMODULE g_module = nullptr;
}

namespace {

void HookMessageGuarded() {
    __try {
        ct::OnHookMessage();
    } __except (EXCEPTION_EXECUTE_HANDLER) {
    }
}

BOOL TestActivateGuarded(HWND tray, const wchar_t* pipeName, const wchar_t* rulesName) {
    __try {
        return ct::Activate(tray, pipeName, rulesName) ? TRUE : FALSE;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return FALSE;
    }
}

int NormalizeGuarded(const wchar_t* exePath, const wchar_t* commandLine, wchar_t* out, int outChars) {
    __try {
        return ct::CopyAppKey(exePath, commandLine, out, outChars);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}

}  // namespace

// Static CRT: thread notifications stay enabled so the CRT can free per-thread data.
BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) ct::g_module = module;
    return TRUE;
}

extern "C" LRESULT CALLBACK CleanTrayGetMsgProc(int code, WPARAM wParam, LPARAM lParam) {
    if (code == HC_ACTION) HookMessageGuarded();
    return CallNextHookEx(nullptr, code, wParam, lParam);
}

// CleanTray keys apps it has never seen (Windows' own tray list) with this, so its keys can
// never drift from the ones the hook computes. Contract: ct::CopyAppKey.
extern "C" int WINAPI CleanTrayNormalizeAppKey(const wchar_t* exePath, const wchar_t* commandLine, wchar_t* out,
                                               int outChars) {
    return NormalizeGuarded(exePath, commandLine, out, outChars);
}

// The production activation path against a window and object names of the caller's
// choosing, so CleanTrayHook.RuntimeTests can drive the runtime without Explorer.
extern "C" BOOL WINAPI CleanTrayTestActivate(HWND tray, const wchar_t* pipeName, const wchar_t* rulesName) {
    return TestActivateGuarded(tray, pipeName, rulesName);
}

// Test-only: arms a one-shot structured exception in the tray handler (1), the pixel
// extractor (2) or the pipe worker (3).
extern "C" void WINAPI CleanTrayTestInjectFault(LONG where) { ct::ArmTestFault((ct::TestFault)where); }

// Test-only: aims the fault path's TaskbarCreated at one window instead of every app.
extern "C" void WINAPI CleanTrayTestSetFaultBroadcast(HWND target) { ct::SetFaultBroadcastTarget(target); }
