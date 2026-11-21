#pragma once
#include <windows.h>

#include <string>
#include <vector>

#include "core/RulesView.h"

namespace ct {

// Seqlock reader of the rules table CleanTray publishes. One instance per thread.
class SharedRules {
public:
    explicit SharedRules(std::wstring mappingName) : name_(std::move(mappingName)) {}
    ~SharedRules();
    SharedRules(const SharedRules&) = delete;
    SharedRules& operator=(const SharedRules&) = delete;

    // Never waits. Leaves rules untouched (false) when the table does not exist yet,
    // stays mid-write for every retry, or fails to parse.
    bool Refresh(RulesView& rules) noexcept;

private:
    bool Open();

    std::wstring name_;
    HANDLE map_ = nullptr;
    const BYTE* view_ = nullptr;
    size_t size_ = 0;
    std::vector<BYTE> copy_;
};

}  // namespace ct
