// Drives the real CleanTrayHook.dll in this process: a window on its own thread stands in
// for Shell_TrayWnd (its wndproc plays Explorer's original procedure), this process plays
// CleanTray (pipe server and rules publisher), and a child copy of this exe owns the icon
// window, so the icon's pid differs from the pipe server's as it does for real apps.
#include <windows.h>
#include <shellapi.h>

#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <deque>
#include <functional>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

#include "core/AppKey.h"
#include "core/Callbacks.h"
#include "core/TrayPayload.h"

namespace {

constexpr DWORD kWaitMs = 5000;
constexpr UINT kCb = WM_APP + 5;
constexpr UINT kActivateMsg = WM_APP + 1;
constexpr UINT kSyncMsg = WM_APP + 2;
constexpr UINT kIconUid = 7;
constexpr UINT kOwnUid = 9;

int g_failures = 0;

#define EXPECT(cond)                                                                  \
    do {                                                                              \
        if (!(cond)) {                                                                \
            ++g_failures;                                                             \
            std::fprintf(stderr, "%s:%d: FAILED %s\n", __FILE__, __LINE__, #cond);    \
        }                                                                             \
    } while (0)

#define REQUIRE(cond)                                                                       \
    do {                                                                                    \
        if (!(cond)) {                                                                      \
            ++g_failures;                                                                   \
            std::fprintf(stderr, "%s:%d: FAILED (stopping) %s\n", __FILE__, __LINE__, #cond); \
            return;                                                                         \
        }                                                                                   \
    } while (0)

void Step(const char* name) { std::printf("-- %s\n", name); }

std::wstring ProcessPath(HANDLE process) {
    std::wstring buf(32768, L'\0');
    DWORD n = (DWORD)buf.size();
    if (!QueryFullProcessImageNameW(process, 0, buf.data(), &n)) return {};
    buf.resize(n);
    return buf;
}

LRESULT CALLBACK IconProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    static const UINT taskbarCreated = RegisterWindowMessageW(L"TaskbarCreated");
    if (m == taskbarCreated) {
        std::printf("kick\n");
        std::fflush(stdout);
        return 0;
    }
    if (m == kCb) {
        std::printf("cb %llx %llx\n", (unsigned long long)w, (unsigned long long)l);
        std::fflush(stdout);
        return 0;
    }
    return DefWindowProcW(h, m, w, l);
}

int IconHostMain() {
    WNDCLASSW wc{};
    wc.lpfnWndProc = IconProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"CleanTrayRuntimeTestIcon";
    RegisterClassW(&wc);
    HWND h = CreateWindowExW(0, wc.lpszClassName, L"", WS_OVERLAPPED, 0, 0, 0, 0, nullptr, nullptr,
                             wc.hInstance, nullptr);
    if (!h) return 1;
    std::printf("hwnd %llx\n", (unsigned long long)(ULONG_PTR)h);
    std::fflush(stdout);
    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    return 0;
}

class Child {
public:
    ~Child() {
        // Kill-on-close takes the child down with the job.
        if (job_) CloseHandle(job_);
        if (reader_) {
            WaitForSingleObject(reader_, kWaitMs);
            CloseHandle(reader_);
        }
        if (read_) CloseHandle(read_);
        if (process_) CloseHandle(process_);
    }

    bool Start(const std::wstring& self) {
        SECURITY_ATTRIBUTES sa{sizeof sa, nullptr, TRUE};
        HANDLE write = nullptr;
        if (!CreatePipe(&read_, &write, &sa, 0)) return false;
        SetHandleInformation(read_, HANDLE_FLAG_INHERIT, 0);

        job_ = CreateJobObjectW(nullptr, nullptr);
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        SetInformationJobObject(job_, JobObjectExtendedLimitInformation, &limits, sizeof limits);

        commandLine_ = L"\"" + self + L"\" --icon-host";
        std::wstring cmd = commandLine_;
        STARTUPINFOW si{sizeof si};
        si.dwFlags = STARTF_USESTDHANDLES;
        si.hStdOutput = write;
        si.hStdError = write;
        PROCESS_INFORMATION pi{};
        BOOL ok = CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, TRUE,
                                 CREATE_SUSPENDED | CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi);
        CloseHandle(write);
        if (!ok) return false;
        AssignProcessToJobObject(job_, pi.hProcess);
        ResumeThread(pi.hThread);
        CloseHandle(pi.hThread);
        process_ = pi.hProcess;
        pid = pi.dwProcessId;
        reader_ = CreateThread(nullptr, 0, &Child::ReaderThread, this, 0, nullptr);

        auto line = WaitLine("hwnd ", kWaitMs);
        if (!line) return false;
        hwnd = (HWND)(ULONG_PTR)std::strtoull(line->c_str() + 5, nullptr, 16);
        return IsWindow(hwnd) != FALSE;
    }

    // Discards lines that do not start with prefix.
    std::optional<std::string> WaitLine(const std::string& prefix, DWORD timeoutMs) {
        std::unique_lock<std::mutex> lock(m_);
        auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeoutMs);
        for (;;) {
            while (!lines_.empty()) {
                std::string l = std::move(lines_.front());
                lines_.pop_front();
                if (l.compare(0, prefix.size(), prefix) == 0) return l;
            }
            if (eof_ || cv_.wait_until(lock, deadline) == std::cv_status::timeout) {
                if (lines_.empty()) return std::nullopt;
            }
        }
    }

    HANDLE Process() const { return process_; }
    const std::wstring& CommandLine() const { return commandLine_; }

    DWORD pid = 0;
    HWND hwnd = nullptr;

private:
    static DWORD WINAPI ReaderThread(void* p) {
        auto* self = static_cast<Child*>(p);
        std::string partial;
        char buf[512];
        DWORD n = 0;
        while (ReadFile(self->read_, buf, sizeof buf, &n, nullptr) && n) {
            partial.append(buf, n);
            size_t nl;
            while ((nl = partial.find('\n')) != std::string::npos) {
                std::string line = partial.substr(0, nl);
                if (!line.empty() && line.back() == '\r') line.pop_back();
                partial.erase(0, nl + 1);
                std::lock_guard<std::mutex> lock(self->m_);
                self->lines_.push_back(std::move(line));
                self->cv_.notify_all();
            }
        }
        std::lock_guard<std::mutex> lock(self->m_);
        self->eof_ = true;
        self->cv_.notify_all();
        return 0;
    }

    HANDLE process_ = nullptr, job_ = nullptr, read_ = nullptr, reader_ = nullptr;
    std::wstring commandLine_;
    std::mutex m_;
    std::condition_variable cv_;
    std::deque<std::string> lines_;
    bool eof_ = false;
};

struct Frame {
    uint16_t type;
    std::vector<BYTE> payload;
};

class Server {
public:
    ~Server() { Close(); if (event_) CloseHandle(event_); }

    bool Create(const std::wstring& name) {
        event_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        // The previous instance may linger for a moment after the client let go of it.
        for (int attempt = 0; attempt < 40; ++attempt) {
            pipe_ = CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                                     PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                                     1, 1 << 16, 1 << 16, 0, nullptr);
            if (pipe_ != INVALID_HANDLE_VALUE) return true;
            Sleep(50);
        }
        return false;
    }

    bool Accept(DWORD timeoutMs) {
        OVERLAPPED ov{};
        ov.hEvent = event_;
        if (ConnectNamedPipe(pipe_, &ov)) return true;
        DWORD e = GetLastError();
        if (e == ERROR_PIPE_CONNECTED) return true;
        if (e != ERROR_IO_PENDING) return false;
        DWORD n;
        return Finish(ov, timeoutMs, n);
    }

    std::optional<Frame> Read(DWORD timeoutMs) {
        ULONGLONG deadline = GetTickCount64() + timeoutMs;
        for (;;) {
            if (buf_.size() >= 6) {
                uint32_t len;
                std::memcpy(&len, buf_.data(), 4);
                if (buf_.size() >= 6 + (size_t)len) {
                    Frame f;
                    std::memcpy(&f.type, buf_.data() + 4, 2);
                    f.payload.assign(buf_.begin() + 6, buf_.begin() + 6 + len);
                    buf_.erase(buf_.begin(), buf_.begin() + 6 + len);
                    return f;
                }
            }
            ULONGLONG now = GetTickCount64();
            if (now >= deadline || broken) return std::nullopt;
            chunk_.resize(1 << 16);
            OVERLAPPED ov{};
            ov.hEvent = event_;
            if (!ReadFile(pipe_, chunk_.data(), (DWORD)chunk_.size(), nullptr, &ov) &&
                GetLastError() != ERROR_IO_PENDING) {
                broken = true;
                return std::nullopt;
            }
            DWORD n = 0;
            if (!Finish(ov, (DWORD)(deadline - now), n)) {
                if (GetLastError() != ERROR_OPERATION_ABORTED) broken = true;
                return std::nullopt;
            }
            buf_.insert(buf_.end(), chunk_.begin(), chunk_.begin() + n);
        }
    }

    std::optional<Frame> ReadType(uint16_t type, DWORD timeoutMs,
                                  const std::function<bool(const Frame&)>& accept = nullptr) {
        ULONGLONG deadline = GetTickCount64() + timeoutMs;
        for (;;) {
            ULONGLONG now = GetTickCount64();
            if (now >= deadline) return std::nullopt;
            auto f = Read((DWORD)(deadline - now));
            if (!f) return std::nullopt;
            if (f->type == type && (!accept || accept(*f))) return f;
        }
    }

    bool WaitBroken(DWORD timeoutMs) {
        ULONGLONG deadline = GetTickCount64() + timeoutMs;
        while (!broken) {
            ULONGLONG now = GetTickCount64();
            if (now >= deadline) return false;
            Read((DWORD)(deadline - now));
        }
        return true;
    }

    bool Write(const std::vector<BYTE>& bytes) {
        OVERLAPPED ov{};
        ov.hEvent = event_;
        if (!WriteFile(pipe_, bytes.data(), (DWORD)bytes.size(), nullptr, &ov) &&
            GetLastError() != ERROR_IO_PENDING)
            return false;
        DWORD n = 0;
        return Finish(ov, kWaitMs, n) && n == bytes.size();
    }

    void Close() {
        if (pipe_ != INVALID_HANDLE_VALUE) CloseHandle(pipe_);
        pipe_ = INVALID_HANDLE_VALUE;
    }

    bool broken = false;

private:
    bool Finish(OVERLAPPED& ov, DWORD timeoutMs, DWORD& n) {
        if (WaitForSingleObject(event_, timeoutMs) != WAIT_OBJECT_0) CancelIoEx(pipe_, &ov);
        return GetOverlappedResult(pipe_, &ov, &n, TRUE) != FALSE;
    }

    HANDLE pipe_ = INVALID_HANDLE_VALUE;
    HANDLE event_ = nullptr;
    std::vector<BYTE> buf_, chunk_;
};

class Reader {
public:
    explicit Reader(const std::vector<BYTE>& b) : b_(b) {}
    template <class T>
    T Get() {
        T v{};
        if (pos_ + sizeof v > b_.size()) {
            ok = false;
            return v;
        }
        std::memcpy(&v, b_.data() + pos_, sizeof v);
        pos_ += sizeof v;
        return v;
    }
    std::wstring Str() {
        uint16_t n = Get<uint16_t>();
        if (!ok || pos_ + n * 2u > b_.size()) {
            ok = false;
            return {};
        }
        std::wstring s(n, L'\0');
        std::memcpy(s.data(), b_.data() + pos_, n * 2u);
        pos_ += n * 2u;
        return s;
    }
    size_t Remaining() const { return b_.size() - pos_; }
    const BYTE* Here() const { return b_.data() + pos_; }
    bool ok = true;

private:
    const std::vector<BYTE>& b_;
    size_t pos_ = 0;
};

struct Upsert {
    uint64_t id;
    uint32_t pid, hwnd, uid, version;
    uint8_t captured;
    std::wstring appKey, tip;
    uint16_t w, h;
    bool pixelsMatch, anyAlpha;
};

std::optional<Upsert> ParseUpsert(const Frame& f) {
    Reader r(f.payload);
    Upsert u{};
    u.id = r.Get<uint64_t>();
    u.pid = r.Get<uint32_t>();
    u.hwnd = r.Get<uint32_t>();
    u.uid = r.Get<uint32_t>();
    r.Get<uint8_t>();
    r.Get<GUID>();
    r.Get<uint32_t>();
    u.version = r.Get<uint32_t>();
    r.Get<uint32_t>();
    u.captured = r.Get<uint8_t>();
    u.appKey = r.Str();
    r.Str();
    u.tip = r.Str();
    u.w = r.Get<uint16_t>();
    u.h = r.Get<uint16_t>();
    if (!r.ok) return std::nullopt;
    size_t bytes = (size_t)u.w * u.h * 4;
    u.pixelsMatch = r.Remaining() == bytes;
    u.anyAlpha = false;
    for (size_t i = 3; u.pixelsMatch && i < bytes; i += 4) u.anyAlpha |= r.Here()[i] != 0;
    return u;
}

std::vector<BYTE> ClickFrame(uint64_t id, ct::ClickKind kind, int32_t x, int32_t y) {
    std::vector<BYTE> f(6 + 17);
    uint32_t len = 17;
    uint16_t type = 102;
    std::memcpy(&f[0], &len, 4);
    std::memcpy(&f[4], &type, 2);
    std::memcpy(&f[6], &id, 8);
    f[14] = (BYTE)kind;
    std::memcpy(&f[15], &x, 4);
    std::memcpy(&f[19], &y, 4);
    return f;
}

struct CopyRecord {
    WPARAM wParam;
    ULONG_PTR dwData;
    std::optional<ct::TrayMessage> msg;
};

using ActivateFn = BOOL(WINAPI*)(HWND, const wchar_t*, const wchar_t*);
using InjectFaultFn = void(WINAPI*)(LONG);
using SetFaultBroadcastFn = void(WINAPI*)(HWND);
using NormalizeFn = int(WINAPI*)(const wchar_t*, const wchar_t*, wchar_t*, int);

std::mutex g_recordsLock;
std::vector<CopyRecord> g_records;
HANDLE g_syncEvent;
ActivateFn g_activate;
InjectFaultFn g_injectFault;
SetFaultBroadcastFn g_setFaultBroadcast;
std::wstring g_pipeName, g_rulesName;

LRESULT CALLBACK TrayProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    switch (m) {
        case WM_COPYDATA: {
            auto* cds = reinterpret_cast<const COPYDATASTRUCT*>(l);
            CopyRecord r{w, cds->dwData, ct::ParseTrayCopyData(cds->dwData, cds->lpData, cds->cbData)};
            std::lock_guard<std::mutex> lock(g_recordsLock);
            g_records.push_back(std::move(r));
            return 1;
        }
        case kActivateMsg:
            return g_activate(h, g_pipeName.c_str(), g_rulesName.c_str());
        case kSyncMsg:
            SetEvent(g_syncEvent);
            return 0;
        case WM_DESTROY:
            PostQuitMessage(0);
            return 0;
    }
    return DefWindowProcW(h, m, w, l);
}

struct TrayStart {
    HWND hwnd;
    HANDLE ready;
};

DWORD WINAPI TrayThread(void* p) {
    auto* start = static_cast<TrayStart*>(p);
    WNDCLASSW wc{};
    wc.lpfnWndProc = TrayProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"CleanTrayRuntimeTestTray";
    RegisterClassW(&wc);
    start->hwnd = CreateWindowExW(0, wc.lpszClassName, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr,
                                  wc.hInstance, nullptr);
    SetEvent(start->ready);
    if (!start->hwnd) return 1;
    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) DispatchMessageW(&msg);
    return 0;
}

std::vector<CopyRecord> Records() {
    std::lock_guard<std::mutex> lock(g_recordsLock);
    return g_records;
}

void ClearRecords() {
    std::lock_guard<std::mutex> lock(g_recordsLock);
    g_records.clear();
}

bool WaitRecords(const std::function<bool(const std::vector<CopyRecord>&)>& done, DWORD timeoutMs) {
    ULONGLONG deadline = GetTickCount64() + timeoutMs;
    for (;;) {
        if (done(Records())) return true;
        if (GetTickCount64() >= deadline) return false;
        Sleep(20);
    }
}

LRESULT SendTray(HWND tray, DWORD nim, const ct::TrayMessage& m, HWND sender) {
    std::vector<BYTE> bytes = ct::BuildTrayCopyData(nim, m);
    COPYDATASTRUCT cds{ct::kTrayDataIcon, (DWORD)bytes.size(), bytes.data()};
    DWORD_PTR result = 0;
    if (!SendMessageTimeoutW(tray, WM_COPYDATA, (WPARAM)sender, (LPARAM)&cds, SMTO_NORMAL, kWaitMs, &result))
        return -1;
    return (LRESULT)result;
}

// Posted messages are FIFO, so once this returns every command the worker posted
// before it has run on the tray thread.
bool SyncTray(HWND tray) {
    ResetEvent(g_syncEvent);
    return PostMessageW(tray, kSyncMsg, 0, 0) && WaitForSingleObject(g_syncEvent, kWaitMs) == WAIT_OBJECT_0;
}

LRESULT Activate(HWND tray) {
    DWORD_PTR result = 0;
    if (!SendMessageTimeoutW(tray, kActivateMsg, 0, 0, SMTO_NORMAL, kWaitMs, &result)) return -1;
    return (LRESULT)result;
}

HANDLE WriteRules(const std::wstring& name, const std::wstring& capturedKey) {
    constexpr DWORD kSize = 4u << 20;
    HANDLE map = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, kSize, name.c_str());
    if (!map) return nullptr;
    auto* view = static_cast<BYTE*>(MapViewOfFile(map, FILE_MAP_WRITE, 0, 0, 0));
    if (!view) return nullptr;
    uint16_t keyLen = (uint16_t)capturedKey.size();
    uint32_t header[6] = {0x4C525443, 1, 2, 0, 1, 4u + keyLen * 2u};
    std::memcpy(view, header, sizeof header);
    BYTE* p = view + sizeof header;
    std::memcpy(p, &keyLen, 2);
    p[2] = 1;  // Captured
    p[3] = 1;  // popups allowed
    std::memcpy(p + 4, capturedKey.data(), keyLen * 2u);
    UnmapViewOfFile(view);
    return map;
}

void ExpectHello(Server& server) {
    auto hello = server.ReadType(1, kWaitMs);
    EXPECT(hello.has_value());
    if (!hello) return;
    Reader r(hello->payload);
    EXPECT(r.Get<uint32_t>() == GetCurrentProcessId());
    EXPECT(r.Get<uint32_t>() == 1u);
    EXPECT(r.ok && r.Remaining() == 0);
}

void CheckNormalizeExport(NormalizeFn normalize, const std::wstring& path, const std::wstring& commandLine) {
    Step("CleanTrayNormalizeAppKey gives the core's key under the export's buffer contract");
    const std::wstring expected = ct::NormalizeAppKey(path, commandLine);
    const int need = (int)expected.size() + 1;
    EXPECT(normalize(path.c_str(), commandLine.c_str(), nullptr, 0) == need);
    std::wstring buf((size_t)need, L'x');
    EXPECT(normalize(path.c_str(), commandLine.c_str(), buf.data(), need) == need - 1);
    EXPECT(std::wstring(buf.c_str()) == expected);
    EXPECT(normalize(path.c_str(), commandLine.c_str(), buf.data(), need - 1) == need);
    EXPECT(buf[0] == L'\0');

    const wchar_t* host = L"C:\\Java\\bin\\javaw.exe";
    const wchar_t* script = L"javaw -jar D:\\T\\app.jar";
    wchar_t out[128] = {};
    EXPECT(normalize(host, script, out, 128) == (int)ct::NormalizeAppKey(host, script).size());
    EXPECT(std::wstring(out) == ct::NormalizeAppKey(host, script));
    EXPECT(normalize(host, nullptr, out, 128) == (int)ct::NormalizeAppKey(host, L"").size());

    EXPECT(normalize(nullptr, L"", out, 128) == -1);
    // A bad buffer faults inside the DLL; the export must contain it.
    EXPECT(normalize(path.c_str(), L"", reinterpret_cast<wchar_t*>(8), 128) == -1);
}

void RunAll(HWND tray, Child& child) {
    const DWORD iconHwnd = (DWORD)(ULONG_PTR)child.hwnd;
    const DWORD ownHwnd = (DWORD)(ULONG_PTR)tray;
    g_setFaultBroadcast(child.hwnd);
    const std::wstring childPath = ProcessPath(child.Process());
    const std::wstring expectedKey = ct::NormalizeAppKey(childPath, child.CommandLine());
    REQUIRE(!childPath.empty());

    HANDLE rules = WriteRules(g_rulesName, expectedKey);
    REQUIRE(rules != nullptr);

    Server server;
    REQUIRE(server.Create(g_pipeName));

    Step("activation connects and says Hello within 5 s");
    ULONGLONG t0 = GetTickCount64();
    REQUIRE(Activate(tray) == TRUE);
    EXPECT(Activate(tray) == FALSE);  // already active
    REQUIRE(server.Accept(kWaitMs));
    ExpectHello(server);
    EXPECT(GetTickCount64() - t0 < kWaitMs);

    Step("captured app with no known icon gets kicked");
    EXPECT(child.WaitLine("kick", kWaitMs).has_value());
    REQUIRE(SyncTray(tray));

    Step("CleanTray's own icon passes through even though its key is captured");
    ClearRecords();
    ct::TrayMessage own;
    own.hwnd = ownHwnd;
    own.uid = kOwnUid;
    own.flags = NIF_MESSAGE | NIF_TIP;
    own.callbackMsg = kCb;
    own.tip = L"own";
    EXPECT(SendTray(tray, NIM_ADD, own, tray) == 1);
    {
        auto recs = Records();
        EXPECT(recs.size() == 1);
        if (recs.size() == 1 && recs[0].msg) {
            EXPECT(recs[0].msg->nim == (DWORD)NIM_ADD);
            EXPECT(recs[0].msg->uid == kOwnUid);
            EXPECT(recs[0].msg->tip == L"own");
        }
    }
    ct::TrayMessage ownId;
    ownId.hwnd = ownHwnd;
    ownId.uid = kOwnUid;
    EXPECT(SendTray(tray, NIM_DELETE, ownId, tray) == 1);
    EXPECT(Records().size() == 2);

    Step("captured NIM_ADD is swallowed; Explorer sees only the safety NIM_DELETE");
    ClearRecords();
    HICON icon = CopyIcon(LoadIconW(nullptr, IDI_APPLICATION));
    REQUIRE(icon != nullptr);
    ct::TrayMessage add;
    add.hwnd = iconHwnd;
    add.uid = kIconUid;
    add.flags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
    add.callbackMsg = kCb;
    add.hicon = (DWORD)(ULONG_PTR)icon;
    add.tip = L"rt-test";
    add.exePath = childPath;
    EXPECT(SendTray(tray, NIM_ADD, add, child.hwnd) == 1);
    {
        auto recs = Records();
        EXPECT(recs.size() == 1);
        if (recs.size() == 1) {
            EXPECT(recs[0].dwData == ct::kTrayDataIcon);
            EXPECT((DWORD)recs[0].wParam == iconHwnd);
            EXPECT(recs[0].msg && recs[0].msg->nim == (DWORD)NIM_DELETE);
            EXPECT(recs[0].msg && recs[0].msg->hwnd == iconHwnd && recs[0].msg->uid == kIconUid);
        }
    }
    ct::TrayMessage ver;
    ver.hwnd = iconHwnd;
    ver.uid = kIconUid;
    ver.version = 4;
    EXPECT(SendTray(tray, NIM_SETVERSION, ver, child.hwnd) == 1);
    EXPECT(Records().size() == 1);

    Step("IconUpsert reports it captured, with pixels");
    auto upFrame = server.ReadType(2, kWaitMs, [&](const Frame& f) {
        auto u = ParseUpsert(f);
        return u && u->uid == kIconUid && u->version == 4;
    });
    REQUIRE(upFrame.has_value());
    auto up = ParseUpsert(*upFrame);
    REQUIRE(up.has_value());
    EXPECT(up->captured == 1);
    EXPECT(up->pid == child.pid);
    EXPECT(up->hwnd == iconHwnd);
    EXPECT(up->appKey == expectedKey);
    EXPECT(up->tip == L"rt-test");
    EXPECT(up->w > 0 && up->h > 0);
    EXPECT(up->pixelsMatch);
    EXPECT(up->anyAlpha);
    const uint64_t id = up->id;

    Step("balloon on a captured icon becomes a Popup event");
    ct::TrayMessage popup;
    popup.hwnd = iconHwnd;
    popup.uid = kIconUid;
    popup.flags = NIF_INFO;
    popup.info = L"hello";
    popup.infoTitle = L"T";
    popup.infoFlags = NIIF_INFO;
    EXPECT(SendTray(tray, NIM_MODIFY, popup, child.hwnd) == 1);
    auto pop = server.ReadType(4, kWaitMs);
    EXPECT(pop.has_value());
    if (pop) {
        Reader r(pop->payload);
        EXPECT(r.Get<uint64_t>() == id);
        EXPECT(r.Get<uint32_t>() == (uint32_t)NIIF_INFO);
        EXPECT(r.Str() == L"T");
        EXPECT(r.Str() == L"hello");
    }
    EXPECT(Records().size() == 1);

    Step("Click command posts the v4 right-click sequence to the icon window");
    REQUIRE(server.Write(ClickFrame(id, ct::ClickKind::Right, 100, 200)));
    for (const auto& want : ct::BuildClickMessages(4, kCb, ct::ClickKind::Right, kIconUid, 100, 200)) {
        char expected[64];
        std::snprintf(expected, sizeof expected, "cb %llx %llx", (unsigned long long)want.wParam,
                      (unsigned long long)want.lParam);
        auto line = child.WaitLine("cb ", kWaitMs);
        EXPECT(line.has_value());
        if (!line) break;
        EXPECT(*line == expected);
    }

    Step("closing the pipe releases the captured icon back to Explorer");
    ClearRecords();
    server.Close();
    bool released = WaitRecords(
        [&](const std::vector<CopyRecord>& recs) {
            bool sawAdd = false;
            for (const auto& r : recs) {
                if (!r.msg || r.msg->uid != kIconUid) continue;
                if (r.msg->nim == NIM_ADD) sawAdd = r.msg->hicon != 0 && r.msg->tip == L"rt-test";
                if (r.msg->nim == NIM_SETVERSION && sawAdd) return r.msg->version == 4;
            }
            return false;
        },
        kWaitMs);
    EXPECT(released);

    Step("a later injection reactivates with a fresh interceptor");
    Server again;
    REQUIRE(again.Create(g_pipeName));
    REQUIRE(Activate(tray) == TRUE);
    REQUIRE(again.Accept(kWaitMs));
    ExpectHello(again);
    EXPECT(child.WaitLine("kick", kWaitMs).has_value());

    auto upsertWhere = [](const std::function<bool(const Upsert&)>& pred) {
        return [pred](const Frame& f) {
            auto u = ParseUpsert(f);
            return u && pred(*u);
        };
    };

    Step("a fault while reading pixels is contained under the interceptor's lock");
    EXPECT(SendTray(tray, NIM_ADD, add, child.hwnd) == 1);
    EXPECT(again.ReadType(2, kWaitMs, upsertWhere([](const Upsert& u) { return u.uid == kIconUid && u.w > 0; })));
    ct::TrayMessage tip;
    tip.hwnd = iconHwnd;
    tip.uid = kIconUid;
    tip.flags = NIF_TIP;
    tip.tip = L"faulted";
    g_injectFault(2);
    EXPECT(SendTray(tray, NIM_MODIFY, tip, child.hwnd) == 1);
    auto bare = again.ReadType(2, kWaitMs, upsertWhere([](const Upsert& u) { return u.tip == L"faulted"; }));
    EXPECT(bare && ParseUpsert(*bare)->w == 0 && ParseUpsert(*bare)->h == 0);
    tip.tip = L"after";
    EXPECT(SendTray(tray, NIM_MODIFY, tip, child.hwnd) == 1);
    auto full = again.ReadType(2, kWaitMs, upsertWhere([](const Upsert& u) { return u.tip == L"after"; }));
    EXPECT(full && ParseUpsert(*full)->w > 0 && ParseUpsert(*full)->pixelsMatch);

    Step("a worker fault drops the pipe, detaches and re-kicks apps");
    g_injectFault(3);
    tip.tip = L"boom";
    EXPECT(SendTray(tray, NIM_MODIFY, tip, child.hwnd) == 1);
    EXPECT(again.WaitBroken(2000));
    EXPECT(child.WaitLine("kick", kWaitMs).has_value());
    ClearRecords();
    EXPECT(SendTray(tray, NIM_MODIFY, tip, child.hwnd) == 1);
    {
        auto recs = Records();
        EXPECT(recs.size() == 1);
        EXPECT(recs.size() == 1 && recs[0].msg && recs[0].msg->nim == (DWORD)NIM_MODIFY);
    }

    Step("a tray handler fault passes the message on, detaches and re-kicks apps");
    again.Close();
    Server third;
    REQUIRE(third.Create(g_pipeName));
    REQUIRE(Activate(tray) == TRUE);
    REQUIRE(third.Accept(kWaitMs));
    ExpectHello(third);
    EXPECT(child.WaitLine("kick", kWaitMs).has_value());
    ClearRecords();
    g_injectFault(1);
    EXPECT(SendTray(tray, NIM_ADD, add, child.hwnd) == 1);
    {
        auto recs = Records();
        EXPECT(recs.size() == 1);
        EXPECT(recs.size() == 1 && recs[0].msg && recs[0].msg->nim == (DWORD)NIM_ADD &&
               recs[0].msg->tip == L"rt-test");
    }
    EXPECT(child.WaitLine("kick", kWaitMs).has_value());
    EXPECT(third.WaitBroken(2000));

    Step("tray window destruction stops the worker and drops the pipe");
    third.Close();
    Server last;
    REQUIRE(last.Create(g_pipeName));
    REQUIRE(Activate(tray) == TRUE);
    REQUIRE(last.Accept(kWaitMs));
    ExpectHello(last);
    ClearRecords();
    DWORD_PTR ignored;
    EXPECT(SendMessageTimeoutW(tray, WM_CLOSE, 0, 0, SMTO_NORMAL, kWaitMs, &ignored) != 0);
    EXPECT(last.WaitBroken(2000));
    EXPECT(Records().empty());

    DestroyIcon(icon);
    CloseHandle(rules);
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc > 1 && std::wstring(argv[1]) == L"--icon-host") return IconHostMain();

    std::wstring self = ProcessPath(GetCurrentProcess());
    wchar_t suffix[32];
    swprintf_s(suffix, L"%lu", GetCurrentProcessId());
    g_pipeName = std::wstring(L"\\\\.\\pipe\\CleanTray.RuntimeTests.") + suffix;
    g_rulesName = std::wstring(L"Local\\CleanTray.RuntimeTests.Rules.") + suffix;
    g_syncEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);

    HMODULE dll = LoadLibraryW(CT_HOOK_DLL);
    if (!dll) {
        std::fprintf(stderr, "cannot load %ls (%lu)\n", CT_HOOK_DLL, GetLastError());
        return 1;
    }
    g_activate = reinterpret_cast<ActivateFn>(GetProcAddress(dll, "CleanTrayTestActivate"));
    g_injectFault = reinterpret_cast<InjectFaultFn>(GetProcAddress(dll, "CleanTrayTestInjectFault"));
    g_setFaultBroadcast =
        reinterpret_cast<SetFaultBroadcastFn>(GetProcAddress(dll, "CleanTrayTestSetFaultBroadcast"));
    auto normalize = reinterpret_cast<NormalizeFn>(GetProcAddress(dll, "CleanTrayNormalizeAppKey"));
    if (!g_activate || !g_injectFault || !g_setFaultBroadcast || !normalize ||
        !GetProcAddress(dll, "CleanTrayGetMsgProc")) {
        std::fprintf(stderr, "missing exports\n");
        return 1;
    }

    Child child;
    if (!child.Start(self)) {
        std::fprintf(stderr, "icon host child did not start\n");
        return 1;
    }
    CheckNormalizeExport(normalize, ProcessPath(child.Process()), child.CommandLine());

    TrayStart start{nullptr, CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    HANDLE trayThread = CreateThread(nullptr, 0, TrayThread, &start, 0, nullptr);
    if (!trayThread || WaitForSingleObject(start.ready, kWaitMs) != WAIT_OBJECT_0 || !start.hwnd) {
        std::fprintf(stderr, "fake tray window did not start\n");
        return 1;
    }

    RunAll(start.hwnd, child);

    if (IsWindow(start.hwnd)) {
        DWORD_PTR ignored;
        SendMessageTimeoutW(start.hwnd, WM_CLOSE, 0, 0, SMTO_NORMAL, kWaitMs, &ignored);
    }
    if (WaitForSingleObject(trayThread, kWaitMs) != WAIT_OBJECT_0) {
        ++g_failures;
        std::fprintf(stderr, "fake tray thread did not exit\n");
    }
    std::printf("runtime tests: %d failed checks\n", g_failures);
    return g_failures;
}
