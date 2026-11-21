#pragma once
#include <string>
#include <string_view>

#include "TrayPayload.h"

namespace ct {

// Stable per-app identity that survives auto-updater folder renames. The C#
// side must produce byte-identical keys (invariant-culture lowercase).
std::wstring NormalizeAppKey(std::wstring_view exePath, std::wstring_view commandLine);

// The CleanTrayNormalizeAppKey export. Writes the NUL-terminated key and returns its length.
// When outChars has no room for it, returns the size needed (length + 1) and leaves an empty
// string if it can; (nullptr, 0) just asks for the size. -1 for bad arguments or out of memory.
int CopyAppKey(const wchar_t* exePath, const wchar_t* commandLine, wchar_t* out, int outChars) noexcept;

std::wstring IconPart(const TrayMessage& m);

}  // namespace ct
