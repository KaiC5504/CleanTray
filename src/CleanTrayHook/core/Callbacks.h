#pragma once
#include <windows.h>
#include <shellapi.h>

#include <cstdint>
#include <vector>

namespace ct {

enum class ClickKind : uint8_t { Left = 1, Double = 2, Right = 3 };

struct PostedMessage {
    UINT msg;
    WPARAM wParam;
    LPARAM lParam;
};

// Packs an icon callback the way Explorer does for the icon's NOTIFYICON_VERSION.
PostedMessage BuildCallback(UINT version, UINT callbackMsg, UINT event, UINT uid, int x, int y);

// The message sequence a real click on a tray icon would deliver to its owner.
std::vector<PostedMessage> BuildClickMessages(UINT version, UINT callbackMsg, ClickKind kind,
                                              UINT uid, int x, int y);

}  // namespace ct
