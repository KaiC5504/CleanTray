#include "PipeWorker.h"

#include <unordered_map>
#include <utility>

#include "TestHooks.h"
#include "core/Seh.h"
#include "core/Wire.h"

namespace ct {
namespace {

constexpr DWORD kConnectWindowMs = 5000;
constexpr DWORD kConnectRetryMs = 250;
constexpr DWORD kSweepMs = 5000;
// A CleanTray that stops reading for this long is treated as gone, so its icons come back.
constexpr DWORD kWriteTimeoutMs = 30000;
constexpr size_t kMaxFrameBytes = 8u << 20;
constexpr size_t kFlushBytes = 1u << 20;
constexpr size_t kMaxQueuedPopups = 64;
constexpr int kPostAttempts = 50;
constexpr DWORD kPostRetryMs = 100;

enum : uint16_t {
    kRulesChanged = 101,
    kClick = 102,
    kPopupResult = 103,
    kSetAnchor = 104,
    kReleaseAllAndIdle = 105,
};

void Append(std::vector<BYTE>& out, const std::vector<BYTE>& frame) {
    out.insert(out.end(), frame.begin(), frame.end());
}

// Runs under the interceptor's lock: a GDI fault has to stop here, below the lock's
// guard, or /EHsc would skip the guard and leave the tray thread's next lock hanging.
bool SafeExtract(DWORD hicon, IconSnapshot& o) {
    __try {
        MaybeRaiseTestFault(TestFault::Extractor);
        ExtractBgra(hicon, o.w, o.h, o.bgra);
        return true;
    } __except (SehFilter(GetExceptionCode())) {
        o.w = o.h = 0;
        return false;
    }
}

BOOL CALLBACK CollectWindow(HWND hwnd, LPARAM param) {
    try {
        reinterpret_cast<std::vector<HWND>*>(param)->push_back(hwnd);
        return TRUE;
    } catch (...) {
        return FALSE;
    }
}

}  // namespace

void EventQueue::Popup(uint64_t id, DWORD infoFlags, const std::wstring& title, const std::wstring& text) {
    std::vector<BYTE> frame = WritePopup(id, infoFlags, title, text);
    AcquireSRWLockExclusive(&lock_);
    try {
        if (popups_.size() < kMaxQueuedPopups) popups_.push_back(std::move(frame));
    } catch (...) {
    }
    ReleaseSRWLockExclusive(&lock_);
    SetEvent(coalescer_.Event());
}

EventQueue::Drained EventQueue::Drain() {
    Drained d;
    d.icons = coalescer_.Drain();
    AcquireSRWLockExclusive(&lock_);
    d.popups.swap(popups_);
    ReleaseSRWLockExclusive(&lock_);
    return d;
}

PipeWorker::PipeWorker(const TrayInterceptor& interceptor, EventQueue& events, ShellHost& host,
                       ICommandPoster& poster, std::wstring pipeName, std::wstring rulesName,
                       HANDLE stop)
    : interceptor_(interceptor),
      events_(events),
      host_(host),
      poster_(poster),
      pipeName_(std::move(pipeName)),
      stop_(stop),
      sharedRules_(std::move(rulesName)),
      readEvent_(CreateEventW(nullptr, TRUE, FALSE, nullptr)),
      writeEvent_(CreateEventW(nullptr, TRUE, FALSE, nullptr)),
      taskbarCreated_(RegisterWindowMessageW(L"TaskbarCreated")) {}

PipeWorker::~PipeWorker() {
    Close();
    if (readEvent_) CloseHandle(readEvent_);
    if (writeEvent_) CloseHandle(writeEvent_);
}

bool PipeWorker::Stopping() const { return WaitForSingleObject(stop_, 0) == WAIT_OBJECT_0; }

void PipeWorker::Run() noexcept {
    End end = End::Idle;
    try {
        if (readEvent_ && writeEvent_) end = Session();
    } catch (...) {
    }
    Close();
    if (end == End::Idle && !Stopping()) PostReliably(HookCommand{HookCommand::Kind::ReleaseAllAndIdle});
}

PipeWorker::End PipeWorker::Session() {
    if (!Connect()) return End::Idle;
    if (GetNamedPipeServerProcessId(pipe_, &serverPid_) && serverPid_) {
        server_ = OpenProcess(SYNCHRONIZE, FALSE, serverPid_);
        HookCommand c{HookCommand::Kind::SetCleanTrayPid};
        c.pid = serverPid_;
        // Without it CleanTray's own icon could stay captured for the whole session.
        if (!PostReliably(c)) return End::Idle;
    }
    if (!Write(WriteHello(GetCurrentProcessId()))) return End::Idle;

    // Marks from before the connection are covered by the full resync.
    EventQueue::Drained early = events_.Drain();
    if (!Resync(std::move(early.popups))) return End::Idle;
    Kick();
    if (!StartRead()) return End::Idle;

    ULONGLONG nextSweep = GetTickCount64() + kSweepMs;
    for (;;) {
        HANDLE waits[4] = {readEvent_, events_.Event(), stop_, server_};
        DWORD count = server_ ? 4 : 3;
        ULONGLONG now = GetTickCount64();
        DWORD timeout = now >= nextSweep ? 0 : (DWORD)(nextSweep - now);
        DWORD r = WaitForMultipleObjects(count, waits, FALSE, timeout);

        if (r == WAIT_OBJECT_0) {
            bool idle = false, rulesChanged = false;
            if (!FinishRead(idle, rulesChanged) || idle) return End::Idle;
            if (rulesChanged) Kick();
            if (!StartRead()) return End::Idle;
        } else if (r == WAIT_OBJECT_0 + 1) {
            if (!SendEvents()) return End::Idle;
        } else if (r == WAIT_OBJECT_0 + 2) {
            return End::Stopped;
        } else if (r == WAIT_OBJECT_0 + 3) {
            return End::Idle;
        } else if (r == WAIT_TIMEOUT) {
            Post(HookCommand{HookCommand::Kind::SweepDead});
            nextSweep = GetTickCount64() + kSweepMs;
        } else {
            return End::Idle;
        }
    }
}

bool PipeWorker::Connect() {
    ULONGLONG deadline = GetTickCount64() + kConnectWindowMs;
    for (;;) {
        // Identification-level QoS: the pipe's server never gets to act as Explorer.
        pipe_ = CreateFileW(pipeName_.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                            FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION,
                            nullptr);
        if (pipe_ != INVALID_HANDLE_VALUE) return true;
        if (GetTickCount64() >= deadline) return false;
        if (WaitForSingleObject(stop_, kConnectRetryMs) == WAIT_OBJECT_0) return false;
    }
}

bool PipeWorker::Write(const std::vector<BYTE>& bytes) {
    if (bytes.empty()) return true;
    OVERLAPPED ov{};
    ov.hEvent = writeEvent_;
    if (!WriteFile(pipe_, bytes.data(), (DWORD)bytes.size(), nullptr, &ov) &&
        GetLastError() != ERROR_IO_PENDING)
        return false;

    HANDLE waits[3] = {writeEvent_, stop_, server_};
    DWORD r = WaitForMultipleObjects(server_ ? 3 : 2, waits, FALSE, kWriteTimeoutMs);
    DWORD written = 0;
    if (r != WAIT_OBJECT_0) {
        CancelIoEx(pipe_, &ov);
        // ov and the buffer must outlive the cancelled write.
        GetOverlappedResult(pipe_, &ov, &written, TRUE);
        return false;
    }
    return GetOverlappedResult(pipe_, &ov, &written, FALSE) && written == bytes.size();
}

bool PipeWorker::AppendSnapshot(uint64_t id, std::vector<BYTE>& out) {
    IconSnapshot s{};
    // Pixels are read inside the interceptor's lock; the tray thread may destroy the
    // copy the moment the lock is released.
    bool ok = interceptor_.TryGetSnapshot(id, s, [](DWORD hicon, IconSnapshot& o) { SafeExtract(hicon, o); });
    if (ok) Append(out, WriteIconUpsert(s));
    return ok;
}

bool PipeWorker::Resync(std::vector<std::vector<BYTE>> popups) {
    std::vector<BYTE> batch;
    for (uint64_t id : interceptor_.AllIds()) {
        AppendSnapshot(id, batch);
        if (batch.size() >= kFlushBytes) {
            if (!Write(batch)) return false;
            batch.clear();
        }
    }
    for (const auto& p : popups) Append(batch, p);
    return Write(batch);
}

bool PipeWorker::SendEvents() {
    MaybeRaiseTestFault(TestFault::Worker);
    EventQueue::Drained d = events_.Drain();
    std::vector<BYTE> batch;
    for (uint64_t id : d.icons.removed) Append(batch, WriteIconRemoved(id));
    for (uint64_t id : d.icons.changed) {
        // A miss means the icon went away meanwhile; its IconRemoved is already queued.
        AppendSnapshot(id, batch);
        if (batch.size() >= kFlushBytes) {
            if (!Write(batch)) return false;
            batch.clear();
        }
    }
    for (const auto& p : d.popups) Append(batch, p);
    return Write(batch);
}

bool PipeWorker::StartRead() {
    readOv_ = OVERLAPPED{};
    readOv_.hEvent = readEvent_;
    if (ReadFile(pipe_, chunk_, sizeof chunk_, nullptr, &readOv_) || GetLastError() == ERROR_IO_PENDING) {
        readPending_ = true;
        return true;
    }
    return false;
}

bool PipeWorker::FinishRead(bool& idleRequested, bool& rulesChanged) {
    DWORD n = 0;
    readPending_ = false;
    if (!GetOverlappedResult(pipe_, &readOv_, &n, FALSE) && GetLastError() != ERROR_MORE_DATA)
        return false;
    inbox_.insert(inbox_.end(), chunk_, chunk_ + n);
    if (inbox_.size() > kMaxFrameBytes) inbox_.clear();

    size_t offset = 0;
    while (offset < inbox_.size() && !idleRequested) {
        size_t consumed = 0;
        auto c = TryParseCommand(inbox_.data() + offset, inbox_.size() - offset, consumed);
        if (!c) {
            // A malformed frame leaves no way to find the next boundary.
            if (consumed) offset = inbox_.size();
            break;
        }
        offset += consumed;
        Dispatch(*c, idleRequested, rulesChanged);
    }
    inbox_.erase(inbox_.begin(), inbox_.begin() + (ptrdiff_t)offset);
    return true;
}

void PipeWorker::Dispatch(const Command& c, bool& idleRequested, bool& rulesChanged) {
    HookCommand h{};
    switch (c.type) {
        case kRulesChanged:
            h.kind = HookCommand::Kind::RulesChanged;
            rulesChanged = true;
            break;
        case kClick:
            if (c.kind < (uint8_t)ClickKind::Left || c.kind > (uint8_t)ClickKind::Right) return;
            h.kind = HookCommand::Kind::Click;
            h.iconId = c.iconId;
            h.arg = c.kind;
            h.a = c.a;
            h.b = c.b;
            break;
        case kPopupResult:
            if (c.kind != 1 && c.kind != 2) return;
            h.kind = HookCommand::Kind::PopupResult;
            h.iconId = c.iconId;
            h.arg = c.kind;
            break;
        case kSetAnchor:
            h.kind = HookCommand::Kind::SetAnchor;
            h.a = c.a;
            h.b = c.b;
            h.c = c.c;
            h.d = c.d;
            break;
        case kReleaseAllAndIdle:
            // Posted by Run once the pipe is closed, so CleanTray sees the disconnect at once.
            idleRequested = true;
            return;
        default:
            return;
    }
    Post(h);
}

// Apps whose icons were added before we were injected are invisible to the
// interceptor; TaskbarCreated makes them add their icons again, through us.
void PipeWorker::Kick() {
    sharedRules_.Refresh(rules_);
    std::vector<HWND> windows;
    EnumWindows(CollectWindow, (LPARAM)&windows);

    const DWORD self = GetCurrentProcessId();
    std::unordered_map<DWORD, bool> decided;
    for (HWND hwnd : windows) {
        if (Stopping()) return;
        DWORD pid = 0;
        GetWindowThreadProcessId(hwnd, &pid);
        if (!pid || pid == self || pid == serverPid_) continue;
        auto [it, fresh] = decided.try_emplace(pid, false);
        if (fresh && !interceptor_.HasIconsForPid(pid)) {
            auto app = host_.IdentifyPid(pid);
            it->second = app && rules_.AppCaptured(app->appKey);
        }
        // Like Explorer's own broadcast: the icon may belong to any top-level window of the app.
        if (it->second) PostMessageW(hwnd, taskbarCreated_, 0, 0);
    }
}

void PipeWorker::Post(const HookCommand& c) { poster_.PostToTray(c); }

// For commands whose loss would leave icons in the wrong place: rides out a full queue.
bool PipeWorker::PostReliably(const HookCommand& c) {
    for (int attempt = 0; attempt < kPostAttempts; ++attempt) {
        if (poster_.PostToTray(c)) return true;
        if (WaitForSingleObject(stop_, kPostRetryMs) == WAIT_OBJECT_0) return false;
    }
    return false;
}

void PipeWorker::Abandon() noexcept {
    // No waiting for cancellation: this object is never freed after a fault, so the
    // OVERLAPPED and buffer stay valid for the cancelled read.
    if (pipe_ != INVALID_HANDLE_VALUE) {
        CancelIoEx(pipe_, nullptr);
        CloseHandle(pipe_);
        pipe_ = INVALID_HANDLE_VALUE;
    }
    if (server_) {
        CloseHandle(server_);
        server_ = nullptr;
    }
}

void PipeWorker::Close() {
    if (pipe_ != INVALID_HANDLE_VALUE) {
        if (readPending_) {
            DWORD n = 0;
            CancelIoEx(pipe_, &readOv_);
            GetOverlappedResult(pipe_, &readOv_, &n, TRUE);
            readPending_ = false;
        }
        CloseHandle(pipe_);
        pipe_ = INVALID_HANDLE_VALUE;
    }
    if (server_) {
        CloseHandle(server_);
        server_ = nullptr;
    }
}

}  // namespace ct
