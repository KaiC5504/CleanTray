#pragma once
#include <windows.h>

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

namespace ct_test {

struct TestEntry {
    std::wstring key;
    uint8_t mode, popups;
};

// Rules table bytes in the rules_v1.hex format.
inline std::vector<BYTE> BuildRules(uint32_t flags, const std::vector<TestEntry>& entries) {
    std::vector<BYTE> body;
    for (const auto& e : entries) {
        uint16_t len = (uint16_t)e.key.size();
        body.push_back((BYTE)(len & 0xFF));
        body.push_back((BYTE)(len >> 8));
        body.push_back(e.mode);
        body.push_back(e.popups);
        const BYTE* p = reinterpret_cast<const BYTE*>(e.key.data());
        body.insert(body.end(), p, p + e.key.size() * 2);
    }
    std::vector<BYTE> out(24);
    auto put = [&](size_t off, uint32_t v) { std::memcpy(&out[off], &v, 4); };
    put(0, 0x4C525443);
    put(4, 1);
    put(8, 0);
    put(12, flags);
    put(16, (uint32_t)entries.size());
    put(20, (uint32_t)body.size());
    out.insert(out.end(), body.begin(), body.end());
    return out;
}

}  // namespace ct_test
