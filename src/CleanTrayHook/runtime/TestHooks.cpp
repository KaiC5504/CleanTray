#include "TestHooks.h"

namespace ct {
namespace {

constexpr DWORD kTestFaultCode = 0xE0435446;

volatile LONG g_armed = 0;
HWND volatile g_broadcastTarget = HWND_BROADCAST;

}  // namespace

void ArmTestFault(TestFault f) noexcept { InterlockedExchange(&g_armed, (LONG)f); }

void MaybeRaiseTestFault(TestFault f) {
    if (g_armed == (LONG)f && InterlockedCompareExchange(&g_armed, 0, (LONG)f) == (LONG)f)
        RaiseException(kTestFaultCode, 0, 0, nullptr);
}

HWND FaultBroadcastTarget() noexcept { return g_broadcastTarget; }

void SetFaultBroadcastTarget(HWND target) noexcept { g_broadcastTarget = target ? target : HWND_BROADCAST; }

}  // namespace ct
