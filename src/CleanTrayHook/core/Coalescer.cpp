#include "Coalescer.h"

#include <algorithm>

namespace ct {
namespace {

struct Guard {
    explicit Guard(SRWLOCK& l) : lock(l) { AcquireSRWLockExclusive(&lock); }
    ~Guard() { ReleaseSRWLockExclusive(&lock); }
    SRWLOCK& lock;
};

std::vector<uint64_t> SortedVector(const std::unordered_set<uint64_t>& s) {
    std::vector<uint64_t> v(s.begin(), s.end());
    std::sort(v.begin(), v.end());
    return v;
}

}  // namespace

Coalescer::Coalescer() : event_(CreateEventW(nullptr, FALSE, FALSE, nullptr)) {}

Coalescer::~Coalescer() {
    if (event_) CloseHandle(event_);
}

void Coalescer::MarkChanged(uint64_t id) {
    {
        Guard g(lock_);
        if (!removed_.count(id)) changed_.insert(id);
    }
    SetEvent(event_);
}

void Coalescer::MarkRemoved(uint64_t id) {
    {
        Guard g(lock_);
        changed_.erase(id);
        removed_.insert(id);
    }
    SetEvent(event_);
}

Coalescer::Batch Coalescer::Drain() {
    Guard g(lock_);
    Batch b{SortedVector(changed_), SortedVector(removed_)};
    changed_.clear();
    removed_.clear();
    return b;
}

}  // namespace ct
