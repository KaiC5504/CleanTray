#include "Check.h"

int main() {
    for (const auto& t : ct_test::Tests()) {
        int before = ct_test::Failures();
        t.fn();
        if (ct_test::Failures() != before) std::fprintf(stderr, "  in test %s\n", t.name);
    }
    std::printf("%zu tests, %d failed checks\n", ct_test::Tests().size(), ct_test::Failures());
    return ct_test::Failures();
}
