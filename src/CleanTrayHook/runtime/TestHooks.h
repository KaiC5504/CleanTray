#pragma once
#include <windows.h>

namespace ct {

enum class TestFault : LONG { None = 0, TrayHandler = 1, Extractor = 2, Worker = 3 };

// Armed only through the CleanTrayTest* exports, so production never raises these.
void ArmTestFault(TestFault f) noexcept;
// Raises a structured exception if f is armed, disarming it.
void MaybeRaiseTestFault(TestFault f);

// Where a fault tells apps to re-add their icons; HWND_BROADCAST outside tests, so the
// runtime tests do not make every app on the desktop re-add its icons.
HWND FaultBroadcastTarget() noexcept;
void SetFaultBroadcastTarget(HWND target) noexcept;

}  // namespace ct
