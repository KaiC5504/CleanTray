#include "Check.h"
#include "core/Wire.h"

using namespace ct;

namespace {
IconSnapshot FixtureSnapshot() {
    IconSnapshot s{};
    s.id = 42;
    s.pid = 1000;
    s.hwnd = 0x1234;
    s.uid = 7;
    s.hasGuid = false;
    s.callbackMsg = 0x8001;
    s.version = 4;
    s.state = 0;
    s.captured = true;
    s.appKey = L"c:\\a\\b.exe";
    s.exePath = L"C:\\A\\b.exe";
    s.tip = L"Tip";
    s.w = 1;
    s.h = 1;
    s.bgra = {1, 2, 3, 4};
    return s;
}
}  // namespace

TEST(Wire_IconUpsertMatchesFixture) {
    auto expected = LOAD_HEX("icon_upsert_v1.hex");
    CHECK(!expected.empty());
    CHECK(WriteIconUpsert(FixtureSnapshot()) == expected);
}

TEST(Wire_IconUpsertGuidByteOrderMatchesFixture) {
    IconSnapshot s = FixtureSnapshot();
    s.hasGuid = true;
    s.guid = {0x3b3905d1, 0xafd0, 0xca7e, {0x3c, 0xa2, 0xc9, 0xda, 0x57, 0x91, 0xfd, 0x04}};
    auto expected = LOAD_HEX("icon_upsert_guid_v1.hex");
    CHECK(!expected.empty());
    CHECK(WriteIconUpsert(s) == expected);
}

TEST(Wire_Hello) {
    auto b = WriteHello(0x1234);
    std::vector<BYTE> expected = {8, 0, 0, 0, 1, 0, 0x34, 0x12, 0, 0, 1, 0, 0, 0};
    CHECK(b == expected);
}

TEST(Wire_IconRemoved) {
    auto b = WriteIconRemoved(0x0102030405060708ull);
    std::vector<BYTE> expected = {8, 0, 0, 0, 3, 0, 8, 7, 6, 5, 4, 3, 2, 1};
    CHECK(b == expected);
}

TEST(Wire_Popup) {
    auto b = WritePopup(1, 2, L"T", L"XY");
    std::vector<BYTE> expected = {
        22, 0, 0, 0, 4, 0,
        1, 0, 0, 0, 0, 0, 0, 0,
        2, 0, 0, 0,
        1, 0, 'T', 0,
        2, 0, 'X', 0, 'Y', 0};
    CHECK(b == expected);
}

TEST(Wire_ClickCommandFromFixture) {
    auto bytes = LOAD_HEX("click_v1.hex");
    size_t consumed = 99;
    auto cmd = TryParseCommand(bytes.data(), bytes.size(), consumed);
    CHECK(cmd.has_value());
    CHECK_EQ(consumed, bytes.size());
    if (!cmd) return;
    CHECK_EQ(cmd->type, uint16_t{102});
    CHECK_EQ(cmd->iconId, uint64_t{42});
    CHECK_EQ(cmd->kind, uint8_t{3});
    CHECK_EQ(cmd->a, 100);
    CHECK_EQ(cmd->b, 200);
}

TEST(Wire_TruncatedCommandIsIncomplete) {
    auto bytes = LOAD_HEX("click_v1.hex");
    size_t consumed = 99;
    CHECK(!TryParseCommand(bytes.data(), bytes.size() - 1, consumed).has_value());
    CHECK_EQ(consumed, size_t{0});
    CHECK(!TryParseCommand(bytes.data(), 3, consumed).has_value());
    CHECK_EQ(consumed, size_t{0});
}

TEST(Wire_TwoCommandsBackToBack) {
    auto one = LOAD_HEX("click_v1.hex");
    std::vector<BYTE> two = one;
    const BYTE rules[] = {0, 0, 0, 0, 101, 0};
    two.insert(two.end(), rules, rules + 6);
    size_t consumed = 0;
    auto a = TryParseCommand(two.data(), two.size(), consumed);
    CHECK(a.has_value());
    CHECK_EQ(consumed, one.size());
    auto b = TryParseCommand(two.data() + consumed, two.size() - consumed, consumed);
    CHECK(b.has_value());
    CHECK_EQ(consumed, size_t{6});
    if (b) CHECK_EQ(b->type, uint16_t{101});
}

TEST(Wire_OtherCommandTypes) {
    size_t consumed = 0;
    const BYTE popup[] = {9, 0, 0, 0, 103, 0, 7, 0, 0, 0, 0, 0, 0, 0, 2};
    auto p = TryParseCommand(popup, sizeof popup, consumed);
    CHECK(p.has_value());
    if (p) {
        CHECK_EQ(p->iconId, uint64_t{7});
        CHECK_EQ(p->kind, uint8_t{2});
    }
    const BYTE anchor[] = {16, 0, 0, 0, 104, 0, 1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF};
    auto a = TryParseCommand(anchor, sizeof anchor, consumed);
    CHECK(a.has_value());
    if (a) {
        CHECK_EQ(a->a, 1);
        CHECK_EQ(a->b, 2);
        CHECK_EQ(a->c, 3);
        CHECK_EQ(a->d, -1);
    }
    const BYTE release[] = {0, 0, 0, 0, 105, 0};
    auto r = TryParseCommand(release, sizeof release, consumed);
    CHECK(r.has_value());
    if (r) CHECK_EQ(r->type, uint16_t{105});
}

TEST(Wire_UnknownTypeOrBadLengthDropsBuffer) {
    size_t consumed = 0;
    const BYTE unknown[] = {2, 0, 0, 0, 0x99, 0, 1, 2, 1, 1, 1};
    CHECK(!TryParseCommand(unknown, sizeof unknown, consumed).has_value());
    CHECK_EQ(consumed, sizeof unknown);

    const BYTE badLen[] = {3, 0, 0, 0, 102, 0, 1, 2, 3};
    CHECK(!TryParseCommand(badLen, sizeof badLen, consumed).has_value());
    CHECK_EQ(consumed, sizeof badLen);
}
