using System.Windows;
using System.Windows.Threading;
using CleanTray.Core;
using CleanTray.Hook;
using CleanTray.Native;
using CleanTray.Services;
using CleanTray.Tray;
using CleanTray.UI;

namespace CleanTray;

public partial class App : Application
{
    const string InstanceMutexName = @"Local\CleanTray.Instance";
    const string OpenSettingsEventName = @"Local\CleanTray.OpenSettings";
    // Lets a second launch (`CleanTray --open-panel` / `--exit`) drive the running instance without the mouse.
    const string OpenPanelEventName = @"Local\CleanTray.OpenPanel";
    const string ExitEventName = @"Local\CleanTray.Exit";
    const uint AsfwAny = uint.MaxValue;
    static readonly TimeSpan ReleaseWait = TimeSpan.FromSeconds(1);
    static readonly TimeSpan SaveWindow = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan SignalRetryFor = TimeSpan.FromSeconds(3);
    static readonly object AutostartGate = new();

    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CleanTray", "settings.json");

    readonly IconRegistry _registry = new();
    readonly List<RegisteredWaitHandle> _waits = new();
    Mutex? _instance;
    AppSettings _settings = new();
    SettingsPersister _persister = new(SettingsPath, SettingsLoadStatus.Missing);
    Debouncer? _saveSoon;
    RulesPublisher? _rules;
    MasterIcon? _master;
    HotkeyService? _hotkey;
    PopupToasts? _toasts;
    HookHost? _host;
    PanelWindow? _panel;
    SettingsWindow? _settingsWindow;
    string? _ownKey;
    long _panelClosedAt;
    bool _dirty;
    bool _paused;
    bool _hookStopped;
    bool _shutDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // A failing callback must not take the tray app down with every captured icon in it.
        DispatcherUnhandledException += (_, ev) =>
        {
            ev.Handled = true;
            Log.Write($"unhandled: {LogText.Error(ev.Exception)}");
        };
        try
        {
            Start(e.Args);
        }
        catch (Exception ex)
        {
            // Handled dispatcher exceptions would otherwise leave this process alive and holding the mutex.
            Log.Write($"startup failed: {LogText.Error(ex)}");
            ExitApp(1);
        }
    }

    void Start(string[] args)
    {
        if (args.Contains("--register-autostart"))
        {
            RegisterAutostartAndExit();
            return;
        }
        // Windows relaunches the exe with this when a toast is clicked after the app has gone;
        // the balloon it belonged to is long over, so there is nothing to start.
        if (args.Contains("-ToastActivated"))
        {
            _shutDown = true;
            Shutdown();
            return;
        }
        var openPanel = args.Contains("--open-panel");
        var openSettings = args.Contains("--settings");
        var exit = args.Contains("--exit");

        _instance = new Mutex(true, InstanceMutexName, out var first);
        if (!first || exit)
        {
            if (!first)
                Signal(exit ? ExitEventName : openPanel ? OpenPanelEventName : OpenSettingsEventName);
            _instance.Dispose();
            _instance = null;
            _shutDown = true;
            Shutdown();
            return;
        }

        Log.Write($"start, pid {Environment.ProcessId}");
        SessionEnding += (_, _) => OnSessionEnding();
        Listen(OpenPanelEventName, OpenPanel);
        Listen(OpenSettingsEventName, OpenSettings);
        Listen(ExitEventName, () => ExitApp());

        var load = SettingsStore.LoadWithStatus(SettingsPath);
        _settings = load.Settings;
        _persister = new SettingsPersister(SettingsPath, load.Status);
        _saveSoon = new Debouncer(SaveWindow, () => Dispatcher.BeginInvoke(() => CommitNow()));
        // Unreadable means the real file exists but could not be read: run on defaults, never save over it.
        Log.Write($"settings: {load.Status}, {_settings.Apps.Count} app rules");
        _ownKey = Environment.ProcessPath is { } self ? AppKeys.Normalize(self) : null;
        if (_ownKey is null) Log.Write("app key: the hook DLL could not key CleanTray itself");

        try
        {
            _rules = new RulesPublisher();
            _rules.Publish(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the table the DLL falls back to showing everything.
            Log.Write($"rules: cannot open the mapping: {LogText.Error(ex)}");
        }

        _master = new MasterIcon();
        _master.LeftClick += TogglePanel;
        _master.TaskbarCreated += () => _host?.OnTaskbarCreated();
        _master.SettingsClicked += OpenSettings;
        _master.PauseClicked += TogglePause;
        _master.ExitClicked += () => ExitApp();
        _master.SessionEnding += OnSessionEnding;
        _master.SetVisible(_settings.ShowMasterIcon);

        _hotkey = new HotkeyService(_master);
        _hotkey.Pressed += () => Dispatcher.BeginInvoke(OpenPanelAtCursor);
        _hotkey.Register(_settings.Hotkey);

        _toasts = new PopupToasts(Send);

        _host = new HookHost(Dispatcher);
        _host.EventReceived += OnHookEvent;
        // Captured apps asking for their icon's rect get this until the panel first opens.
        _host.Connected += () =>
        {
            if (_panel is not null) _panel.SendAnchor();
            else if (_master is not null) SendAnchor(_master.GetScreenRect());
        };
        _host.Disconnected += () => _registry.Clear();
        _host.StateChanged += OnHostState;
        _ = _host.StartAsync();

        _ = ApplyAutostart(force: false);

        if (openPanel) Dispatcher.BeginInvoke(OpenPanel, DispatcherPriority.ApplicationIdle);
        if (openSettings) Dispatcher.BeginInvoke(OpenSettings, DispatcherPriority.ApplicationIdle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ShutDownHook();
        Log.Close();
        base.OnExit(e);
    }

    void OnHookEvent(HookEvent ev)
    {
        switch (ev)
        {
            case IconUpsertEvent u:
                // Animated icons send hundreds of upserts a second; one line per new icon or capture flip is enough.
                if (_registry.CaptureChanged(u)) Log.Write(LogText.Icon(u));
                _registry.Apply(ev);
                if (AppCatalog.RecordSeen(_settings, u, DateTime.UtcNow, DisplayNames.For, _ownKey))
                {
                    _dirty = true;
                    _saveSoon?.Request();
                }
                break;
            case IconRemovedEvent r:
                Log.Write($"icon {r.IconId} removed");
                _registry.Apply(ev);
                break;
            case PopupEvent p:
                _toasts?.Show(p, AppNameFor(p.IconId), _registry.All.FirstOrDefault(i => i.Id == p.IconId)?.Pid ?? 0);
                break;
        }
    }

    string AppNameFor(ulong iconId)
    {
        var icon = _registry.All.FirstOrDefault(i => i.Id == iconId);
        if (icon is null) return "An app";
        var rule = _settings.Apps.FirstOrDefault(a => a.Key == icon.AppKey);
        return DisplayNames.Choose(rule?.DisplayName, icon.ExePath);
    }

    /// <summary>
    /// Saves, then republishes the rules and tells the DLL. The rules go out even when the save
    /// is blocked or fails, so a change still takes effect for this session.
    /// </summary>
    SaveOutcome CommitNow()
    {
        _saveSoon?.Cancel();
        var outcome = _persister.Save(_settings);
        _dirty = false;
        if (outcome == SaveOutcome.Blocked) Log.Write("settings: not saved, the file could not be read at startup");
        else if (outcome == SaveOutcome.Failed) Log.Write("settings: save failed");
        _rules?.Publish(_settings);
        _host?.Send(CommandWriter.RulesChanged());
        return outcome;
    }

    void OnHostState(HostState s)
    {
        Log.Write($"host: {s}");
        // An attempt still in flight when the user paused reports late; the paused tooltip wins.
        if (_paused && s != HostState.Paused) return;
        switch (s)
        {
            case HostState.Attached:
                _master?.SetTip("CleanTray");
                break;
            case HostState.AttachFailed:
                _master?.SetTip("CleanTray: couldn't attach to the taskbar");
                break;
            case HostState.Paused:
                _master?.SetTip("CleanTray (paused)");
                break;
        }
    }

    void TogglePanel()
    {
        if (_panel is not null)
        {
            _panel.Close();
            return;
        }
        // The panel's hooks may have closed it on this same click, e.g. on a taskbar created after it opened.
        if (Environment.TickCount64 - _panelClosedAt < 300) return;
        OpenPanel();
    }

    void OpenPanel() => ShowPanel(atCursor: false);

    void OpenPanelAtCursor() => ShowPanel(atCursor: true);

    void ShowPanel(bool atCursor)
    {
        if (_hookStopped || _master is null) return;
        if (_panel is not null) return;
        Func<Rect>? placeAt = null;
        if (atCursor)
        {
            Win32.GetCursorPos(out var pt);
            var here = new Rect(pt.X, pt.Y, 1, 1);
            placeAt = () => here;
        }
        _panel = new PanelWindow(_registry, Send, _master.GetScreenRect, placeAt);
        _panel.Closed += (_, _) =>
        {
            _panel = null;
            _panelClosedAt = Environment.TickCount64;
        };
        _panel.Show();
    }

    void OpenSettings()
    {
        if (_shutDown || _hookStopped) return;
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(new SettingsHost
            {
                Settings = _settings,
                Registry = _registry,
                CanSave = _persister.CanSave,
                OwnKey = _ownKey,
                KnownApps = ReadKnownApps,
                Commit = CommitNow,
                HotkeyRegistered = () => _hotkey?.IsRegistered == true,
                RegisterHotkey = text => _hotkey?.Register(text) == true,
                ShowMasterIcon = visible => _master?.SetVisible(visible),
                ApplyAutostart = () => _ = ApplyAutostart(force: false),
            });
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Present();
    }

    static IReadOnlyList<KnownApp> ReadKnownApps()
    {
        var started = Environment.TickCount64;
        var apps = KnownApps.From(NotifyIconSettingsRegistry.Read(), new KnownAppSystem(
            Win32.KnownFolderPath, File.Exists, path => AppKeys.Normalize(path), DisplayNames.Description));
        Log.Write($"settings: {apps.Count} tray apps known to Windows ({Environment.TickCount64 - started} ms)");
        return apps;
    }

    void Send(byte[] command) => _host?.Send(command);

    void SendAnchor(Rect r) => Send(CommandWriter.SetAnchor((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom));

    void TogglePause()
    {
        if (_host is null || _master is null) return;
        _paused = !_paused;
        _master.Paused = _paused;
        if (_paused)
        {
            _panel?.Close();
            _host.Pause();
        }
        else
        {
            _master.SetTip("CleanTray");
            _host.Resume();
        }
    }

    void ExitApp(int exitCode = 0)
    {
        try
        {
            ShutDownHook();
        }
        finally
        {
            Shutdown(exitCode);
        }
    }

    void OnSessionEnding()
    {
        Guard("hook", StopHook);
        Dispatcher.BeginInvoke(() => ExitApp());
    }

    /// <summary>Hands every captured icon back to the tray. Safe to call from the master icon's WndProc.</summary>
    void StopHook()
    {
        if (_hookStopped) return;
        _hookStopped = true;
        Log.Write("shutting down");
        // Each step is guarded so nothing before it can skip the release in _host.Stop.
        Guard("signals", () =>
        {
            foreach (var w in _waits) w.Unregister(null);
        });
        Guard("panel", () => _panel?.Close());
        Guard("toasts", () => _toasts?.Shutdown());
        Guard("release", () => _host?.Stop(ReleaseWait));
        _host = null;
    }

    void ShutDownHook()
    {
        if (_shutDown) return;
        _shutDown = true;
        Guard("hook", StopHook);
        Guard("settings window", () => _settingsWindow?.Close());
        Guard("settings", () =>
        {
            // The debounced save may have fired but not yet run on the dispatcher, so pending is not enough.
            if (_dirty) _persister.Save(_settings);
        });
        Guard("save timer", () => _saveSoon?.Dispose());
        Guard("hotkey", () =>
        {
            _hotkey?.Dispose();
            _hotkey = null;
        });
        Guard("master icon", () =>
        {
            _master?.Dispose();
            _master = null;
        });
        Guard("rules", () =>
        {
            _rules?.Dispose();
            _rules = null;
        });
        Guard("instance", ReleaseInstance);
        Log.Write("stopped");
    }

    // One failing step must not skip the rest, least of all the mutex release.
    static void Guard(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Write($"shutdown: {step} failed: {LogText.Error(ex)}");
        }
    }

    void ReleaseInstance()
    {
        if (_instance is null) return;
        try
        {
            _instance.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread, which only happens if startup failed on another one.
        }
        _instance.Dispose();
        _instance = null;
    }

    /// <summary>The install script's entry point: applies the Start with Windows setting and exits.</summary>
    void RegisterAutostartAndExit()
    {
        _shutDown = true;
        var load = SettingsStore.LoadWithStatus(SettingsPath);
        _settings = load.Settings;
        _persister = new SettingsPersister(SettingsPath, load.Status);
        ApplyAutostart(force: true).GetAwaiter().GetResult();
        Shutdown();
    }

    /// <summary>
    /// Only an installed copy registers itself: a dev or build-output run must never leave a
    /// scheduled task pointing at its own folder. An unreadable settings file means the setting
    /// is unknown, so the registration is left as it is.
    /// </summary>
    Task ApplyAutostart(bool force)
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !InstallLocation.IsInstalled(exe))
        {
            Log.Write("autostart skipped (not installed)");
            return Task.CompletedTask;
        }
        if (!_persister.CanSave)
        {
            Log.Write("autostart unchanged (settings unreadable)");
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            lock (AutostartGate)
            {
                try
                {
                    // Read under the lock so the last request to run always applies the latest setting.
                    if (_settings.StartWithWindows)
                    {
                        if (force || !Autostart.IsCurrent(exe))
                            Log.Write($"autostart: enabled through {Autostart.Enable(exe)}");
                    }
                    else if (force || Autostart.IsEnabled())
                    {
                        Autostart.Disable();
                        Log.Write("autostart: disabled");
                    }
                }
                catch (Exception ex)
                {
                    Log.Write($"autostart: failed: {LogText.Error(ex)}");
                }
            }
        });
    }

    void Listen(string name, Action onSignal)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        _waits.Add(ThreadPool.RegisterWaitForSingleObject(ev,
            (_, _) => Dispatcher.BeginInvoke(onSignal), null, Timeout.Infinite, executeOnlyOnce: false));
    }

    // The first instance creates its events a moment after taking the mutex, so a quick second launch waits for them.
    static void Signal(string name)
    {
        EventWaitHandle? ev = null;
        var deadline = Environment.TickCount64 + (long)SignalRetryFor.TotalMilliseconds;
        while (!EventWaitHandle.TryOpenExisting(name, out ev))
        {
            if (Environment.TickCount64 >= deadline) return;
            Thread.Sleep(50);
        }
        using (ev)
        {
            // The running instance needs this to bring its window to the front.
            Win32.AllowSetForegroundWindow(AsfwAny);
            ev.Set();
        }
    }
}
