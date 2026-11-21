#pragma once
#include <windows.h>

#include <functional>
#include <map>
#include <string>
#include <string_view>

namespace ct {

struct Decision {
    bool captured;
    bool popups;
};

// Parsed copy of the rules table (wire format in the plan). Not thread-safe:
// the owner swaps whole instances rather than mutating a shared one.
class RulesView {
public:
    // The caller supplies a consistent snapshot; an odd seq (writer mid-update)
    // is rejected so a torn read never replaces good state.
    bool Load(const BYTE* data, size_t size);

    Decision Resolve(std::wstring_view appKey, std::wstring_view iconPart) const;

    // Whether any icon of this app may be captured, i.e. worth kicking Explorer for:
    // the app entry is Captured, any of its icon overrides is Captured, or there is
    // no app entry and new apps are captured by default.
    bool AppCaptured(std::wstring_view appKey) const;

private:
    struct Entry {
        bool captured;
        bool popups;
    };

    std::map<std::wstring, Entry, std::less<>> entries_;
    bool defaultCaptured_ = false;
};

}  // namespace ct
