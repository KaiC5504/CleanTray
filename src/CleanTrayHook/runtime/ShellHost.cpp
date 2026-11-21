#include "ShellHost.h"

#include <commctrl.h>
#include <winternl.h>

#include <string>

#include "core/AppKey.h"

namespace ct {
namespace {

constexpr int kMaxIconSide = 256;
constexpr size_t kMaxCachedProcesses = 512;
constexpr PROCESSINFOCLASS kProcessCommandLineInformation = (PROCESSINFOCLASS)60;
constexpr NTSTATUS kStatusInfoLengthMismatch = (NTSTATUS)0xC0000004L;

using NtQueryInformationProcessFn = NTSTATUS(NTAPI*)(HANDLE, PROCESSINFOCLASS, PVOID, ULONG, PULONG);

struct HandleGuard {
    HANDLE h;
    ~HandleGuard() {
        if (h) CloseHandle(h);
    }
};

std::wstring ImagePath(HANDLE process) {
    for (DWORD cap : {DWORD{MAX_PATH}, DWORD{32768}}) {
        std::wstring buf(cap, L'\0');
        DWORD n = cap;
        if (QueryFullProcessImageNameW(process, 0, buf.data(), &n)) {
            buf.resize(n);
            return buf;
        }
        if (GetLastError() != ERROR_INSUFFICIENT_BUFFER) break;
    }
    return {};
}

std::wstring CommandLine(HANDLE process) {
    static const auto query = reinterpret_cast<NtQueryInformationProcessFn>(
        GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryInformationProcess"));
    if (!query) return {};

    std::vector<BYTE> buf(1024);
    for (int attempt = 0; attempt < 2; ++attempt) {
        ULONG needed = 0;
        NTSTATUS st = query(process, kProcessCommandLineInformation, buf.data(), (ULONG)buf.size(), &needed);
        if (st == kStatusInfoLengthMismatch && needed > buf.size() && needed <= 128 * 1024) {
            buf.resize(needed);
            continue;
        }
        if (st < 0 || buf.size() < sizeof(UNICODE_STRING)) return {};
        const auto* us = reinterpret_cast<const UNICODE_STRING*>(buf.data());
        const BYTE* start = reinterpret_cast<const BYTE*>(us->Buffer);
        if (!start || start < buf.data() || start + us->Length > buf.data() + buf.size()) return {};
        return std::wstring(us->Buffer, us->Length / sizeof(wchar_t));
    }
    return {};
}

struct IconBitmaps {
    ICONINFO ii{};
    ~IconBitmaps() {
        if (ii.hbmColor) DeleteObject(ii.hbmColor);
        if (ii.hbmMask) DeleteObject(ii.hbmMask);
    }
};

struct MemoryDc {
    HDC dc = CreateCompatibleDC(nullptr);
    ~MemoryDc() {
        if (dc) DeleteDC(dc);
    }
};

bool ReadBits(HDC dc, HBITMAP bmp, int w, int h, std::vector<BYTE>& out) {
    BITMAPINFO bi{};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = w;
    bi.bmiHeader.biHeight = -h;
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    out.assign((size_t)w * h * 4, 0);
    return GetDIBits(dc, bmp, 0, (UINT)h, out.data(), &bi, DIB_RGB_COLORS) == h;
}

}  // namespace

LRESULT ShellHost::ForwardToShell(HWND sender, ULONG_PTR dwData, const std::vector<BYTE>& payload) {
    COPYDATASTRUCT cds{dwData, (DWORD)payload.size(), const_cast<BYTE*>(payload.data())};
    return DefSubclassProc(tray_, WM_COPYDATA, (WPARAM)sender, (LPARAM)&cds);
}

void ShellHost::PostToApp(DWORD hwnd, const PostedMessage& m) {
    PostMessageW(HwndFrom32(hwnd), m.msg, m.wParam, m.lParam);
}

bool ShellHost::IsWindowAlive(DWORD hwnd) { return hwnd && IsWindow(HwndFrom32(hwnd)); }

std::optional<AppIdentity> ShellHost::Identify(DWORD hwnd) {
    DWORD pid = 0;
    if (!hwnd || !GetWindowThreadProcessId(HwndFrom32(hwnd), &pid) || !pid) return std::nullopt;
    return IdentifyPid(pid);
}

std::optional<AppIdentity> ShellHost::IdentifyPid(DWORD pid) {
    HandleGuard process{OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)};
    if (!process.h) return std::nullopt;
    FILETIME created, exited, kernel, user;
    if (!GetProcessTimes(process.h, &created, &exited, &kernel, &user)) return std::nullopt;
    // Creation time tells a reused pid apart from the process we cached.
    ULONGLONG stamp = ((ULONGLONG)created.dwHighDateTime << 32) | created.dwLowDateTime;

    AcquireSRWLockShared(&cacheLock_);
    auto it = cache_.find(pid);
    std::optional<AppIdentity> hit;
    if (it != cache_.end() && it->second.created == stamp) {
        try {
            hit = it->second.app;
        } catch (...) {
        }
    }
    ReleaseSRWLockShared(&cacheLock_);
    if (hit) return hit;

    std::wstring path = ImagePath(process.h);
    if (path.empty()) return std::nullopt;
    AppIdentity app{pid, path, NormalizeAppKey(path, CommandLine(process.h))};

    AcquireSRWLockExclusive(&cacheLock_);
    try {
        if (cache_.size() >= kMaxCachedProcesses) cache_.clear();
        cache_[pid] = Cached{stamp, app};
    } catch (...) {
    }
    ReleaseSRWLockExclusive(&cacheLock_);
    return app;
}

DWORD ShellHost::CopyIconHandle(DWORD hicon) {
    HICON copy = CopyIcon((HICON)HandleFrom32(hicon));
    return (DWORD)(ULONG_PTR)copy;
}

void ShellHost::DestroyIconHandle(DWORD hicon) {
    if (hicon) DestroyIcon((HICON)HandleFrom32(hicon));
}

bool ExtractBgra(DWORD hicon, uint16_t& w, uint16_t& h, std::vector<BYTE>& bgra) {
    w = h = 0;
    bgra.clear();
    if (!hicon) return false;

    IconBitmaps bitmaps;
    const ICONINFO& ii = bitmaps.ii;
    if (!GetIconInfo((HICON)HandleFrom32(hicon), &bitmaps.ii)) return false;
    HBITMAP sizeSource = ii.hbmColor ? ii.hbmColor : ii.hbmMask;
    BITMAP bm{};
    if (!sizeSource || !GetObjectW(sizeSource, sizeof bm, &bm)) return false;
    int width = bm.bmWidth;
    int height = ii.hbmColor ? bm.bmHeight : bm.bmHeight / 2;
    if (width <= 0 || height <= 0 || width > kMaxIconSide || height > kMaxIconSide) return false;

    MemoryDc mem;
    if (!mem.dc) return false;
    const size_t count = (size_t)width * height;
    std::vector<BYTE> px, mask;
    if (ii.hbmColor) {
        if (!ReadBits(mem.dc, ii.hbmColor, width, height, px)) return false;
        bool hasAlpha = false;
        for (size_t i = 0; i < count && !hasAlpha; ++i) hasAlpha = px[i * 4 + 3] != 0;
        if (!hasAlpha) {
            // Icons without an alpha channel carry their transparency in the AND mask.
            if (!ii.hbmMask || !ReadBits(mem.dc, ii.hbmMask, width, height, mask)) return false;
            for (size_t i = 0; i < count; ++i) px[i * 4 + 3] = mask[i * 4] ? 0 : 255;
        }
    } else {
        // Monochrome icons stack the AND mask above the XOR mask in a single bitmap.
        if (!ReadBits(mem.dc, ii.hbmMask, width, height * 2, mask)) return false;
        px.assign(count * 4, 0);
        for (size_t i = 0; i < count; ++i) {
            bool andBit = mask[i * 4] != 0;
            bool xorBit = mask[(count + i) * 4] != 0;
            if (andBit && !xorBit) continue;
            // Screen-inverting pixels have no straight-alpha equivalent; draw them black.
            BYTE v = (xorBit && !andBit) ? 255 : 0;
            px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = v;
            px[i * 4 + 3] = 255;
        }
    }
    w = (uint16_t)width;
    h = (uint16_t)height;
    bgra = std::move(px);
    return true;
}

}  // namespace ct
