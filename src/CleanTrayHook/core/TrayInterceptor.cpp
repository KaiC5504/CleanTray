#include "TrayInterceptor.h"

#include <utility>

#include "AppKey.h"
#include "Seh.h"

namespace ct {
namespace {

constexpr size_t kNone = static_cast<size_t>(-1);

class ExclusiveLock {
public:
    explicit ExclusiveLock(SRWLOCK& l) : l_(l) { AcquireSRWLockExclusive(&l_); }
    ~ExclusiveLock() { ReleaseSRWLockExclusive(&l_); }
    ExclusiveLock(const ExclusiveLock&) = delete;
    ExclusiveLock& operator=(const ExclusiveLock&) = delete;

private:
    SRWLOCK& l_;
};

class SharedLock {
public:
    explicit SharedLock(SRWLOCK& l) : l_(l) { AcquireSRWLockShared(&l_); }
    ~SharedLock() { ReleaseSRWLockShared(&l_); }
    SharedLock(const SharedLock&) = delete;
    SharedLock& operator=(const SharedLock&) = delete;

private:
    SRWLOCK& l_;
};

bool IsZero(const GUID& g) { return IsEqualGUID(g, GUID{}); }

TrayMessage IdentityOnly(const TrayMessage& s, bool hasGuid) {
    TrayMessage m;
    m.hwnd = s.hwnd;
    m.uid = s.uid;
    if (hasGuid) {
        m.flags = NIF_GUID;
        m.guid = s.guid;
    }
    return m;
}

void ParseIconInto(const COPYDATASTRUCT& cds, std::optional<TrayMessage>& out) {
    out = ParseTrayCopyData(cds.dwData, cds.lpData, cds.cbData);
}

// A fault while parsing must be caught here, below the table lock's guard, so the lock
// is released; /EHsc would skip the guard's destructor if the fault unwound past it.
bool SafeParseIcon(const COPYDATASTRUCT& cds, std::optional<TrayMessage>& out) {
    __try {
        ParseIconInto(cds, out);
        return true;
    } __except (SehFilter(GetExceptionCode())) {
        return false;
    }
}

bool SafeParseRect(const COPYDATASTRUCT& cds, std::optional<TrayRectRequest>& out) {
    __try {
        out = ParseTrayRectRequest(cds.dwData, cds.lpData, cds.cbData);
        return true;
    } __except (SehFilter(GetExceptionCode())) {
        return false;
    }
}

}  // namespace

// Host and sink calls are queued while the table lock is held and run after it is
// released: ForwardToShell re-enters Explorer's window procedure, and SRW locks are
// not recursive.
struct TrayInterceptor::Effects {
    enum class Kind : uint8_t { Forward, Post, Changed, Removed, Popup, DestroyIcon };
    struct Item {
        Kind kind;
        DWORD hwnd = 0;
        uint64_t id = 0;
        std::vector<BYTE> payload;
        PostedMessage posted{};
        DWORD infoFlags = 0;
        std::wstring title, text;
    };
    std::vector<Item> items;

    // Each item is complete before it is queued, so a bad_alloc can never leave a
    // half-built one behind (e.g. a forward with an empty payload).
    void Forward(DWORD nim, const TrayMessage& m) {
        Item i{Kind::Forward};
        i.hwnd = m.hwnd;
        i.payload = BuildTrayCopyData(nim, m);
        items.push_back(std::move(i));
    }
    void Post(DWORD hwnd, const PostedMessage& p) {
        Item i{Kind::Post};
        i.hwnd = hwnd;
        i.posted = p;
        items.push_back(std::move(i));
    }
    void Changed(uint64_t id) {
        Item i{Kind::Changed};
        i.id = id;
        items.push_back(std::move(i));
    }
    void Removed(uint64_t id) {
        Item i{Kind::Removed};
        i.id = id;
        items.push_back(std::move(i));
    }
    // Queued rather than immediate: a release NIM_ADD queued earlier may carry this handle.
    void DestroyIcon(DWORD hicon) {
        Item i{Kind::DestroyIcon};
        i.hwnd = hicon;
        items.push_back(std::move(i));
    }
    void Popup(uint64_t id, DWORD infoFlags, const std::wstring& title, const std::wstring& text) {
        Item i{Kind::Popup};
        i.id = id;
        i.infoFlags = infoFlags;
        i.title = title;
        i.text = text;
        items.push_back(std::move(i));
    }
};

TrayInterceptor::TrayInterceptor(ITrayHost& host, IEventSink& sink, const RulesView& rules,
                                 DWORD cleanTrayPid)
    : host_(host), sink_(sink), rules_(rules), cleanTrayPid_(cleanTrayPid) {}

TrayInterceptor::~TrayInterceptor() {
    for (const Icon& icon : icons_)
        if (icon.hiconCopy) host_.DestroyIconHandle(icon.hiconCopy);
}

// Before the pipe connects the pid is 0, so CleanTray's own icon may have been
// captured meanwhile; hand it back now that we know whose it is.
void TrayInterceptor::SetCleanTrayPid(DWORD pid) noexcept {
    Effects fx;
    try {
        ExclusiveLock lock(lock_);
        cleanTrayPid_ = pid;
        for (Icon& icon : icons_) {
            if (icon.app.pid != pid || !icon.captured) continue;
            Release(icon, fx);
            fx.Changed(icon.id);
        }
    } catch (...) {
    }
    Run(fx);
}

LRESULT TrayInterceptor::OnCopyData(HWND, const COPYDATASTRUCT* cds, bool& handled) noexcept {
    handled = false;
    if (!cds) return 0;
    LRESULT lr = 0;
    Effects fx;
    try {
        if (cds->dwData == kTrayDataRect) {
            SharedLock lock(lock_);
            return OnRectRequest(*cds, handled);
        }
        ExclusiveLock lock(lock_);
        lr = OnIconMessage(*cds, handled, fx);
    } catch (...) {
        // Out of memory inside Explorer: let Explorer have the message as if we were absent.
        handled = false;
        lr = 0;
    }
    // Effects already queued describe table changes that did happen, so run them anyway.
    Run(fx);
    return lr;
}

LRESULT TrayInterceptor::OnIconMessage(const COPYDATASTRUCT& cds, bool& handled, Effects& fx) {
    std::optional<TrayMessage> m;
    if (!SafeParseIcon(cds, m) || !m) return 0;
    const bool mergeable = m->nim == NIM_ADD || m->nim == NIM_MODIFY || m->nim == NIM_SETVERSION;
    const bool hasGuid = (m->flags & NIF_GUID) != 0;

    size_t i = Find(hasGuid, m->guid, m->hwnd, m->uid);
    if (i == kNone) {
        if (!mergeable && m->nim != NIM_SETFOCUS) return 0;
        auto app = host_.Identify(m->hwnd);
        if (!app || app->pid == cleanTrayPid_) return 0;

        if (m->nim == NIM_SETFOCUS) {
            if (!rules_.Resolve(app->appKey, IconPart(*m)).captured) return 0;
            // Same safety delete as any first sight; focus has no state worth an entry.
            fx.Forward(NIM_DELETE, IdentityOnly(*m, hasGuid));
            handled = true;
            return 1;
        }

        Icon fresh;
        fresh.hasGuid = hasGuid;
        fresh.msg = IdentityOnly(*m, hasGuid);
        fresh.captured = rules_.Resolve(app->appKey, IconPart(*m)).captured;
        fresh.inShell = !fresh.captured;
        fresh.app = std::move(*app);
        icons_.push_back(std::move(fresh));
        i = icons_.size() - 1;
        icons_[i].id = nextId_++;
        // Explorer may still hold this icon from before we were injected; deleting
        // an icon it does not have is harmless.
        if (icons_[i].captured) fx.Forward(NIM_DELETE, icons_[i].msg);
    }

    Icon& icon = icons_[i];
    // The entry predates SetCleanTrayPid for a new CleanTray process; never touch its icons.
    if (icon.app.pid == cleanTrayPid_) {
        if (m->nim == NIM_DELETE) Drop(i, fx);
        return 0;
    }
    if (m->nim == NIM_DELETE) {
        handled = icon.captured;
        Drop(i, fx);
        return handled ? 1 : 0;
    }
    if (!icon.captured) {
        if (mergeable) {
            Merge(icon, *m);
            fx.Changed(icon.id);
        }
        return 0;
    }
    if (m->nim == NIM_SETFOCUS) {
        handled = true;
        return 1;
    }
    if (!mergeable) return 0;

    Merge(icon, *m);
    fx.Changed(icon.id);
    if (m->nim != NIM_SETVERSION) HandlePopup(icon, *m, fx);
    handled = true;
    return 1;
}

LRESULT TrayInterceptor::OnRectRequest(const COPYDATASTRUCT& cds, bool& handled) const {
    std::optional<TrayRectRequest> r;
    if (!SafeParseRect(cds, r) || !r) return 0;
    bool byGuid = !IsZero(r->guid);
    size_t i = Find(byGuid, r->guid, r->hwnd, r->uid);
    if (i == kNone || !icons_[i].captured) return 0;

    const RECT& a = anchor_;
    if (r->message == kRectSize) {
        handled = true;
        // A zero size makes shell32 fail the call without asking for the position.
        if (a.right <= a.left || a.bottom <= a.top) return 0;
        return (LRESULT)(DWORD)MAKELONG(a.right - a.left, a.bottom - a.top);
    }
    if (r->message == kRectTopLeft) {
        handled = true;
        return (LRESULT)(DWORD)MAKELONG((short)a.left, (short)a.top);
    }
    return 0;
}

void TrayInterceptor::Merge(Icon& icon, const TrayMessage& m) {
    TrayMessage& s = icon.msg;
    // GUID icons may move to a new window (an app restart re-adds the same GUID).
    if (m.hwnd) {
        s.hwnd = m.hwnd;
        s.uid = m.uid;
    }
    s.flags |= m.flags;
    if (m.flags & NIF_MESSAGE) s.callbackMsg = m.callbackMsg;
    if (m.flags & NIF_TIP) s.tip = m.tip;
    if (m.flags & NIF_STATE) {
        s.state = (s.state & ~m.stateMask) | (m.state & m.stateMask);
        s.stateMask |= m.stateMask;
        icon.hasState = true;
    }
    if (m.flags & NIF_GUID) s.guid = m.guid;
    if (m.nim == NIM_SETVERSION) s.version = m.version;
    if (!m.exePath.empty()) s.exePath = m.exePath;
    if (m.flags & NIF_ICON) {
        s.hicon = m.hicon;
        DWORD copy = m.hicon ? host_.CopyIconHandle(m.hicon) : 0;
        // On a failed copy the stale image beats none.
        if (copy || !m.hicon) {
            if (icon.hiconCopy) host_.DestroyIconHandle(icon.hiconCopy);
            icon.hiconCopy = copy;
        }
    }
}

void TrayInterceptor::HandlePopup(const Icon& icon, const TrayMessage& m, Effects& fx) const {
    // An empty szInfo is the app dismissing its balloon.
    if (!(m.flags & NIF_INFO) || m.info.empty()) return;
    if (rules_.Resolve(icon.app.appKey, IconPart(icon.msg)).popups) {
        fx.Popup(icon.id, m.infoFlags, m.infoTitle, m.info);
        return;
    }
    if (!icon.msg.callbackMsg) return;
    POINT pt = AnchorPoint();
    fx.Post(icon.msg.hwnd, BuildCallback(icon.msg.version, icon.msg.callbackMsg, NIN_BALLOONTIMEOUT,
                                         icon.msg.uid, pt.x, pt.y));
}

void TrayInterceptor::Release(Icon& icon, Effects& fx) const {
    const TrayMessage& s = icon.msg;
    TrayMessage add = IdentityOnly(s, icon.hasGuid);
    add.flags |= NIF_MESSAGE | NIF_ICON | NIF_TIP;
    if (s.flags & NIF_SHOWTIP) add.flags |= NIF_SHOWTIP;
    if (icon.hasState) {
        add.flags |= NIF_STATE;
        add.state = s.state;
        add.stateMask = s.stateMask;
    }
    add.callbackMsg = s.callbackMsg;
    add.hicon = icon.hiconCopy ? icon.hiconCopy : s.hicon;
    add.tip = s.tip;
    add.exePath = s.exePath.empty() ? icon.app.exePath : s.exePath;
    fx.Forward(NIM_ADD, add);

    if (s.version) {
        TrayMessage ver = IdentityOnly(s, icon.hasGuid);
        ver.version = s.version;
        fx.Forward(NIM_SETVERSION, ver);
    }
    icon.captured = false;
    icon.inShell = true;
}

void TrayInterceptor::Hide(Icon& icon, Effects& fx) const {
    if (icon.inShell) fx.Forward(NIM_DELETE, IdentityOnly(icon.msg, icon.hasGuid));
    icon.captured = true;
    icon.inShell = false;
}

void TrayInterceptor::Drop(size_t index, Effects& fx) {
    Icon& icon = icons_[index];
    if (icon.hiconCopy) fx.DestroyIcon(icon.hiconCopy);
    fx.Removed(icon.id);
    icons_.erase(icons_.begin() + (ptrdiff_t)index);
}

POINT TrayInterceptor::AnchorPoint() const {
    return {anchor_.left + (anchor_.right - anchor_.left) / 2,
            anchor_.top + (anchor_.bottom - anchor_.top) / 2};
}

size_t TrayInterceptor::Find(bool hasGuid, const GUID& guid, DWORD hwnd, UINT uid) const {
    for (size_t i = 0; i < icons_.size(); ++i) {
        const Icon& icon = icons_[i];
        if (icon.hasGuid != hasGuid) continue;
        if (hasGuid ? IsEqualGUID(icon.msg.guid, guid)
                    : icon.msg.hwnd == hwnd && icon.msg.uid == uid)
            return i;
    }
    return kNone;
}

size_t TrayInterceptor::FindId(uint64_t id) const {
    for (size_t i = 0; i < icons_.size(); ++i)
        if (icons_[i].id == id) return i;
    return kNone;
}

void TrayInterceptor::OnRulesChanged() noexcept {
    Effects fx;
    try {
        ExclusiveLock lock(lock_);
        for (Icon& icon : icons_) {
            if (icon.app.pid == cleanTrayPid_) continue;
            bool want = rules_.Resolve(icon.app.appKey, IconPart(icon.msg)).captured;
            if (want == icon.captured) continue;
            if (want)
                Hide(icon, fx);
            else
                Release(icon, fx);
            fx.Changed(icon.id);
        }
    } catch (...) {
    }
    Run(fx);
}

void TrayInterceptor::OnClick(uint64_t id, ClickKind kind, int x, int y) noexcept {
    Effects fx;
    try {
        SharedLock lock(lock_);
        size_t i = FindId(id);
        if (i == kNone || !icons_[i].msg.callbackMsg) return;
        const TrayMessage& s = icons_[i].msg;
        for (const auto& p : BuildClickMessages(s.version, s.callbackMsg, kind, s.uid, x, y))
            fx.Post(s.hwnd, p);
    } catch (...) {
        return;
    }
    Run(fx);
}

void TrayInterceptor::OnPopupResult(uint64_t id, bool clicked) noexcept {
    Effects fx;
    try {
        SharedLock lock(lock_);
        size_t i = FindId(id);
        if (i == kNone || !icons_[i].msg.callbackMsg) return;
        const TrayMessage& s = icons_[i].msg;
        POINT pt = AnchorPoint();
        fx.Post(s.hwnd, BuildCallback(s.version, s.callbackMsg,
                                      clicked ? NIN_BALLOONUSERCLICK : NIN_BALLOONTIMEOUT, s.uid,
                                      pt.x, pt.y));
    } catch (...) {
        return;
    }
    Run(fx);
}

void TrayInterceptor::SetAnchor(RECT r) noexcept {
    ExclusiveLock lock(lock_);
    anchor_ = r;
}

// Leaves the table empty so a later reactivation starts from first sight again.
void TrayInterceptor::ReleaseAll() noexcept {
    Effects fx;
    try {
        ExclusiveLock lock(lock_);
        for (size_t i = icons_.size(); i-- > 0;)
            if (!host_.IsWindowAlive(icons_[i].msg.hwnd)) Drop(i, fx);
        for (Icon& icon : icons_)
            if (icon.captured) Release(icon, fx);
        while (!icons_.empty()) Drop(icons_.size() - 1, fx);
    } catch (...) {
    }
    Run(fx);
}

void TrayInterceptor::SweepDead() noexcept {
    Effects fx;
    try {
        ExclusiveLock lock(lock_);
        for (size_t i = icons_.size(); i-- > 0;)
            if (!host_.IsWindowAlive(icons_[i].msg.hwnd)) Drop(i, fx);
    } catch (...) {
    }
    Run(fx);
}

void TrayInterceptor::FillSnapshot(const Icon& icon, IconSnapshot& out) const {
    const TrayMessage& s = icon.msg;
    out.id = icon.id;
    out.pid = icon.app.pid;
    out.hwnd = s.hwnd;
    out.uid = s.uid;
    out.hasGuid = icon.hasGuid;
    out.guid = s.guid;
    out.callbackMsg = s.callbackMsg;
    out.version = s.version;
    out.state = s.state;
    out.captured = icon.captured;
    out.appKey = icon.app.appKey;
    out.exePath = s.exePath.empty() ? icon.app.exePath : s.exePath;
    out.tip = s.tip;
    out.w = out.h = 0;
    out.bgra.clear();
}

bool TrayInterceptor::TryGetSnapshot(uint64_t id, IconSnapshot& out, DWORD& hiconCopy) const noexcept {
    try {
        SharedLock lock(lock_);
        size_t i = FindId(id);
        if (i == kNone) return false;
        FillSnapshot(icons_[i], out);
        hiconCopy = icons_[i].hiconCopy;
        return true;
    } catch (...) {
        return false;
    }
}

bool TrayInterceptor::TryGetSnapshot(
    uint64_t id, IconSnapshot& out,
    const std::function<void(DWORD hicon, IconSnapshot&)>& fillPixels) const noexcept {
    try {
        SharedLock lock(lock_);
        size_t i = FindId(id);
        if (i == kNone) return false;
        FillSnapshot(icons_[i], out);
        if (fillPixels) fillPixels(icons_[i].hiconCopy, out);
        return true;
    } catch (...) {
        return false;
    }
}

bool TrayInterceptor::HasIconsForPid(DWORD pid) const noexcept {
    SharedLock lock(lock_);
    for (const Icon& icon : icons_)
        if (icon.app.pid == pid) return true;
    return false;
}

std::vector<uint64_t> TrayInterceptor::AllIds() const noexcept {
    std::vector<uint64_t> ids;
    try {
        SharedLock lock(lock_);
        ids.reserve(icons_.size());
        for (const Icon& icon : icons_) ids.push_back(icon.id);
    } catch (...) {
        ids.clear();
    }
    return ids;
}

void TrayInterceptor::Run(const Effects& fx) noexcept {
    try {
        for (const auto& e : fx.items) {
            switch (e.kind) {
                case Effects::Kind::Forward:
                    host_.ForwardToShell((HWND)(ULONG_PTR)e.hwnd, kTrayDataIcon, e.payload);
                    break;
                case Effects::Kind::Post:
                    host_.PostToApp(e.hwnd, e.posted);
                    break;
                case Effects::Kind::Changed:
                    sink_.IconChanged(e.id);
                    break;
                case Effects::Kind::Removed:
                    sink_.IconRemoved(e.id);
                    break;
                case Effects::Kind::Popup:
                    sink_.Popup(e.id, e.infoFlags, e.title, e.text);
                    break;
                case Effects::Kind::DestroyIcon:
                    host_.DestroyIconHandle(e.hwnd);
                    break;
            }
        }
    } catch (...) {
    }
}

}  // namespace ct
