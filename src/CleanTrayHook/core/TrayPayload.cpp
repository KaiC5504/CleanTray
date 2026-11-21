#include "TrayPayload.h"

#include <algorithm>
#include <cstring>

namespace ct {
namespace {

template <size_t N>
std::wstring ReadFixed(const WCHAR (&a)[N]) {
    size_t n = 0;
    while (n < N && a[n] != 0) ++n;
    return std::wstring(a, n);
}

template <size_t N>
void WriteFixed(WCHAR (&a)[N], const std::wstring& s) {
    size_t n = (std::min)(s.size(), N - 1);
    std::copy_n(s.data(), n, a);
}

}  // namespace

std::optional<TrayMessage> ParseTrayCopyData(ULONG_PTR dwData, const void* data, DWORD cb) {
    if (dwData != kTrayDataIcon || !data || cb < kTrayMinSize) return std::nullopt;

    // Copy into a zeroed struct so a short payload reads as zeros past cb.
    TrayIconData32 raw{};
    std::memcpy(&raw, data, (std::min)(cb, kTrayFullSize));
    if (raw.signature != kTraySignature) return std::nullopt;

    TrayMessage m;
    m.nim = raw.nim;
    m.hwnd = raw.hWnd;
    m.uid = raw.uID;
    m.flags = raw.uFlags;
    m.callbackMsg = raw.uCallbackMessage;
    m.hicon = raw.hIcon;
    m.tip = ReadFixed(raw.szTip);
    m.state = raw.dwState;
    m.stateMask = raw.dwStateMask;
    m.info = ReadFixed(raw.szInfo);
    m.infoTitle = ReadFixed(raw.szInfoTitle);
    m.infoFlags = raw.dwInfoFlags;
    m.version = raw.uVersion;
    m.guid = raw.guidItem;
    if (cb >= kTrayFullSize) m.exePath = ReadFixed(raw.exePath);
    return m;
}

std::vector<BYTE> BuildTrayCopyData(DWORD nim, const TrayMessage& m) {
    TrayIconData32 raw{};
    raw.signature = kTraySignature;
    raw.nim = nim;
    raw.cbSize = offsetof(TrayIconData32, exePath) - offsetof(TrayIconData32, cbSize);
    raw.hWnd = m.hwnd;
    raw.uID = m.uid;
    raw.uFlags = m.flags;
    raw.uCallbackMessage = m.callbackMsg;
    raw.hIcon = m.hicon;
    WriteFixed(raw.szTip, m.tip);
    raw.dwState = m.state;
    raw.dwStateMask = m.stateMask;
    WriteFixed(raw.szInfo, m.info);
    raw.uVersion = m.version;
    WriteFixed(raw.szInfoTitle, m.infoTitle);
    raw.dwInfoFlags = m.infoFlags;
    raw.guidItem = m.guid;
    WriteFixed(raw.exePath, m.exePath);

    const BYTE* p = reinterpret_cast<const BYTE*>(&raw);
    return std::vector<BYTE>(p, p + sizeof raw);
}

std::optional<TrayRectRequest> ParseTrayRectRequest(ULONG_PTR dwData, const void* data, DWORD cb) {
    if (dwData != kTrayDataRect || !data || cb < sizeof(TrayRectData32)) return std::nullopt;

    TrayRectData32 raw;
    std::memcpy(&raw, data, sizeof raw);
    if (raw.signature != kTraySignature) return std::nullopt;
    return TrayRectRequest{raw.message, raw.hWnd, raw.uID, raw.guidItem};
}

}  // namespace ct
