#include "Check.h"
#include "core/Callbacks.h"

using namespace ct;

TEST(Callbacks_V4RightClick) {
    auto v4 = BuildClickMessages(4, 0x8001, ClickKind::Right, 7, 100, 200);
    CHECK_EQ(v4.size(), size_t{3});
    CHECK_EQ(v4[0].msg, UINT{0x8001});
    CHECK_EQ(LOWORD(v4[0].lParam), WORD{WM_RBUTTONDOWN});
    CHECK_EQ(HIWORD(v4[0].lParam), WORD{7});
    CHECK_EQ(LOWORD(v4[1].lParam), WORD{WM_RBUTTONUP});
    CHECK_EQ(v4[2].wParam, MAKEWPARAM(100, 200));
    CHECK_EQ(v4[2].lParam, MAKELPARAM(WM_CONTEXTMENU, 7));
}

TEST(Callbacks_V3LeftClickEndsWithSelect) {
    auto v3 = BuildClickMessages(3, 0x8001, ClickKind::Left, 7, 0, 0);
    CHECK_EQ(v3.size(), size_t{3});
    CHECK_EQ(v3[0].wParam, WPARAM{7});
    CHECK_EQ(v3[0].lParam, LPARAM{WM_LBUTTONDOWN});
    CHECK_EQ(v3[2].lParam, (LPARAM)NIN_SELECT);
    CHECK_EQ(v3[2].wParam, (WPARAM)7);
}

TEST(Callbacks_V0HasNoSelectOrContextMenu) {
    CHECK_EQ(BuildClickMessages(0, 0x8001, ClickKind::Left, 7, 0, 0).size(), size_t{2});
    CHECK_EQ(BuildClickMessages(0, 0x8001, ClickKind::Right, 7, 0, 0).size(), size_t{2});
}

TEST(Callbacks_DoubleClick) {
    auto dbl = BuildClickMessages(4, 0x8001, ClickKind::Double, 7, 1, 2);
    CHECK_EQ(dbl.size(), size_t{4});
    CHECK_EQ(LOWORD(dbl[0].lParam), WORD{WM_LBUTTONDOWN});
    CHECK_EQ(LOWORD(dbl[1].lParam), WORD{WM_LBUTTONUP});
    CHECK_EQ(LOWORD(dbl[2].lParam), WORD{WM_LBUTTONDBLCLK});
    CHECK_EQ(LOWORD(dbl[3].lParam), WORD{WM_LBUTTONUP});
}

TEST(Callbacks_BuildCallbackLegacyAndV4Packing) {
    auto legacy = BuildCallback(0, 0x8001, WM_MOUSEMOVE, 9, 5, 6);
    CHECK_EQ(legacy.wParam, WPARAM{9});
    CHECK_EQ(legacy.lParam, LPARAM{WM_MOUSEMOVE});
    auto v4 = BuildCallback(4, 0x8001, NIN_SELECT, 9, 5, 6);
    CHECK_EQ(v4.wParam, MAKEWPARAM(5, 6));
    CHECK_EQ(v4.lParam, MAKELPARAM(NIN_SELECT, 9));
}
