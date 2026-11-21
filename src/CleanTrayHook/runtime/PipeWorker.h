#pragma once
#include <windows.h>

#include <cstdint>
#include <string>
#include <vector>

#include "SharedRules.h"
#include "ShellHost.h"
#include "core/Coalescer.h"
#include "core/RulesView.h"
#include "core/TrayInterceptor.h"

namespace ct {

// Work the worker hands to the tray thread, which owns every interceptor mutation.
struct HookCommand {
    enum class Kind : uint8_t {
        RulesChanged,
        Click,
        PopupResult,
        SetAnchor,
        ReleaseAllAndIdle,
        SweepDead,
        SetCleanTrayPid,
    };
    Kind kind;
    uint64_t iconId = 0;
    uint8_t arg = 0;
    int32_t a = 0, b = 0, c = 0, d = 0;
    DWORD pid = 0;
};

struct ICommandPoster {
    // Never blocks; false if the command could not be queued.
    virtual bool PostToTray(const HookCommand& c) noexcept = 0;

protected:
    ~ICommandPoster() = default;
};

// The interceptor's sink. Called on the tray thread; only takes short locks.
class EventQueue final : public IEventSink {
public:
    struct Drained {
        Coalescer::Batch icons;
        std::vector<std::vector<BYTE>> popups;
    };

    void IconChanged(uint64_t id) override { coalescer_.MarkChanged(id); }
    void IconRemoved(uint64_t id) override { coalescer_.MarkRemoved(id); }
    void Popup(uint64_t id, DWORD infoFlags, const std::wstring& title, const std::wstring& text) override;

    HANDLE Event() const { return coalescer_.Event(); }
    Drained Drain();

private:
    Coalescer coalescer_;
    SRWLOCK lock_ = SRWLOCK_INIT;
    std::vector<std::vector<BYTE>> popups_;
};

class PipeWorker {
public:
    PipeWorker(const TrayInterceptor& interceptor, EventQueue& events, ShellHost& host,
               ICommandPoster& poster, std::wstring pipeName, std::wstring rulesName, HANDLE stop);
    ~PipeWorker();
    PipeWorker(const PipeWorker&) = delete;
    PipeWorker& operator=(const PipeWorker&) = delete;

    // Thread body. Returns when stop is signalled or the session ends; in the latter
    // case it has asked the tray thread to release everything and go idle.
    void Run() noexcept;

    // After a structured exception killed Run: drops the pipe so CleanTray sees the
    // disconnect, without touching anything the fault may have left half-done.
    void Abandon() noexcept;

private:
    enum class End { Stopped, Idle };

    End Session();
    bool Connect();
    bool Write(const std::vector<BYTE>& bytes);
    bool AppendSnapshot(uint64_t id, std::vector<BYTE>& out);
    bool Resync(std::vector<std::vector<BYTE>> popups);
    bool SendEvents();
    bool StartRead();
    bool FinishRead(bool& idleRequested, bool& rulesChanged);
    void Dispatch(const Command& c, bool& idleRequested, bool& rulesChanged);
    void Kick();
    void Post(const HookCommand& c);
    bool PostReliably(const HookCommand& c);
    void Close();
    bool Stopping() const;

    const TrayInterceptor& interceptor_;
    EventQueue& events_;
    ShellHost& host_;
    ICommandPoster& poster_;
    std::wstring pipeName_;
    HANDLE stop_;
    SharedRules sharedRules_;
    RulesView rules_;

    HANDLE pipe_ = INVALID_HANDLE_VALUE;
    HANDLE server_ = nullptr;
    DWORD serverPid_ = 0;
    HANDLE readEvent_, writeEvent_;
    OVERLAPPED readOv_{};
    bool readPending_ = false;
    BYTE chunk_[512];
    std::vector<BYTE> inbox_;
    UINT taskbarCreated_;
};

}  // namespace ct
