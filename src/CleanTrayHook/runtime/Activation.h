#pragma once
#include <windows.h>

namespace ct {

extern HMODULE g_module;

// Called from the WH_GETMESSAGE hook on every message: activates on Explorer's tray
// thread, using the production pipe and rules names, if not already active.
void OnHookMessage() noexcept;

// Activates against the given window, which must belong to the calling thread.
// False if it does not, if already active, or if setup failed.
bool Activate(HWND tray, const wchar_t* pipeName, const wchar_t* rulesName) noexcept;

}  // namespace ct
