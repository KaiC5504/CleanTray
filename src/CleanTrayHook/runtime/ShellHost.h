#pragma once
#include <windows.h>

#include <cstdint>
#include <optional>
#include <unordered_map>
#include <vector>

#include "core/TrayInterceptor.h"

namespace ct {

// Tray payloads carry 32-bit handles; user32 sign-extends them the same way.
inline HANDLE HandleFrom32(DWORD h) { return (HANDLE)(LONG_PTR)(LONG)h; }
inline HWND HwndFrom32(DWORD h) { return (HWND)HandleFrom32(h); }

// The real ITrayHost inside Explorer. ForwardToShell may only run on the tray thread
// from inside the subclass procedure; Identify and IdentifyPid are safe on any thread.
class ShellHost final : public ITrayHost {
public:
    explicit ShellHost(HWND tray) : tray_(tray) {}
    ShellHost(const ShellHost&) = delete;
    ShellHost& operator=(const ShellHost&) = delete;

    LRESULT ForwardToShell(HWND sender, ULONG_PTR dwData, const std::vector<BYTE>& payload) override;
    void PostToApp(DWORD hwnd, const PostedMessage& m) override;
    bool IsWindowAlive(DWORD hwnd) override;
    std::optional<AppIdentity> Identify(DWORD hwnd) override;
    DWORD CopyIconHandle(DWORD hicon) override;
    void DestroyIconHandle(DWORD hicon) override;

    std::optional<AppIdentity> IdentifyPid(DWORD pid);

private:
    struct Cached {
        ULONGLONG created;
        AppIdentity app;
    };

    HWND tray_;
    SRWLOCK cacheLock_ = SRWLOCK_INIT;
    std::unordered_map<DWORD, Cached> cache_;
};

// Straight-alpha, top-down BGRA. Returns false with w = h = 0 when the icon cannot be
// read or is larger than any tray icon could reasonably be.
bool ExtractBgra(DWORD hicon, uint16_t& w, uint16_t& h, std::vector<BYTE>& bgra);

}  // namespace ct
