#include "RulesView.h"

#include <cstring>

namespace ct {
namespace {

constexpr uint32_t kMagic = 0x4C525443;
constexpr uint32_t kVersion = 1;
constexpr size_t kHeaderSize = 24;
constexpr size_t kEntryHeaderSize = 4;

uint32_t ReadU32(const BYTE* p) {
    uint32_t v;
    std::memcpy(&v, p, sizeof v);
    return v;
}

}  // namespace

bool RulesView::Load(const BYTE* data, size_t size) {
    if (!data || size < kHeaderSize) return false;
    if (ReadU32(data) != kMagic || ReadU32(data + 4) != kVersion) return false;
    if (ReadU32(data + 8) & 1) return false;

    uint32_t flags = ReadU32(data + 12);
    uint32_t count = ReadU32(data + 16);
    uint32_t payloadBytes = ReadU32(data + 20);
    if (payloadBytes > size - kHeaderSize) return false;

    std::map<std::wstring, Entry, std::less<>> entries;
    const BYTE* p = data + kHeaderSize;
    const BYTE* end = p + payloadBytes;
    for (uint32_t i = 0; i < count; ++i) {
        if ((size_t)(end - p) < kEntryHeaderSize) return false;
        size_t keyLen = (size_t)p[0] | ((size_t)p[1] << 8);
        uint8_t mode = p[2];
        uint8_t popups = p[3];
        p += kEntryHeaderSize;
        if (mode > 1 || popups > 1) return false;
        if ((size_t)(end - p) < keyLen * sizeof(wchar_t)) return false;
        std::wstring key(keyLen, L'\0');
        std::memcpy(key.data(), p, keyLen * sizeof(wchar_t));
        p += keyLen * sizeof(wchar_t);
        entries[std::move(key)] = Entry{mode == 1, popups == 1};
    }
    if (p != end) return false;

    entries_ = std::move(entries);
    defaultCaptured_ = (flags & 1) != 0;
    return true;
}

Decision RulesView::Resolve(std::wstring_view appKey, std::wstring_view iconPart) const {
    std::wstring iconKey;
    iconKey.reserve(appKey.size() + 1 + iconPart.size());
    iconKey.append(appKey).push_back(L'#');
    iconKey.append(iconPart);

    if (auto it = entries_.find(iconKey); it != entries_.end())
        return {it->second.captured, it->second.popups};
    if (auto it = entries_.find(appKey); it != entries_.end())
        return {it->second.captured, it->second.popups};
    return {defaultCaptured_, true};
}

bool RulesView::AppCaptured(std::wstring_view appKey) const {
    auto app = entries_.find(appKey);
    if (app != entries_.end() && app->second.captured) return true;

    // Icon keys sort contiguously right after "<appKey>#".
    std::wstring prefix(appKey);
    prefix.push_back(L'#');
    for (auto it = entries_.lower_bound(prefix);
         it != entries_.end() && it->first.compare(0, prefix.size(), prefix) == 0; ++it) {
        if (it->second.captured) return true;
    }
    return app == entries_.end() && defaultCaptured_;
}

}  // namespace ct
