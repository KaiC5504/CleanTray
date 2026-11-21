#include "Callbacks.h"

namespace ct {

PostedMessage BuildCallback(UINT version, UINT callbackMsg, UINT event, UINT uid, int x, int y) {
    if (version >= 4)
        return {callbackMsg, MAKEWPARAM(x, y), MAKELPARAM(event, uid)};
    return {callbackMsg, (WPARAM)uid, (LPARAM)event};
}

std::vector<PostedMessage> BuildClickMessages(UINT version, UINT callbackMsg, ClickKind kind,
                                              UINT uid, int x, int y) {
    auto cb = [&](UINT event) { return BuildCallback(version, callbackMsg, event, uid, x, y); };
    std::vector<PostedMessage> out;
    switch (kind) {
        case ClickKind::Left:
            out = {cb(WM_LBUTTONDOWN), cb(WM_LBUTTONUP)};
            if (version >= 3) out.push_back(cb(NIN_SELECT));
            break;
        case ClickKind::Double:
            out = {cb(WM_LBUTTONDOWN), cb(WM_LBUTTONUP), cb(WM_LBUTTONDBLCLK), cb(WM_LBUTTONUP)};
            break;
        case ClickKind::Right:
            out = {cb(WM_RBUTTONDOWN), cb(WM_RBUTTONUP)};
            if (version >= 3) out.push_back(cb(WM_CONTEXTMENU));
            break;
    }
    return out;
}

}  // namespace ct
