#include "SharedRules.h"

#include <atomic>
#include <cstdint>
#include <cstring>

namespace ct {
namespace {

constexpr size_t kHeaderSize = 24;
constexpr size_t kSeqOffset = 8;
constexpr size_t kPayloadOffset = 20;
constexpr int kMaxAttempts = 100;

// The view is memory another process controls; a fault reading it must not reach Explorer.
bool SafeCopy(void* dst, const void* src, size_t n) {
    __try {
        std::memcpy(dst, src, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool SafeReadSeq(const BYTE* view, uint32_t& seq) {
    __try {
        seq = *reinterpret_cast<const volatile uint32_t*>(view + kSeqOffset);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

}  // namespace

SharedRules::~SharedRules() {
    if (view_) UnmapViewOfFile(view_);
    if (map_) CloseHandle(map_);
}

bool SharedRules::Open() {
    map_ = OpenFileMappingW(FILE_MAP_READ, FALSE, name_.c_str());
    if (!map_) return false;
    view_ = static_cast<const BYTE*>(MapViewOfFile(map_, FILE_MAP_READ, 0, 0, 0));
    MEMORY_BASIC_INFORMATION mbi{};
    if (view_ && VirtualQuery(view_, &mbi, sizeof mbi) == sizeof mbi && mbi.RegionSize >= kHeaderSize) {
        size_ = mbi.RegionSize;
        return true;
    }
    if (view_) UnmapViewOfFile(view_);
    CloseHandle(map_);
    view_ = nullptr;
    map_ = nullptr;
    return false;
}

bool SharedRules::Refresh(RulesView& rules) noexcept {
    try {
        if (!view_ && !Open()) return false;
        for (int attempt = 0; attempt < kMaxAttempts; ++attempt) {
            if (attempt) YieldProcessor();
            uint32_t before = 0;
            if (!SafeReadSeq(view_, before)) return false;
            if (before & 1) continue;

            BYTE header[kHeaderSize];
            if (!SafeCopy(header, view_, kHeaderSize)) return false;
            uint32_t payload;
            std::memcpy(&payload, header + kPayloadOffset, sizeof payload);
            if (payload > size_ - kHeaderSize) continue;

            size_t n = kHeaderSize + payload;
            copy_.resize(n);
            if (!SafeCopy(copy_.data(), view_, n)) return false;
            std::atomic_thread_fence(std::memory_order_acquire);
            uint32_t after = 0;
            if (!SafeReadSeq(view_, after)) return false;
            if (after != before) continue;
            return rules.Load(copy_.data(), n);
        }
    } catch (...) {
    }
    return false;
}

}  // namespace ct
