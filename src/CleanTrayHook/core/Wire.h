#pragma once
#include <windows.h>

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace ct {

struct IconSnapshot {
    uint64_t id;
    DWORD pid, hwnd;
    UINT uid;
    bool hasGuid;
    GUID guid;
    UINT callbackMsg, version;
    DWORD state;
    bool captured;
    std::wstring appKey, exePath, tip;
    uint16_t w, h;
    std::vector<BYTE> bgra;
};

// Every frame is u32 payloadLength (excluding this 6-byte header), u16 type, payload.
std::vector<BYTE> WriteHello(DWORD explorerPid);
std::vector<BYTE> WriteIconUpsert(const IconSnapshot& s);
std::vector<BYTE> WriteIconRemoved(uint64_t id);
std::vector<BYTE> WritePopup(uint64_t id, DWORD infoFlags, std::wstring_view title,
                             std::wstring_view text);

struct Command {
    uint16_t type;
    uint64_t iconId;
    uint8_t kind;
    int32_t a, b, c, d;
};

// nullopt with consumed == 0: need more bytes. nullopt with consumed == len: the
// buffer is malformed (unknown type or wrong length) and the caller should drop it.
std::optional<Command> TryParseCommand(const BYTE* buf, size_t len, size_t& consumed);

}  // namespace ct
