#include "Wire.h"

#include <algorithm>
#include <cstring>

namespace ct {
namespace {

constexpr size_t kFrameHeader = 6;
constexpr uint32_t kProtocolVersion = 1;

enum EventType : uint16_t { kHello = 1, kIconUpsert = 2, kIconRemoved = 3, kPopup = 4 };
enum CommandType : uint16_t {
    kRulesChanged = 101,
    kClick = 102,
    kPopupResult = 103,
    kSetAnchor = 104,
    kReleaseAllAndIdle = 105,
};

class FrameWriter {
public:
    explicit FrameWriter(uint16_t type) {
        buf_.resize(kFrameHeader);
        std::memcpy(&buf_[4], &type, sizeof type);
    }

    template <class T>
    FrameWriter& Put(T v) {
        const BYTE* p = reinterpret_cast<const BYTE*>(&v);
        buf_.insert(buf_.end(), p, p + sizeof v);
        return *this;
    }

    FrameWriter& Str(std::wstring_view s) {
        // The wire length field is u16; longer strings are cut rather than corrupting the frame.
        uint16_t n = (uint16_t)(std::min)(s.size(), size_t{0xFFFF});
        Put(n);
        const BYTE* p = reinterpret_cast<const BYTE*>(s.data());
        buf_.insert(buf_.end(), p, p + n * sizeof(wchar_t));
        return *this;
    }

    FrameWriter& Bytes(const BYTE* p, size_t n) {
        buf_.insert(buf_.end(), p, p + n);
        return *this;
    }

    std::vector<BYTE> Finish() {
        uint32_t len = (uint32_t)(buf_.size() - kFrameHeader);
        std::memcpy(&buf_[0], &len, sizeof len);
        return std::move(buf_);
    }

private:
    std::vector<BYTE> buf_;
};

template <class T>
T Read(const BYTE*& p) {
    T v;
    std::memcpy(&v, p, sizeof v);
    p += sizeof v;
    return v;
}

// Payload size per command type, or -1 for an unknown type.
int ExpectedPayload(uint16_t type) {
    switch (type) {
        case kRulesChanged:
        case kReleaseAllAndIdle: return 0;
        case kClick: return 8 + 1 + 4 + 4;
        case kPopupResult: return 8 + 1;
        case kSetAnchor: return 16;
        default: return -1;
    }
}

}  // namespace

std::vector<BYTE> WriteHello(DWORD explorerPid) {
    return FrameWriter(kHello).Put<uint32_t>(explorerPid).Put<uint32_t>(kProtocolVersion).Finish();
}

std::vector<BYTE> WriteIconUpsert(const IconSnapshot& s) {
    FrameWriter w(kIconUpsert);
    w.Put<uint64_t>(s.id)
        .Put<uint32_t>(s.pid)
        .Put<uint32_t>(s.hwnd)
        .Put<uint32_t>(s.uid)
        .Put<uint8_t>(s.hasGuid ? 1 : 0)
        .Bytes(reinterpret_cast<const BYTE*>(&s.guid), sizeof s.guid)
        .Put<uint32_t>(s.callbackMsg)
        .Put<uint32_t>(s.version)
        .Put<uint32_t>(s.state)
        .Put<uint8_t>(s.captured ? 1 : 0)
        .Str(s.appKey)
        .Str(s.exePath)
        .Str(s.tip);

    // A pixel buffer that disagrees with w*h would desync the reader, so send no icon instead.
    size_t expected = (size_t)s.w * s.h * 4;
    if (s.bgra.size() == expected) {
        w.Put<uint16_t>(s.w).Put<uint16_t>(s.h).Bytes(s.bgra.data(), s.bgra.size());
    } else {
        w.Put<uint16_t>(0).Put<uint16_t>(0);
    }
    return w.Finish();
}

std::vector<BYTE> WriteIconRemoved(uint64_t id) {
    return FrameWriter(kIconRemoved).Put<uint64_t>(id).Finish();
}

std::vector<BYTE> WritePopup(uint64_t id, DWORD infoFlags, std::wstring_view title,
                             std::wstring_view text) {
    return FrameWriter(kPopup).Put<uint64_t>(id).Put<uint32_t>(infoFlags).Str(title).Str(text).Finish();
}

std::optional<Command> TryParseCommand(const BYTE* buf, size_t len, size_t& consumed) {
    consumed = 0;
    if (len < kFrameHeader) return std::nullopt;

    const BYTE* p = buf;
    uint32_t payloadLen = Read<uint32_t>(p);
    uint16_t type = Read<uint16_t>(p);

    int expected = ExpectedPayload(type);
    if (expected < 0 || payloadLen != (uint32_t)expected) {
        consumed = len;
        return std::nullopt;
    }
    if (len < kFrameHeader + payloadLen) return std::nullopt;

    Command c{};
    c.type = type;
    switch (type) {
        case kClick:
            c.iconId = Read<uint64_t>(p);
            c.kind = Read<uint8_t>(p);
            c.a = Read<int32_t>(p);
            c.b = Read<int32_t>(p);
            break;
        case kPopupResult:
            c.iconId = Read<uint64_t>(p);
            c.kind = Read<uint8_t>(p);
            break;
        case kSetAnchor:
            c.a = Read<int32_t>(p);
            c.b = Read<int32_t>(p);
            c.c = Read<int32_t>(p);
            c.d = Read<int32_t>(p);
            break;
        default: break;
    }
    consumed = kFrameHeader + payloadLen;
    return c;
}

}  // namespace ct
