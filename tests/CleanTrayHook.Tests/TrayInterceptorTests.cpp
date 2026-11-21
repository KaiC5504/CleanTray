#include "Check.h"
#include "TestRules.h"
#include "core/TrayInterceptor.h"

#include <map>
#include <set>
#include <utility>

using namespace ct;
using ct_test::BuildRules;
using ct_test::TestEntry;

namespace {

constexpr DWORD kCleanTrayPid = 900;
constexpr DWORD kAppPid = 100;
constexpr DWORD kHwnd = 0xF0806;
constexpr DWORD kOtherHwnd = 0xA0A0A;
constexpr DWORD kAppIcon = 0x299E085D;
constexpr UINT kCb = 0x8001;
const std::wstring kAppKey = L"c:\\a\\b.exe";
const std::wstring kOtherKey = L"c:\\other.exe";
const GUID kGuid = {0x3b3905d1, 0xafd0, 0xca7e, {0x3c, 0xa2, 0xc9, 0xda, 0x57, 0x91, 0xfd, 0x04}};

struct FakeHost final : ITrayHost {
    struct Forward {
        HWND sender;
        ULONG_PTR dwData;
        std::optional<TrayMessage> msg;
    };
    std::vector<Forward> forwards;
    std::vector<std::pair<DWORD, PostedMessage>> posts;
    std::map<DWORD, AppIdentity> apps;
    std::set<DWORD> dead;
    std::vector<DWORD> destroyed;
    std::vector<std::pair<char, DWORD>> log;  // 'F' forward with its hicon, 'D' destroy
    DWORD nextIcon = 0x5000;

    LRESULT ForwardToShell(HWND sender, ULONG_PTR dwData, const std::vector<BYTE>& payload) override {
        forwards.push_back({sender, dwData, ParseTrayCopyData(dwData, payload.data(), (DWORD)payload.size())});
        log.push_back({'F', forwards.back().msg ? forwards.back().msg->hicon : 0});
        return 1;
    }
    void PostToApp(DWORD hwnd, const PostedMessage& m) override { posts.push_back({hwnd, m}); }
    bool IsWindowAlive(DWORD hwnd) override { return dead.count(hwnd) == 0; }
    std::optional<AppIdentity> Identify(DWORD hwnd) override {
        auto it = apps.find(hwnd);
        if (it == apps.end()) return std::nullopt;
        return it->second;
    }
    DWORD CopyIconHandle(DWORD hicon) override { return hicon ? ++nextIcon : 0; }
    void DestroyIconHandle(DWORD hicon) override {
        destroyed.push_back(hicon);
        log.push_back({'D', hicon});
    }

    size_t CountForwarded(DWORD nim) const {
        size_t n = 0;
        for (const auto& f : forwards)
            if (f.msg && f.msg->nim == nim) ++n;
        return n;
    }
};

struct FakeSink final : IEventSink {
    struct PopupCall {
        uint64_t id;
        DWORD flags;
        std::wstring title, text;
    };
    std::vector<uint64_t> changed, removed;
    std::vector<PopupCall> popups;

    void IconChanged(uint64_t id) override { changed.push_back(id); }
    void IconRemoved(uint64_t id) override { removed.push_back(id); }
    void Popup(uint64_t id, DWORD infoFlags, const std::wstring& title, const std::wstring& text) override {
        popups.push_back({id, infoFlags, title, text});
    }
};

TrayMessage AddMsg(UINT uid = 1, DWORD hwnd = kHwnd) {
    TrayMessage m;
    m.hwnd = hwnd;
    m.uid = uid;
    m.flags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
    m.callbackMsg = kCb;
    m.hicon = kAppIcon;
    m.tip = L"probe";
    m.exePath = L"C:\\A\\b.exe";
    return m;
}

TrayMessage BareMsg(UINT uid = 1, DWORD hwnd = kHwnd) {
    TrayMessage m;
    m.hwnd = hwnd;
    m.uid = uid;
    return m;
}

struct Fixture {
    FakeHost host;
    FakeSink sink;
    RulesView rules;
    TrayInterceptor ti{host, sink, rules, kCleanTrayPid};

    explicit Fixture(std::vector<TestEntry> entries = {}) {
        SetRules(std::move(entries));
        host.apps[kHwnd] = {kAppPid, L"C:\\A\\b.exe", kAppKey};
        host.apps[kOtherHwnd] = {kAppPid + 1, L"C:\\other.exe", kOtherKey};
    }

    void SetRules(std::vector<TestEntry> entries, uint32_t flags = 0) {
        auto bytes = BuildRules(flags, entries);
        CHECK(rules.Load(bytes.data(), bytes.size()));
    }

    LRESULT Send(DWORD nim, const TrayMessage& m, bool& handled) {
        auto bytes = BuildTrayCopyData(nim, m);
        COPYDATASTRUCT cds{1, (DWORD)bytes.size(), bytes.data()};
        return ti.OnCopyData((HWND)(ULONG_PTR)m.hwnd, &cds, handled);
    }

    LRESULT Send(DWORD nim, const TrayMessage& m) {
        bool handled = false;
        return Send(nim, m, handled);
    }

    LRESULT AskRect(DWORD message, DWORD hwnd, UINT uid, const GUID& guid, bool& handled) {
        TrayRectData32 raw{kTraySignature, message, 32, 0, hwnd, uid, guid};
        COPYDATASTRUCT cds{3, sizeof raw, &raw};
        return ti.OnCopyData((HWND)(ULONG_PTR)hwnd, &cds, handled);
    }

    uint64_t OnlyId() {
        auto ids = ti.AllIds();
        CHECK_EQ(ids.size(), size_t{1});
        return ids.empty() ? 0 : ids[0];
    }
};

std::vector<TestEntry> Captured(uint8_t popups = 1) { return {{kAppKey, 1, popups}}; }

bool SamePosted(const PostedMessage& a, const PostedMessage& b) {
    return a.msg == b.msg && a.wParam == b.wParam && a.lParam == b.lParam;
}

}  // namespace

TEST(Interceptor_ShownAppPassesThrough) {
    Fixture f;
    bool handled = true;
    f.Send(NIM_ADD, AddMsg(), handled);
    CHECK(!handled);
    CHECK(f.host.forwards.empty());
    CHECK_EQ(f.sink.changed.size(), size_t{1});
    CHECK_EQ(f.ti.AllIds().size(), size_t{1});
}

TEST(Interceptor_CapturedAddIsSwallowed) {
    Fixture f(Captured());
    bool handled = false;
    LRESULT lr = f.Send(NIM_ADD, AddMsg(), handled);
    CHECK(handled);
    CHECK_EQ(lr, LRESULT{1});
    CHECK_EQ(f.host.forwards.size(), size_t{1});
    if (f.host.forwards.size() != 1) return;
    const auto& fwd = f.host.forwards[0];
    CHECK(fwd.sender == (HWND)(ULONG_PTR)kHwnd);
    CHECK_EQ(fwd.dwData, ULONG_PTR{1});
    CHECK(fwd.msg.has_value());
    if (!fwd.msg) return;
    CHECK_EQ(fwd.msg->nim, DWORD{NIM_DELETE});
    CHECK_EQ(fwd.msg->hwnd, kHwnd);
    CHECK_EQ(fwd.msg->uid, UINT{1});
    CHECK_EQ(f.sink.changed.size(), size_t{1});
}

TEST(Interceptor_CapturedFollowUpsAreSwallowedWithoutMoreDeletes) {
    Fixture f(Captured());
    f.Send(NIM_ADD, AddMsg());
    bool handled = false;
    TrayMessage v = BareMsg();
    v.version = 4;
    CHECK_EQ(f.Send(NIM_SETVERSION, v, handled), LRESULT{1});
    CHECK(handled);
    TrayMessage tip = BareMsg();
    tip.flags = NIF_TIP;
    tip.tip = L"new tip";
    handled = false;
    CHECK_EQ(f.Send(NIM_MODIFY, tip, handled), LRESULT{1});
    CHECK(handled);
    handled = false;
    CHECK_EQ(f.Send(NIM_SETFOCUS, BareMsg(), handled), LRESULT{1});
    CHECK(handled);
    CHECK_EQ(f.host.forwards.size(), size_t{1});

    IconSnapshot s{};
    DWORD hicon = 0;
    CHECK(f.ti.TryGetSnapshot(f.OnlyId(), s, hicon));
    CHECK_EQ(s.tip, std::wstring(L"new tip"));
    CHECK_EQ(s.version, UINT{4});
    CHECK(s.captured);
    CHECK_EQ(s.pid, kAppPid);
    CHECK_EQ(s.appKey, kAppKey);
    CHECK(hicon != 0 && hicon != kAppIcon);
}

TEST(Interceptor_UnknownCapturedModifyCreatesEntry) {
    Fixture f(Captured());
    TrayMessage m = BareMsg();
    m.flags = NIF_TIP;
    m.tip = L"before injection";
    bool handled = false;
    CHECK_EQ(f.Send(NIM_MODIFY, m, handled), LRESULT{1});
    CHECK(handled);
    CHECK_EQ(f.host.CountForwarded(NIM_DELETE), size_t{1});
    CHECK_EQ(f.ti.AllIds().size(), size_t{1});
}

TEST(Interceptor_OwnPidAlwaysPassesThrough) {
    Fixture f(Captured());
    f.host.apps[kHwnd].pid = kCleanTrayPid;
    bool handled = true;
    f.Send(NIM_ADD, AddMsg(), handled);
    CHECK(!handled);
    CHECK(f.host.forwards.empty());

    Fixture g(Captured());
    g.ti.SetCleanTrayPid(kAppPid);
    handled = true;
    g.Send(NIM_ADD, AddMsg(), handled);
    CHECK(!handled);
    CHECK(g.host.forwards.empty());
}

TEST(Interceptor_GarbagePassesThrough) {
    Fixture f(Captured());
    BYTE junk[3] = {1, 2, 3};
    COPYDATASTRUCT cds{1, 3, junk};
    bool handled = true;
    f.ti.OnCopyData(nullptr, &cds, handled);
    CHECK(!handled);

    handled = true;
    f.ti.OnCopyData(nullptr, nullptr, handled);
    CHECK(!handled);

    COPYDATASTRUCT empty{1, 1484, nullptr};
    handled = true;
    f.ti.OnCopyData(nullptr, &empty, handled);
    CHECK(!handled);

    COPYDATASTRUCT rect{3, 3, junk};
    handled = true;
    f.ti.OnCopyData(nullptr, &rect, handled);
    CHECK(!handled);

    CHECK(f.host.forwards.empty());
    CHECK(f.sink.changed.empty());
}

TEST(Interceptor_UnidentifiedWindowPassesThrough) {
    Fixture f(Captured());
    f.host.apps.clear();
    bool handled = true;
    f.Send(NIM_ADD, AddMsg(), handled);
    CHECK(!handled);
    CHECK(f.host.forwards.empty());
}

TEST(Interceptor_RuleFlipCapturesAndReleases) {
    Fixture f;
    f.Send(NIM_ADD, AddMsg());
    uint64_t id = f.OnlyId();
    f.sink.changed.clear();

    f.SetRules(Captured());
    f.ti.OnRulesChanged();
    CHECK_EQ(f.host.forwards.size(), size_t{1});
    CHECK_EQ(f.host.CountForwarded(NIM_DELETE), size_t{1});
    CHECK_EQ(f.sink.changed.size(), size_t{1});
    if (!f.sink.changed.empty()) CHECK_EQ(f.sink.changed[0], id);

    f.host.forwards.clear();
    f.SetRules({});
    f.ti.OnRulesChanged();
    CHECK(!f.host.forwards.empty());
    if (!f.host.forwards.empty() && f.host.forwards[0].msg)
        CHECK_EQ(f.host.forwards[0].msg->nim, DWORD{NIM_ADD});
    CHECK_EQ(f.sink.changed.size(), size_t{2});

    f.host.forwards.clear();
    f.ti.OnRulesChanged();
    CHECK(f.host.forwards.empty());
    CHECK_EQ(f.sink.changed.size(), size_t{2});
}

TEST(Interceptor_ReleaseSendsAddThenSetVersion) {
    Fixture f(Captured());
    f.Send(NIM_ADD, AddMsg());
    TrayMessage v = BareMsg();
    v.version = 4;
    f.Send(NIM_SETVERSION, v);
    f.host.forwards.clear();

    f.SetRules({});
    f.ti.OnRulesChanged();
    CHECK_EQ(f.host.forwards.size(), size_t{2});
    if (f.host.forwards.size() != 2) return;
    const auto& add = f.host.forwards[0];
    const auto& ver = f.host.forwards[1];
    CHECK(add.msg && ver.msg);
    if (!add.msg || !ver.msg) return;
    CHECK_EQ(add.msg->nim, DWORD{NIM_ADD});
    CHECK_EQ(ver.msg->nim, DWORD{NIM_SETVERSION});
    CHECK_EQ(ver.msg->version, UINT{4});
    CHECK(add.sender == (HWND)(ULONG_PTR)kHwnd);
    CHECK(ver.sender == (HWND)(ULONG_PTR)kHwnd);

    UINT want = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
    CHECK_EQ(add.msg->flags, want);
    CHECK_EQ(add.msg->callbackMsg, kCb);
    CHECK_EQ(add.msg->tip, std::wstring(L"probe"));
    CHECK_EQ(add.msg->exePath, std::wstring(L"C:\\A\\b.exe"));
    CHECK(add.msg->hicon != 0 && add.msg->hicon != kAppIcon);
}

TEST(Interceptor_ReleaseRestoresHiddenState) {
    Fixture f(Captured());
    f.Send(NIM_ADD, AddMsg());
    TrayMessage h = BareMsg();
    h.flags = NIF_STATE;
    h.state = NIS_HIDDEN;
    h.stateMask = NIS_HIDDEN;
    f.Send(NIM_MODIFY, h);
    f.host.forwards.clear();

    f.SetRules({});
    f.ti.OnRulesChanged();
    CHECK(!f.host.forwards.empty());
    if (f.host.forwards.empty() || !f.host.forwards[0].msg) return;
    const auto& add = *f.host.forwards[0].msg;
    CHECK_EQ(add.nim, DWORD{NIM_ADD});
    CHECK((add.flags & NIF_STATE) != 0);
    CHECK((add.state & NIS_HIDDEN) != 0);
    CHECK((add.stateMask & NIS_HIDDEN) != 0);
    CHECK_EQ(f.host.CountForwarded(NIM_SETVERSION), size_t{0});
}

TEST(Interceptor_GuidIconReleasesWithGuid) {
    Fixture f(Captured());
    TrayMessage m = AddMsg(5);
    m.flags |= NIF_GUID;
    m.guid = kGuid;
    f.Send(NIM_ADD, m);
    CHECK_EQ(f.host.forwards.size(), size_t{1});
    if (!f.host.forwards.empty() && f.host.forwards[0].msg) {
        CHECK((f.host.forwards[0].msg->flags & NIF_GUID) != 0);
        CHECK(IsEqualGUID(f.host.forwards[0].msg->guid, kGuid));
    }

    // A GUID icon keeps its identity even when the uid in a later message differs.
    TrayMessage v = BareMsg(99);
    v.flags = NIF_GUID;
    v.guid = kGuid;
    v.version = 4;
    f.Send(NIM_SETVERSION, v);
    CHECK_EQ(f.ti.AllIds().size(), size_t{1});

    f.host.forwards.clear();
    f.ti.ReleaseAll();
    CHECK_EQ(f.host.forwards.size(), size_t{2});
    for (const auto& fwd : f.host.forwards) {
        CHECK(fwd.msg.has_value());
        if (!fwd.msg) continue;
        CHECK((fwd.msg->flags & NIF_GUID) != 0);
        CHECK(IsEqualGUID(fwd.msg->guid, kGuid));
    }
}

TEST(Interceptor_CapturedDeleteRemoves) {
    Fixture f(Captured());
    f.Send(NIM_ADD, AddMsg());
    uint64_t id = f.OnlyId();
    IconSnapshot s{};
    DWORD copy = 0;
    CHECK(f.ti.TryGetSnapshot(id, s, copy));

    bool handled = false;
    LRESULT lr = f.Send(NIM_DELETE, BareMsg(), handled);
    CHECK(handled);
    CHECK_EQ(lr, LRESULT{1});
    CHECK_EQ(f.sink.removed.size(), size_t{1});
    if (!f.sink.removed.empty()) CHECK_EQ(f.sink.removed[0], id);
    CHECK(f.ti.AllIds().empty());
    CHECK(!f.ti.TryGetSnapshot(id, s, copy));
    CHECK_EQ(f.host.destroyed.size(), size_t{1});
    CHECK_EQ(f.host.CountForwarded(NIM_DELETE), size_t{1});
}

TEST(Interceptor_ShownDeletePassesThroughAndRemoves) {
    Fixture f;
    f.Send(NIM_ADD, AddMsg());
    bool handled = true;
    f.Send(NIM_DELETE, BareMsg(), handled);
    CHECK(!handled);
    CHECK_EQ(f.sink.removed.size(), size_t{1});
    CHECK(f.ti.AllIds().empty());
    CHECK(f.host.forwards.empty());
}

TEST(Interceptor_IconChangeReplacesCopy) {
    Fixture f(Captured());
    f.Send(NIM_ADD, AddMsg());
    IconSnapshot s{};
    DWORD first = 0, second = 0;
    CHECK(f.ti.TryGetSnapshot(f.OnlyId(), s, first));

    TrayMessage m = BareMsg();
    m.flags = NIF_ICON;
    m.hicon = 0x1234;
    f.Send(NIM_MODIFY, m);
    CHECK(f.ti.TryGetSnapshot(f.OnlyId(), s, second));
    CHECK(second != 0 && second != first);
    CHECK_EQ(f.host.destroyed.size(), size_t{1});
    if (!f.host.destroyed.empty()) CHECK_EQ(f.host.destroyed[0], first);
}

TEST(Interceptor_SweepRemovesDeadWindows) {
    Fixture f(Captured());
    f.Send(NIM_ADD, AddMsg());
    f.Send(NIM_ADD, AddMsg(1, kOtherHwnd));
    CHECK_EQ(f.ti.AllIds().size(), size_t{2});

    f.host.dead.insert(kHwnd);
    f.ti.SweepDead();
    CHECK_EQ(f.sink.removed.size(), size_t{1});
    CHECK_EQ(f.ti.AllIds().size(), size_t{1});
    IconSnapshot s{};
    DWORD copy = 0;
    auto ids = f.ti.AllIds();
    if (!ids.empty() && f.ti.TryGetSnapshot(ids[0], s, copy)) CHECK_EQ(s.hwnd, kOtherHwnd);
}

TEST(Interceptor_PopupAllowedGoesToSink) {
    Fixture f(Captured(1));
    f.Send(NIM_ADD, AddMsg());
    TrayMessage p = BareMsg();
    p.flags = NIF_INFO;
    p.info = L"X";
    p.infoTitle = L"T";
    p.infoFlags = NIIF_INFO;
    bool handled = false;
    CHECK_EQ(f.Send(NIM_MODIFY, p, handled), LRESULT{1});
    CHECK(handled);
    CHECK_EQ(f.sink.popups.size(), size_t{1});
    if (!f.sink.popups.empty()) {
        CHECK_EQ(f.sink.popups[0].id, f.OnlyId());
        CHECK_EQ(f.sink.popups[0].flags, DWORD{NIIF_INFO});
        CHECK_EQ(f.sink.popups[0].title, std::wstring(L"T"));
        CHECK_EQ(f.sink.popups[0].text, std::wstring(L"X"));
    }
    CHECK(f.host.posts.empty());

    // An empty szInfo dismisses the balloon; nothing to show.
    p.info.clear();
    f.Send(NIM_MODIFY, p);
    CHECK_EQ(f.sink.popups.size(), size_t{1});
    CHECK(f.host.posts.empty());
}

TEST(Interceptor_PopupBlockedTimesOut) {
    Fixture f(Captured(0));
    f.Send(NIM_ADD, AddMsg());
    TrayMessage v = BareMsg();
    v.version = 4;
    f.Send(NIM_SETVERSION, v);
    f.ti.SetAnchor({10, 20, 30, 40});

    TrayMessage p = BareMsg();
    p.flags = NIF_INFO;
    p.info = L"X";
    p.infoTitle = L"T";
    f.Send(NIM_MODIFY, p);
    CHECK(f.sink.popups.empty());
    CHECK_EQ(f.host.posts.size(), size_t{1});
    if (f.host.posts.empty()) return;
    CHECK_EQ(f.host.posts[0].first, kHwnd);
    CHECK(SamePosted(f.host.posts[0].second, BuildCallback(4, kCb, NIN_BALLOONTIMEOUT, 1, 20, 30)));
}

TEST(Interceptor_PopupResultPostsBalloonEvent) {
    Fixture f(Captured());
    f.Send(NIM_ADD, AddMsg());
    TrayMessage v = BareMsg();
    v.version = 4;
    f.Send(NIM_SETVERSION, v);
    f.ti.SetAnchor({10, 20, 30, 40});
    uint64_t id = f.OnlyId();

    f.ti.OnPopupResult(id, true);
    f.ti.OnPopupResult(id, false);
    f.ti.OnPopupResult(id + 100, true);
    CHECK_EQ(f.host.posts.size(), size_t{2});
    if (f.host.posts.size() != 2) return;
    CHECK(SamePosted(f.host.posts[0].second, BuildCallback(4, kCb, NIN_BALLOONUSERCLICK, 1, 20, 30)));
    CHECK(SamePosted(f.host.posts[1].second, BuildCallback(4, kCb, NIN_BALLOONTIMEOUT, 1, 20, 30)));
}

TEST(Interceptor_ClickPostsCallbacks) {
    Fixture f(Captured());
    f.Send(NIM_ADD, AddMsg());
    TrayMessage v = BareMsg();
    v.version = 4;
    f.Send(NIM_SETVERSION, v);

    f.ti.OnClick(f.OnlyId(), ClickKind::Right, 10, 20);
    auto want = BuildClickMessages(4, kCb, ClickKind::Right, 1, 10, 20);
    CHECK_EQ(f.host.posts.size(), want.size());
    for (size_t i = 0; i < want.size() && i < f.host.posts.size(); ++i) {
        CHECK_EQ(f.host.posts[i].first, kHwnd);
        CHECK(SamePosted(f.host.posts[i].second, want[i]));
    }
}

TEST(Interceptor_ReleaseAllReturnsEverything) {
    Fixture f(Captured());
    f.Send(NIM_ADD, AddMsg(1));
    f.Send(NIM_ADD, AddMsg(2));
    f.Send(NIM_ADD, AddMsg(1, kOtherHwnd));
    f.host.forwards.clear();
    f.host.destroyed.clear();

    f.ti.ReleaseAll();
    CHECK_EQ(f.host.CountForwarded(NIM_ADD), size_t{2});
    CHECK_EQ(f.host.CountForwarded(NIM_SETVERSION), size_t{0});
    CHECK(f.ti.AllIds().empty());
    CHECK_EQ(f.sink.removed.size(), size_t{3});
    CHECK_EQ(f.host.destroyed.size(), size_t{3});

    // Emptied, so a reactivation sees every icon again for the first time.
    f.host.forwards.clear();
    f.Send(NIM_MODIFY, AddMsg(1));
    CHECK_EQ(f.host.CountForwarded(NIM_DELETE), size_t{1});
}

TEST(Interceptor_ReleaseAllSkipsDeadWindows) {
    Fixture f(Captured());
    f.Send(NIM_ADD, AddMsg(1));
    f.Send(NIM_ADD, AddMsg(2));
    f.host.forwards.clear();
    f.host.dead.insert(kHwnd);

    f.ti.ReleaseAll();
    CHECK(f.host.forwards.empty());
    CHECK_EQ(f.sink.removed.size(), size_t{2});
    CHECK(f.ti.AllIds().empty());
}

TEST(Interceptor_FirstSightSetFocusDeletesOnce) {
    Fixture f(Captured());
    bool handled = false;
    CHECK_EQ(f.Send(NIM_SETFOCUS, BareMsg(), handled), LRESULT{1});
    CHECK(handled);
    CHECK_EQ(f.host.forwards.size(), size_t{1});
    CHECK_EQ(f.host.CountForwarded(NIM_DELETE), size_t{1});
    CHECK(f.ti.AllIds().empty());

    Fixture shown;
    handled = true;
    shown.Send(NIM_SETFOCUS, BareMsg(), handled);
    CHECK(!handled);
    CHECK(shown.host.forwards.empty());
}

TEST(Interceptor_EntryOfLaterOwnPidPassesThrough) {
    Fixture f;
    f.ti.SetCleanTrayPid(0);
    f.SetRules({}, 1);
    f.Send(NIM_ADD, AddMsg());
    CHECK_EQ(f.ti.AllIds().size(), size_t{1});
    f.host.forwards.clear();

    f.ti.SetCleanTrayPid(kAppPid);
    CHECK_EQ(f.host.CountForwarded(NIM_ADD), size_t{1});
    f.host.forwards.clear();
    TrayMessage tip = BareMsg();
    tip.flags = NIF_TIP;
    tip.tip = L"own";
    bool handled = true;
    f.Send(NIM_MODIFY, tip, handled);
    CHECK(!handled);

    f.SetRules(Captured());
    f.ti.OnRulesChanged();
    CHECK(f.host.forwards.empty());

    handled = true;
    f.Send(NIM_DELETE, BareMsg(), handled);
    CHECK(!handled);
}

TEST(Interceptor_GetRectAnswersAnchorForCaptured) {
    Fixture f(Captured());
    f.Send(NIM_ADD, AddMsg());
    f.Send(NIM_ADD, AddMsg(1, kOtherHwnd));
    TrayMessage g = AddMsg(5);
    g.flags |= NIF_GUID;
    g.guid = kGuid;
    f.Send(NIM_ADD, g);
    f.ti.SetAnchor({1, 2, 3, 4});

    bool handled = false;
    CHECK_EQ(f.AskRect(kRectSize, kHwnd, 1, GUID{}, handled), (LRESULT)MAKELONG(2, 2));
    CHECK(handled);
    handled = false;
    CHECK_EQ(f.AskRect(kRectTopLeft, kHwnd, 1, GUID{}, handled), (LRESULT)MAKELONG(1, 2));
    CHECK(handled);

    handled = false;
    CHECK_EQ(f.AskRect(kRectSize, 0, 0, kGuid, handled), (LRESULT)MAKELONG(2, 2));
    CHECK(handled);

    handled = true;
    f.AskRect(kRectSize, kOtherHwnd, 1, GUID{}, handled);
    CHECK(!handled);
    handled = true;
    f.AskRect(kRectSize, kHwnd, 77, GUID{}, handled);
    CHECK(!handled);

    f.ti.SetAnchor({});
    handled = false;
    CHECK_EQ(f.AskRect(kRectSize, kHwnd, 1, GUID{}, handled), LRESULT{0});
    CHECK(handled);
}

TEST(Interceptor_GetRectEncodesNegativeCoordinates) {
    Fixture f(Captured());
    f.Send(NIM_ADD, AddMsg());
    f.ti.SetAnchor({-1920, -40, -1872, 8});
    bool handled = false;
    LRESULT lr = f.AskRect(kRectTopLeft, kHwnd, 1, GUID{}, handled);
    CHECK(handled);
    CHECK_EQ((short)LOWORD(lr), short{-1920});
    CHECK_EQ((short)HIWORD(lr), short{-40});
}

TEST(Interceptor_ReleaseAllForwardsLiveIconHandles) {
    Fixture f(Captured());
    f.Send(NIM_ADD, AddMsg(1));
    f.Send(NIM_ADD, AddMsg(2));
    f.host.log.clear();

    f.ti.ReleaseAll();
    std::set<DWORD> gone;
    size_t addsWithIcon = 0;
    for (const auto& [kind, handle] : f.host.log) {
        if (kind == 'D') {
            gone.insert(handle);
        } else if (handle) {
            ++addsWithIcon;
            CHECK(gone.count(handle) == 0);
        }
    }
    CHECK_EQ(addsWithIcon, size_t{2});
    CHECK_EQ(gone.size(), size_t{2});
}

TEST(Interceptor_SetCleanTrayPidReleasesItsCapturedIcons) {
    Fixture f(Captured());
    f.ti.SetCleanTrayPid(0);
    f.Send(NIM_ADD, AddMsg());
    TrayMessage v = BareMsg();
    v.version = 4;
    f.Send(NIM_SETVERSION, v);
    f.Send(NIM_ADD, AddMsg(1, kOtherHwnd));
    f.host.forwards.clear();
    f.sink.changed.clear();

    f.ti.SetCleanTrayPid(kAppPid);
    CHECK_EQ(f.host.forwards.size(), size_t{2});
    if (f.host.forwards.size() == 2 && f.host.forwards[0].msg && f.host.forwards[1].msg) {
        CHECK_EQ(f.host.forwards[0].msg->nim, DWORD{NIM_ADD});
        CHECK_EQ(f.host.forwards[0].msg->hwnd, kHwnd);
        CHECK_EQ(f.host.forwards[1].msg->nim, DWORD{NIM_SETVERSION});
        CHECK_EQ(f.host.forwards[1].msg->version, UINT{4});
    }
    CHECK_EQ(f.ti.AllIds().size(), size_t{2});
    CHECK_EQ(f.sink.changed.size(), size_t{1});

    IconSnapshot s{};
    DWORD copy = 0;
    for (uint64_t id : f.ti.AllIds())
        if (f.ti.TryGetSnapshot(id, s, copy) && s.hwnd == kHwnd) CHECK(!s.captured);

    bool handled = true;
    f.Send(NIM_MODIFY, BareMsg(), handled);
    CHECK(!handled);
}

TEST(Interceptor_HasIconsForPid) {
    Fixture f(Captured());
    CHECK(!f.ti.HasIconsForPid(kAppPid));
    f.Send(NIM_ADD, AddMsg());
    CHECK(f.ti.HasIconsForPid(kAppPid));
    CHECK(!f.ti.HasIconsForPid(kAppPid + 1));

    f.Send(NIM_ADD, AddMsg(1, kOtherHwnd));
    CHECK(f.ti.HasIconsForPid(kAppPid + 1));
    f.Send(NIM_DELETE, BareMsg());
    CHECK(!f.ti.HasIconsForPid(kAppPid));

    Fixture shown;
    shown.Send(NIM_ADD, AddMsg());
    CHECK(shown.ti.HasIconsForPid(kAppPid));
}

TEST(Interceptor_SnapshotExtractorGetsLiveCopy) {
    Fixture f(Captured());
    f.Send(NIM_ADD, AddMsg());
    uint64_t id = f.OnlyId();
    DWORD liveCopy = f.host.nextIcon;

    IconSnapshot s{};
    DWORD seen = 0;
    bool ok = f.ti.TryGetSnapshot(id, s, [&](DWORD hicon, IconSnapshot& out) {
        seen = hicon;
        CHECK_EQ(out.id, id);
        CHECK(f.host.destroyed.empty());
        out.w = 1;
        out.h = 1;
        out.bgra = {1, 2, 3, 4};
    });
    CHECK(ok);
    CHECK_EQ(seen, liveCopy);
    CHECK(s.captured);
    CHECK_EQ(s.w, uint16_t{1});
    CHECK_EQ(s.bgra.size(), size_t{4});

    bool called = false;
    CHECK(!f.ti.TryGetSnapshot(id + 1, s, [&](DWORD, IconSnapshot&) { called = true; }));
    CHECK(!called);
}

TEST(Interceptor_SnapshotExtractorRunsUnderLock) {
    Fixture f(Captured());
    f.Send(NIM_ADD, AddMsg());
    uint64_t id = f.OnlyId();

    HANDLE swept = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE thread = nullptr;
    DWORD waitInside = WAIT_FAILED;
    struct Ctx {
        TrayInterceptor* ti;
        HANDLE done;
    } ctx{&f.ti, swept};

    IconSnapshot s{};
    f.ti.TryGetSnapshot(id, s, [&](DWORD, IconSnapshot&) {
        thread = CreateThread(
            nullptr, 0,
            [](void* p) -> DWORD {
                auto* c = static_cast<Ctx*>(p);
                c->ti->SweepDead();
                SetEvent(c->done);
                return 0;
            },
            &ctx, 0, nullptr);
        // SweepDead takes the exclusive lock, so it must not finish while we hold it shared.
        waitInside = WaitForSingleObject(swept, 100);
    });
    CHECK_EQ(waitInside, DWORD{WAIT_TIMEOUT});
    CHECK_EQ(WaitForSingleObject(swept, 5000), DWORD{WAIT_OBJECT_0});
    if (thread) {
        WaitForSingleObject(thread, 5000);
        CloseHandle(thread);
    }
    CloseHandle(swept);
}

TEST(Interceptor_SnapshotExtractorThrowIsContained) {
    Fixture f(Captured());
    f.Send(NIM_ADD, AddMsg());
    IconSnapshot s{};
    CHECK(!f.ti.TryGetSnapshot(f.OnlyId(), s, [](DWORD, IconSnapshot&) { throw std::bad_alloc(); }));
    // The lock was released on the way out.
    f.ti.SweepDead();
    CHECK_EQ(f.ti.AllIds().size(), size_t{1});
}

TEST(Interceptor_UnreadablePayloadPassesThrough) {
    Fixture f(Captured());
    void* page = VirtualAlloc(nullptr, 4096, MEM_RESERVE, PAGE_NOACCESS);
    CHECK(page != nullptr);

    COPYDATASTRUCT icon{kTrayDataIcon, kTrayFullSize, page};
    bool handled = true;
    CHECK_EQ(f.ti.OnCopyData(nullptr, &icon, handled), LRESULT{0});
    CHECK(!handled);

    COPYDATASTRUCT rect{kTrayDataRect, sizeof(TrayRectData32), page};
    handled = true;
    CHECK_EQ(f.ti.OnCopyData(nullptr, &rect, handled), LRESULT{0});
    CHECK(!handled);

    // Neither lock was left held.
    f.Send(NIM_ADD, AddMsg());
    CHECK_EQ(f.ti.AllIds().size(), size_t{1});
    VirtualFree(page, 0, MEM_RELEASE);
}
