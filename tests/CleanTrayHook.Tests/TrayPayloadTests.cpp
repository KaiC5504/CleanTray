#include "Check.h"
#include "core/TrayPayload.h"

#include <cstring>

using namespace ct;

namespace {
TrayMessage Sample() {
    TrayMessage m{};
    m.hwnd = 0xF0806;
    m.uid = 7;
    m.flags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID | NIF_INFO | NIF_STATE;
    m.callbackMsg = 0x8001;
    m.hicon = 0x299E085D;
    m.tip = L"h\u00e9llo";
    m.state = 1;
    m.stateMask = 1;
    m.info = L"body";
    m.infoTitle = L"title";
    m.infoFlags = 1;
    m.version = 4;
    m.guid = {0x3b3905d1, 0xafd0, 0xca7e, {0x3c, 0xa2, 0xc9, 0xda, 0x57, 0x91, 0xfd, 0x04}};
    m.exePath = L"C:\\A\\b.exe";
    return m;
}
}  // namespace

TEST(TrayPayload_LayoutMatchesMeasuredPayload) {
    CHECK_EQ(sizeof(TrayIconData32), size_t{1484});
}

TEST(TrayPayload_RoundTrip) {
    TrayMessage in = Sample();
    auto bytes = BuildTrayCopyData(NIM_ADD, in);
    CHECK_EQ(bytes.size(), size_t{1484});
    auto out = ParseTrayCopyData(1, bytes.data(), (DWORD)bytes.size());
    CHECK(out.has_value());
    if (!out) return;
    CHECK_EQ(out->nim, DWORD{NIM_ADD});
    CHECK_EQ(out->hwnd, in.hwnd);
    CHECK_EQ(out->uid, in.uid);
    CHECK_EQ(out->flags, in.flags);
    CHECK_EQ(out->callbackMsg, in.callbackMsg);
    CHECK_EQ(out->hicon, in.hicon);
    CHECK_EQ(out->tip, in.tip);
    CHECK_EQ(out->state, in.state);
    CHECK_EQ(out->stateMask, in.stateMask);
    CHECK_EQ(out->info, in.info);
    CHECK_EQ(out->infoTitle, in.infoTitle);
    CHECK_EQ(out->infoFlags, in.infoFlags);
    CHECK_EQ(out->version, in.version);
    CHECK(IsEqualGUID(out->guid, in.guid));
    CHECK_EQ(out->exePath, in.exePath);
}

TEST(TrayPayload_WireOffsets) {
    auto b = BuildTrayCopyData(NIM_MODIFY, Sample());
    auto u32 = [&](size_t off) {
        DWORD v;
        std::memcpy(&v, &b[off], 4);
        return v;
    };
    CHECK_EQ(u32(0), DWORD{0x34753423});
    CHECK_EQ(u32(4), DWORD{NIM_MODIFY});
    CHECK_EQ(u32(8), DWORD{956});
    CHECK_EQ(u32(12), DWORD{0xF0806});
    CHECK_EQ(u32(16), DWORD{7});
    CHECK_EQ(u32(24), DWORD{0x8001});
    CHECK_EQ(u32(28), DWORD{0x299E085D});
    CHECK_EQ(u32(808), DWORD{4});
    CHECK_EQ(u32(940), DWORD{1});
    CHECK_EQ(u32(944), DWORD{0x3b3905d1});
}

TEST(TrayPayload_ShortPayloadWithoutExePathParses) {
    auto bytes = BuildTrayCopyData(NIM_MODIFY, Sample());
    auto out = ParseTrayCopyData(1, bytes.data(), 964);
    CHECK(out.has_value());
    if (out) CHECK(out->exePath.empty());
}

TEST(TrayPayload_Rejects) {
    auto bytes = BuildTrayCopyData(NIM_ADD, Sample());
    CHECK(!ParseTrayCopyData(1, bytes.data(), 8).has_value());
    CHECK(!ParseTrayCopyData(1, bytes.data(), 963).has_value());
    CHECK(!ParseTrayCopyData(1, nullptr, 1484).has_value());
    CHECK(!ParseTrayCopyData(2, bytes.data(), (DWORD)bytes.size()).has_value());
    CHECK(!ParseTrayCopyData(3, bytes.data(), (DWORD)bytes.size()).has_value());
    bytes[0] ^= 0xFF;
    CHECK(!ParseTrayCopyData(1, bytes.data(), (DWORD)bytes.size()).has_value());
}

TEST(TrayPayload_UnterminatedStringsAreBounded) {
    auto bytes = BuildTrayCopyData(NIM_ADD, Sample());
    for (size_t i = 32; i < 32 + 256; ++i) bytes[i] = 'a';
    auto out = ParseTrayCopyData(1, bytes.data(), (DWORD)bytes.size());
    CHECK(out.has_value());
    if (out) CHECK_EQ(out->tip.size(), size_t{128});
}

TEST(TrayPayload_ParsesRectRequest) {
    auto bytes = LOAD_HEX("getrect_v1.hex");
    CHECK_EQ(bytes.size(), size_t{40});
    auto r = ParseTrayRectRequest(3, bytes.data(), (DWORD)bytes.size());
    CHECK(r.has_value());
    if (!r) return;
    CHECK_EQ(r->message, kRectSize);
    CHECK_EQ(r->hwnd, DWORD{0xF0806});
    CHECK_EQ(r->uid, UINT{1});
    const GUID expected = {0x3b3905d1, 0xafd0, 0xca7e, {0x3c, 0xa2, 0xc9, 0xda, 0x57, 0x91, 0xfd, 0x04}};
    CHECK(IsEqualGUID(r->guid, expected));
}

TEST(TrayPayload_RectRequestRejects) {
    auto bytes = LOAD_HEX("getrect_v1.hex");
    CHECK(!ParseTrayRectRequest(3, bytes.data(), 39).has_value());
    CHECK(!ParseTrayRectRequest(3, nullptr, 40).has_value());
    CHECK(!ParseTrayRectRequest(1, bytes.data(), (DWORD)bytes.size()).has_value());
    bytes[0] ^= 0xFF;
    CHECK(!ParseTrayRectRequest(3, bytes.data(), (DWORD)bytes.size()).has_value());
}
