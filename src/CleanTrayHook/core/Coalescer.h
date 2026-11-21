#pragma once
#include <windows.h>

#include <cstdint>
#include <unordered_set>
#include <vector>

namespace ct {

// Collects "this icon changed/was removed" marks from the tray thread so the
// pipe worker can drain them later. Marks never block on IO.
class Coalescer {
public:
    struct Batch {
        std::vector<uint64_t> changed, removed;
    };

    Coalescer();
    ~Coalescer();
    Coalescer(const Coalescer&) = delete;
    Coalescer& operator=(const Coalescer&) = delete;

    void MarkChanged(uint64_t id);
    void MarkRemoved(uint64_t id);
    Batch Drain();

    // Auto-reset event raised by every mark, for the worker to wait on.
    HANDLE Event() const { return event_; }

private:
    mutable SRWLOCK lock_ = SRWLOCK_INIT;
    std::unordered_set<uint64_t> changed_, removed_;
    HANDLE event_;
};

}  // namespace ct
