#pragma once
#include <windows.h>
#include <shellapi.h>

#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <vector>

#include "Callbacks.h"
#include "RulesView.h"
#include "TrayPayload.h"
#include "Wire.h"

namespace ct {

struct AppIdentity {
    DWORD pid;
    std::wstring exePath, appKey;
};

struct ITrayHost {
    // Must reach Explorer's original window procedure (e.g. DefSubclassProc), not
    // SendMessage to Shell_TrayWnd: a synthesized NIM_DELETE that came back through
    // the subclass would be swallowed as a captured DELETE.
    virtual LRESULT ForwardToShell(HWND sender, ULONG_PTR dwData, const std::vector<BYTE>& payload) = 0;
    virtual void PostToApp(DWORD hwnd, const PostedMessage&) = 0;
    virtual bool IsWindowAlive(DWORD hwnd) = 0;
    virtual std::optional<AppIdentity> Identify(DWORD hwnd) = 0;
    virtual DWORD CopyIconHandle(DWORD hicon) = 0;  // 0 on failure
    virtual void DestroyIconHandle(DWORD hicon) = 0;

protected:
    ~ITrayHost() = default;
};

struct IEventSink {
    virtual void IconChanged(uint64_t id) = 0;
    virtual void IconRemoved(uint64_t id) = 0;
    virtual void Popup(uint64_t id, DWORD infoFlags, const std::wstring& title,
                       const std::wstring& text) = 0;

protected:
    ~IEventSink() = default;
};

// Decides, per tray WM_COPYDATA, whether Explorer sees it, and keeps the state of
// every icon so captured ones can be shown elsewhere and handed back later. All
// side effects go through the host and sink, which are called with no lock held.
class TrayInterceptor {
public:
    TrayInterceptor(ITrayHost& host, IEventSink& sink, const RulesView& rules, DWORD cleanTrayPid);
    ~TrayInterceptor();
    TrayInterceptor(const TrayInterceptor&) = delete;
    TrayInterceptor& operator=(const TrayInterceptor&) = delete;

    void SetCleanTrayPid(DWORD pid) noexcept;

    // handled=false => caller must DefSubclassProc the original message unchanged
    LRESULT OnCopyData(HWND sender, const COPYDATASTRUCT* cds, bool& handled) noexcept;

    // Call after the referenced RulesView has been refreshed.
    void OnRulesChanged() noexcept;
    void OnClick(uint64_t id, ClickKind kind, int x, int y) noexcept;
    void OnPopupResult(uint64_t id, bool clicked) noexcept;
    void SetAnchor(RECT r) noexcept;
    void ReleaseAll() noexcept;
    void SweepDead() noexcept;

    // The returned handle stays owned by the interceptor; it is valid until the
    // icon's next NIF_ICON change or removal.
    bool TryGetSnapshot(uint64_t id, IconSnapshot& out, DWORD& hiconCopy) const noexcept;

    // Runs fillPixels with the icon's own copy (0 if none) while the table is still
    // locked, so the handle cannot be destroyed mid-read. fillPixels must not call
    // back into the interceptor. False if the id is unknown or fillPixels throws.
    bool TryGetSnapshot(uint64_t id, IconSnapshot& out,
                        const std::function<void(DWORD hicon, IconSnapshot&)>& fillPixels) const noexcept;
    std::vector<uint64_t> AllIds() const noexcept;
    bool HasIconsForPid(DWORD pid) const noexcept;

private:
    struct Icon {
        uint64_t id = 0;
        bool hasGuid = false;
        TrayMessage msg;  // merged state; msg.flags accumulates every flag ever seen
        AppIdentity app;
        bool captured = false;
        bool inShell = false;
        bool hasState = false;
        DWORD hiconCopy = 0;
    };

    struct Effects;

    size_t Find(bool hasGuid, const GUID& guid, DWORD hwnd, UINT uid) const;
    size_t FindId(uint64_t id) const;
    void FillSnapshot(const Icon& icon, IconSnapshot& out) const;
    void Merge(Icon& icon, const TrayMessage& m);
    void HandlePopup(const Icon& icon, const TrayMessage& m, Effects& fx) const;
    void Release(Icon& icon, Effects& fx) const;
    void Hide(Icon& icon, Effects& fx) const;
    void Drop(size_t index, Effects& fx);
    POINT AnchorPoint() const;
    LRESULT OnRectRequest(const COPYDATASTRUCT& cds, bool& handled) const;
    LRESULT OnIconMessage(const COPYDATASTRUCT& cds, bool& handled, Effects& fx);
    void Run(const Effects& fx) noexcept;

    ITrayHost& host_;
    IEventSink& sink_;
    const RulesView& rules_;
    DWORD cleanTrayPid_;
    RECT anchor_{};
    uint64_t nextId_ = 1;
    std::vector<Icon> icons_;
    mutable SRWLOCK lock_ = SRWLOCK_INIT;
};

}  // namespace ct
