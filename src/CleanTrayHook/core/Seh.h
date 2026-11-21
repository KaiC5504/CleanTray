#pragma once
#include <windows.h>

namespace ct {

// MSVC raises C++ exceptions as SEH code 0xE06D7363. __except filters let those pass so
// bad_alloc still reaches the C++ handlers and their destructors run.
inline int SehFilter(DWORD code) {
    return code == 0xE06D7363 ? EXCEPTION_CONTINUE_SEARCH : EXCEPTION_EXECUTE_HANDLER;
}

}  // namespace ct
