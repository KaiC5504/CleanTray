#include "Check.h"
#include "core/Coalescer.h"

#include <thread>

using namespace ct;

TEST(Coalescer_CollapsesRepeatedMarks) {
    Coalescer c;
    for (int i = 0; i < 1000; ++i) c.MarkChanged(5);
    c.MarkChanged(6);
    c.MarkRemoved(6);
    auto b = c.Drain();
    CHECK_EQ(b.changed, std::vector<uint64_t>{5});
    CHECK_EQ(b.removed, std::vector<uint64_t>{6});
    CHECK(c.Drain().changed.empty());
}

TEST(Coalescer_RemovedWinsEvenIfChangedLater) {
    Coalescer c;
    c.MarkRemoved(1);
    c.MarkChanged(1);
    auto b = c.Drain();
    CHECK(b.changed.empty());
    CHECK_EQ(b.removed, std::vector<uint64_t>{1});
}

TEST(Coalescer_EventSignalsPendingWork) {
    Coalescer c;
    CHECK_EQ(WaitForSingleObject(c.Event(), 0), DWORD{WAIT_TIMEOUT});
    c.MarkChanged(1);
    CHECK_EQ(WaitForSingleObject(c.Event(), 0), DWORD{WAIT_OBJECT_0});
    CHECK_EQ(WaitForSingleObject(c.Event(), 0), DWORD{WAIT_TIMEOUT});
    c.MarkRemoved(1);
    CHECK_EQ(WaitForSingleObject(c.Event(), 0), DWORD{WAIT_OBJECT_0});
}

TEST(Coalescer_ConcurrentMarks) {
    Coalescer c;
    auto work = [&](uint64_t base) {
        for (uint64_t i = 0; i < 2000; ++i) c.MarkChanged(base + i % 50);
    };
    std::thread t1(work, 0), t2(work, 25), t3(work, 100);
    t1.join();
    t2.join();
    t3.join();
    auto b = c.Drain();
    CHECK_EQ(b.changed.size(), size_t{125});
}
