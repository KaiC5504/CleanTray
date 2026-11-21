#include "Check.h"
#include "TestRules.h"
#include "core/RulesView.h"

#include <vector>

using namespace ct;

TEST(RulesView_ResolvesIconThenAppThenDefault) {
    auto bytes = LOAD_HEX("rules_v1.hex");
    RulesView rv;
    CHECK(rv.Load(bytes.data(), bytes.size()));
    CHECK(rv.Resolve(L"c:\\a\\b.exe", L"uid:1").captured == true && !rv.Resolve(L"c:\\a\\b.exe", L"uid:1").popups);
    CHECK(rv.Resolve(L"c:\\a\\b.exe", L"uid:7").captured == false && rv.Resolve(L"c:\\a\\b.exe", L"uid:7").popups);
    CHECK(rv.Resolve(L"c:\\other.exe", L"uid:1").captured == false && rv.Resolve(L"c:\\other.exe", L"uid:1").popups);
    CHECK(rv.AppCaptured(L"c:\\a\\b.exe"));
    CHECK(!rv.AppCaptured(L"c:\\other.exe"));
}

TEST(RulesView_BadInputKeepsPreviousState) {
    auto good = LOAD_HEX("rules_v1.hex");
    RulesView rv;
    CHECK(rv.Load(good.data(), good.size()));

    auto badMagic = good;
    badMagic[0] = 0;
    CHECK(!rv.Load(badMagic.data(), badMagic.size()));

    auto badVersion = good;
    badVersion[4] = 2;
    CHECK(!rv.Load(badVersion.data(), badVersion.size()));

    auto oddSeq = good;
    oddSeq[8] = 1;
    CHECK(!rv.Load(oddSeq.data(), oddSeq.size()));

    CHECK(!rv.Load(good.data(), good.size() - 1));
    CHECK(!rv.Load(good.data(), 10));
    CHECK(!rv.Load(nullptr, 0));

    CHECK(rv.Resolve(L"c:\\a\\b.exe", L"uid:1").captured);
    CHECK(rv.AppCaptured(L"c:\\a\\b.exe"));
}

TEST(RulesView_EntryLongerThanPayloadIsRejected) {
    auto bytes = LOAD_HEX("rules_v1.hex");
    bytes[24] = 0xff;
    RulesView rv;
    CHECK(!rv.Load(bytes.data(), bytes.size()));
}

TEST(RulesView_CaptureNewByDefaultFlag) {
    auto bytes = LOAD_HEX("rules_v1.hex");
    bytes[12] = 1;
    RulesView rv;
    CHECK(rv.Load(bytes.data(), bytes.size()));
    CHECK(rv.Resolve(L"c:\\other.exe", L"uid:1").captured);
    CHECK(rv.AppCaptured(L"c:\\other.exe"));
}

TEST(RulesView_EmptyViewUsesDefaults) {
    RulesView rv;
    CHECK(!rv.Resolve(L"c:\\x.exe", L"uid:1").captured);
    CHECK(rv.Resolve(L"c:\\x.exe", L"uid:1").popups);
}

using ct_test::BuildRules;

TEST(RulesView_AppCapturedTrueWhenAnyIconOverrideCaptured) {
    auto bytes = BuildRules(0, {{L"c:\\a\\b.exe", 0, 1}, {L"c:\\a\\b.exe#uid:7", 1, 0}});
    RulesView rv;
    CHECK(rv.Load(bytes.data(), bytes.size()));
    CHECK(rv.AppCaptured(L"c:\\a\\b.exe"));
    CHECK(!rv.AppCaptured(L"c:\\a\\bb.exe"));
}

TEST(RulesView_AppCapturedFalseWhenShownWithNoCapturedIcons) {
    auto bytes = BuildRules(1, {{L"c:\\a\\b.exe", 0, 1}, {L"c:\\a\\b.exe#uid:7", 0, 1}, {L"c:\\a\\b.exe.old#uid:1", 1, 1}});
    RulesView rv;
    CHECK(rv.Load(bytes.data(), bytes.size()));
    CHECK(!rv.AppCaptured(L"c:\\a\\b.exe"));
}
