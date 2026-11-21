#include "Activation.h"

#include <commctrl.h>

#include <cwchar>
#include <memory>
#include <new>
#include <unordered_set>

#include "PipeWorker.h"
#include "SharedRules.h"
#include "ShellHost.h"
#include "TestHooks.h"

namespace ct {
namespace {

constexpr UINT_PTR kSubclassId = 1;
constexpr DWORD kShutdownWaitMs = 500;

volatile LONG g_active = 0;
volatile LONG g_nextCookie = 0;

LRESULT CALLBACK SubclassProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp, UINT_PTR, DWORD_PTR ref);

// Shared by the tray thread and the worker; freed by whichever lets go last.
struct Runtime final : ICommandPoster {
    Runtime(HWND trayWnd, const wchar_t* pipeName, const wchar_t* rulesName)
        : tray(trayWnd),
          host(trayWnd),
          trayRules(rulesName),
          interceptor(host, events, rulesView, 0),
          worker(interceptor, events, host, *this, pipeName, rulesName, stop) {}

    ~Runtime() {
        // Commands still sitting in the tray's queue are never dereferenced once we are gone.
        for (HookCommand* c : pending) delete c;
        if (thread) CloseHandle(thread);
        if (stop) CloseHandle(stop);
    }

    bool PostToTray(const HookCommand& c) noexcept override;
    std::unique_ptr<HookCommand> Take(WPARAM wp, LPARAM lp) noexcept;

    const HWND tray;
    const UINT commandMsg = RegisterWindowMessageW(L"CleanTray.Hook.Command");
    const WPARAM cookie = (WPARAM)InterlockedIncrement(&g_nextCookie);
    HANDLE stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE thread = nullptr;
    volatile LONG refs = 2;  // the tray thread's and the worker's
    volatile LONG workerFaulted = 0;

    // Tray thread only.
    int depth = 0;
    bool detached = false;
    bool faulted = false;
    bool trayRefDropped = false;

    SRWLOCK commandLock = SRWLOCK_INIT;
    std::unordered_set<HookCommand*> pending;

    ShellHost host;
    EventQueue events;
    RulesView rulesView;
    SharedRules trayRules;
    TrayInterceptor interceptor;
    PipeWorker worker;
};

void Release(Runtime* rt) {
    if (InterlockedDecrement(&rt->refs) == 0) delete rt;
}

bool Runtime::PostToTray(const HookCommand& c) noexcept {
    HookCommand* raw = new (std::nothrow) HookCommand(c);
    if (!raw) return false;
    bool tracked = false;
    AcquireSRWLockExclusive(&commandLock);
    try {
        tracked = pending.insert(raw).second;
    } catch (...) {
    }
    ReleaseSRWLockExclusive(&commandLock);
    if (tracked && PostMessageW(tray, commandMsg, cookie, (LPARAM)raw)) return true;
    if (tracked) {
        AcquireSRWLockExclusive(&commandLock);
        pending.erase(raw);
        ReleaseSRWLockExclusive(&commandLock);
    }
    delete raw;
    return false;
}

std::unique_ptr<HookCommand> Runtime::Take(WPARAM wp, LPARAM lp) noexcept {
    if (wp != cookie) return nullptr;
    auto* c = reinterpret_cast<HookCommand*>(lp);
    // Any process can post this registered message; only pointers we queued are dereferenced.
    AcquireSRWLockExclusive(&commandLock);
    bool ours = pending.erase(c) != 0;
    ReleaseSRWLockExclusive(&commandLock);
    return std::unique_ptr<HookCommand>(ours ? c : nullptr);
}

// No C++ objects live in this frame, as __try requires; the callee holds them.
template <class F>
bool Guarded(F&& f) noexcept {
    __try {
        f();
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void Detach(Runtime* rt) {
    RemoveWindowSubclass(rt->tray, SubclassProc, kSubclassId);
    rt->detached = true;
    SetEvent(rt->stop);
    InterlockedExchange(&g_active, 0);
}

void Fault(Runtime* rt) {
    OutputDebugStringW(L"CleanTrayHook: fault in tray handler, detaching\n");
    // Leaked on purpose: the fault may have left the interceptor's lock held, so nothing,
    // not even its destructor, may touch it again. A later injection starts fresh.
    rt->faulted = true;
    Detach(rt);
    // Captured icons were deleted from Explorer and can no longer be released through the
    // interceptor. With the subclass gone, every app that re-adds lands in the real tray.
    PostMessageW(FaultBroadcastTarget(), RegisterWindowMessageW(L"TaskbarCreated"), 0, 0);
}

void RunCommand(Runtime* rt, const HookCommand& c) noexcept {
    TrayInterceptor& ti = rt->interceptor;
    switch (c.kind) {
        case HookCommand::Kind::RulesChanged:
            rt->trayRules.Refresh(rt->rulesView);
            ti.OnRulesChanged();
            break;
        case HookCommand::Kind::Click:
            ti.OnClick(c.iconId, (ClickKind)c.arg, c.a, c.b);
            break;
        case HookCommand::Kind::PopupResult:
            ti.OnPopupResult(c.iconId, c.arg == 1);
            break;
        case HookCommand::Kind::SetAnchor:
            ti.SetAnchor(RECT{c.a, c.b, c.c, c.d});
            break;
        case HookCommand::Kind::SweepDead:
            ti.SweepDead();
            break;
        case HookCommand::Kind::SetCleanTrayPid:
            ti.SetCleanTrayPid(c.pid);
            break;
        case HookCommand::Kind::ReleaseAllAndIdle:
            ti.ReleaseAll();
            break;
    }
}

LRESULT Dispatch(Runtime* rt, HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) noexcept {
    if (rt->detached) return DefSubclassProc(hwnd, msg, wp, lp);
    if (rt->workerFaulted) {
        Fault(rt);
        return DefSubclassProc(hwnd, msg, wp, lp);
    }

    if (msg == WM_COPYDATA) {
        bool handled = false;
        LRESULT lr = 0;
        auto* cds = reinterpret_cast<const COPYDATASTRUCT*>(lp);
        if (!Guarded([&] {
                MaybeRaiseTestFault(TestFault::TrayHandler);
                lr = rt->interceptor.OnCopyData((HWND)wp, cds, handled);
            })) {
            Fault(rt);
            handled = false;
        }
        return handled ? lr : DefSubclassProc(hwnd, msg, wp, lp);
    }

    if (msg == rt->commandMsg) {
        std::unique_ptr<HookCommand> c = rt->Take(wp, lp);
        if (!c) return DefSubclassProc(hwnd, msg, wp, lp);
        const HookCommand& cmd = *c;
        if (!Guarded([&] { RunCommand(rt, cmd); }))
            Fault(rt);
        else if (cmd.kind == HookCommand::Kind::ReleaseAllAndIdle)
            Detach(rt);
        return 0;
    }

    if (msg == WM_NCDESTROY) {
        Detach(rt);
        // Explorer is exiting; a short wait lets the worker leave before the process does.
        if (rt->thread) WaitForSingleObject(rt->thread, kShutdownWaitMs);
    }
    return DefSubclassProc(hwnd, msg, wp, lp);
}

LRESULT CALLBACK SubclassProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp, UINT_PTR, DWORD_PTR ref) {
    auto* rt = reinterpret_cast<Runtime*>(ref);
    ++rt->depth;
    LRESULT r = Dispatch(rt, hwnd, msg, wp, lp);
    // Dropped only once no frame of ours is left on the stack: a forward can re-enter.
    if (--rt->depth == 0 && rt->detached && !rt->faulted && !rt->trayRefDropped) {
        rt->trayRefDropped = true;
        Release(rt);
    }
    return r;
}

bool RunWorker(Runtime* rt) {
    __try {
        rt->worker.Run();
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

DWORD WINAPI WorkerThread(void* param) {
    auto* rt = static_cast<Runtime*>(param);
    if (RunWorker(rt)) {
        Release(rt);
    } else {
        // Leaked, as in Fault. Pixel reads, the only worker code under the interceptor's
        // lock, catch their own faults, so the lock is free; the tray thread detaches on
        // its next message, and WM_NULL makes sure there is one.
        OutputDebugStringW(L"CleanTrayHook: fault in pipe worker\n");
        rt->worker.Abandon();
        InterlockedExchange(&rt->workerFaulted, 1);
        PostMessageW(rt->tray, WM_NULL, 0, 0);
    }
    return 0;
}

bool Start(HWND tray, const wchar_t* pipeName, const wchar_t* rulesName) noexcept {
    // The hook that loaded us is removed right after Hello; without the pin the DLL
    // would unload under the subclass.
    HMODULE pinned = nullptr;
    LPCWSTR inModule = g_module ? reinterpret_cast<LPCWSTR>(g_module) : reinterpret_cast<LPCWSTR>(&SubclassProc);
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, inModule,
                            &pinned))
        return false;

    Runtime* rt = nullptr;
    try {
        rt = new Runtime(tray, pipeName, rulesName);
    } catch (...) {
        return false;
    }
    if (!rt->stop || !rt->commandMsg) {
        delete rt;
        return false;
    }
    rt->trayRules.Refresh(rt->rulesView);
    if (!SetWindowSubclass(tray, SubclassProc, kSubclassId, (DWORD_PTR)rt)) {
        delete rt;
        return false;
    }
    rt->thread = CreateThread(nullptr, 0, WorkerThread, rt, 0, nullptr);
    if (!rt->thread) {
        RemoveWindowSubclass(tray, SubclassProc, kSubclassId);
        delete rt;
        return false;
    }
    return true;
}

}  // namespace

bool Activate(HWND tray, const wchar_t* pipeName, const wchar_t* rulesName) noexcept {
    if (!tray || !pipeName || !rulesName) return false;
    if (GetWindowThreadProcessId(tray, nullptr) != GetCurrentThreadId()) return false;
    if (InterlockedCompareExchange(&g_active, 1, 0) != 0) return false;
    if (Start(tray, pipeName, rulesName)) return true;
    InterlockedExchange(&g_active, 0);
    return false;
}

void OnHookMessage() noexcept {
    if (g_active) return;
    // CleanTray.exe loads this DLL too, only to find the hook procedure; nothing there
    // owns Shell_TrayWnd, so it never activates.
    HWND tray = FindWindowW(L"Shell_TrayWnd", nullptr);
    if (!tray || GetWindowThreadProcessId(tray, nullptr) != GetCurrentThreadId()) return;
    DWORD session = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &session)) return;
    wchar_t pipe[64];
    swprintf_s(pipe, L"\\\\.\\pipe\\CleanTray.Hook.S%lu", session);
    Activate(tray, pipe, L"Local\\CleanTray.Rules.v1");
}

}  // namespace ct
