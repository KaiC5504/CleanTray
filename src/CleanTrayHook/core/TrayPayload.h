#pragma once
#include <windows.h>
#include <shellapi.h>

#include <optional>
#include <string>
#include <vector>

namespace ct {

inline constexpr ULONG_PTR kTrayDataIcon = 1;
inline constexpr DWORD kTraySignature = 0x34753423;

// Explorer's WM_COPYDATA payload for Shell_NotifyIcon, measured on Windows 11 build
// 26300: a header, a 32-bit NOTIFYICONDATAW whatever the sender's bitness, and a path
// block that Explorer fills on NIM_ADD only.
#pragma pack(push, 1)
struct TrayIconData32 {
    DWORD signature;
    DWORD nim;
    DWORD cbSize;
    DWORD hWnd;
    UINT uID;
    UINT uFlags;
    UINT uCallbackMessage;
    DWORD hIcon;
    WCHAR szTip[128];
    DWORD dwState;
    DWORD dwStateMask;
    WCHAR szInfo[256];
    DWORD uVersion;  // shares the offset with uTimeout
    WCHAR szInfoTitle[64];
    DWORD dwInfoFlags;
    GUID guidItem;
    DWORD hBalloonIcon;
    WCHAR exePath[260];
};
#pragma pack(pop)

static_assert(sizeof(TrayIconData32) == 1484);
static_assert(offsetof(TrayIconData32, szTip) == 32);
static_assert(offsetof(TrayIconData32, dwState) == 288);
static_assert(offsetof(TrayIconData32, szInfo) == 296);
static_assert(offsetof(TrayIconData32, uVersion) == 808);
static_assert(offsetof(TrayIconData32, szInfoTitle) == 812);
static_assert(offsetof(TrayIconData32, dwInfoFlags) == 940);
static_assert(offsetof(TrayIconData32, guidItem) == 944);
static_assert(offsetof(TrayIconData32, hBalloonIcon) == 960);
static_assert(offsetof(TrayIconData32, exePath) == 964);

inline constexpr DWORD kTrayMinSize = 964;
inline constexpr DWORD kTrayFullSize = sizeof(TrayIconData32);

struct TrayMessage {
    DWORD nim = 0;
    DWORD hwnd = 0;
    UINT uid = 0;
    UINT flags = 0;
    UINT callbackMsg = 0;
    DWORD hicon = 0;
    std::wstring tip;
    DWORD state = 0;
    DWORD stateMask = 0;
    std::wstring info;
    std::wstring infoTitle;
    DWORD infoFlags = 0;
    UINT version = 0;
    GUID guid{};
    std::wstring exePath;  // NIM_ADD only
};

std::optional<TrayMessage> ParseTrayCopyData(ULONG_PTR dwData, const void* data, DWORD cb);
std::vector<BYTE> BuildTrayCopyData(DWORD nim, const TrayMessage& m);

inline constexpr ULONG_PTR kTrayDataRect = 3;

// Shell_NotifyIconGetRect asks twice: kRectSize first, then kRectTopLeft only if the
// size answer was nonzero (measured on build 26300).
inline constexpr DWORD kRectTopLeft = 1;
inline constexpr DWORD kRectSize = 2;

#pragma pack(push, 1)
struct TrayRectData32 {
    DWORD signature;
    DWORD message;
    DWORD cbSize;
    DWORD unknown;
    DWORD hWnd;
    UINT uID;
    GUID guidItem;
};
#pragma pack(pop)

static_assert(sizeof(TrayRectData32) == 40);
static_assert(offsetof(TrayRectData32, hWnd) == 16);
static_assert(offsetof(TrayRectData32, guidItem) == 24);

struct TrayRectRequest {
    DWORD message = 0;
    DWORD hwnd = 0;
    UINT uid = 0;
    GUID guid{};
};

std::optional<TrayRectRequest> ParseTrayRectRequest(ULONG_PTR dwData, const void* data, DWORD cb);

}  // namespace ct
