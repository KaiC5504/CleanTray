using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TrayProbe;

internal static class Program
{
    private const string Usage =
        "usage: TrayProbe [--guid] [--version 0|3|4] [--popup <title> <text>] [--getrect] [--hide-self] [--name <tip>] [--spin <hz>]";

    [STAThread]
    private static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        using var log = new ProbeLog(options.Name);
        // Not disposed: a signal handler thread may still be returning from Wait when Main exits.
        var exited = new ManualResetEventSlim();
        var window = new ProbeWindow(options, log);

        // Ctrl+C, Ctrl+Break and console close all route through the message loop so the
        // icon is deleted on the thread that owns it before the process goes away.
        void OnSignal(PosixSignalContext context)
        {
            context.Cancel = true;
            window.PostClose();
            exited.Wait(TimeSpan.FromSeconds(3));
        }

        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        using var sigQuit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, OnSignal);
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);

        window.Start();
        Application.Run();
        exited.Set();
        return 0;
    }
}

internal sealed record Options(
    string Name,
    uint Version,
    bool UseGuid,
    string? PopupTitle,
    string? PopupText,
    bool GetRect,
    bool HideSelf,
    double SpinHz)
{
    public static Options Parse(string[] args)
    {
        string name = "TrayProbe";
        uint version = 4;
        bool useGuid = false, getRect = false, hideSelf = false;
        string? popupTitle = null, popupText = null;
        double spinHz = 0;

        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");

            switch (args[i])
            {
                case "--guid": useGuid = true; break;
                case "--getrect": getRect = true; break;
                case "--hide-self": hideSelf = true; break;
                case "--name": name = Next(); break;
                case "--version":
                    version = Next() switch
                    {
                        "0" => 0,
                        "3" => 3,
                        "4" => 4,
                        var v => throw new ArgumentException($"unsupported version {v}"),
                    };
                    break;
                case "--popup":
                    popupTitle = Next();
                    popupText = Next();
                    break;
                case "--spin":
                    if (!double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out spinHz) || spinHz <= 0 || spinHz > 1000)
                        throw new ArgumentException("--spin needs a rate between 0 and 1000 Hz");
                    break;
                default:
                    throw new ArgumentException($"unknown argument {args[i]}");
            }
        }

        if (name.Length == 0 || name.Length > 127)
            throw new ArgumentException("--name must be 1-127 characters (szTip limit)");

        return new Options(name, version, useGuid, popupTitle, popupText, getRect, hideSelf, spinHz);
    }
}

internal sealed class ProbeLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _file;

    public ProbeLog(string name)
    {
        string safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        FilePath = Path.Combine(Path.GetTempPath(), $"TrayProbe-{safe}.log");
        _file = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public string FilePath { get; }

    public void Write(string line)
    {
        lock (_gate)
        {
            Console.WriteLine(line);
            _file.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _file.Dispose();
    }
}

internal sealed class ProbeWindow : NativeWindow
{
    private const uint IconId = 1;
    private const int CallbackMessage = Native.WM_APP + 1;

    private readonly Options _options;
    private readonly ProbeLog _log;
    private readonly Guid _guid;
    private readonly Icon _icon = (Icon)SystemIcons.Application.Clone();
    private readonly object _nidGate = new();
    private readonly int _taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
    private System.Windows.Forms.Timer? _rectTimer;
    private Thread? _spinThread;
    private readonly ManualResetEventSlim _spinStop = new();
    private bool _closing;

    public ProbeWindow(Options options, ProbeLog log)
    {
        _options = options;
        _log = log;
        _guid = options.UseGuid ? GuidFor(options.Name) : Guid.Empty;
        CreateHandle(new CreateParams { Caption = "TrayProbe-" + options.Name });
    }

    public void Start()
    {
        _log.Write(
            $"START name={_options.Name} pid={Environment.ProcessId} hwnd=0x{Handle:X} uid={IconId} " +
            $"callback=0x{CallbackMessage:X4} version={_options.Version} guid={(_options.UseGuid ? _guid.ToString("B") : "none")} log={_log.FilePath}");

        AddIcon();

        if (_options.GetRect)
        {
            QueryRect();
            _rectTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _rectTimer.Tick += (_, _) => QueryRect();
            _rectTimer.Start();
        }

        if (_options.SpinHz > 0)
        {
            _spinThread = new Thread(SpinLoop) { IsBackground = true, Name = "spin" };
            _spinThread.Start();
        }
    }

    public void PostClose() => Native.PostMessageW(Handle, Native.WM_CLOSE, 0, 0);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == CallbackMessage)
        {
            _log.Write($"CB msg=0x{m.Msg:X4} wParam=0x{(nuint)m.WParam:X} lParam=0x{(nuint)m.LParam:X}{Describe(m.WParam, m.LParam)}");
            return;
        }

        if (m.Msg == _taskbarCreated && _taskbarCreated != 0)
        {
            _log.Write("TASKBARCREATED re-adding icon");
            AddIcon();
            return;
        }

        if (m.Msg == Native.WM_CLOSE)
        {
            Shutdown();
            return;
        }

        base.WndProc(ref m);
    }

    private void AddIcon()
    {
        uint tipFlags = Native.NIF_TIP | (_options.Version >= 4 ? Native.NIF_SHOWTIP : 0);

        var add = NewData(Native.NIF_MESSAGE | Native.NIF_ICON | tipFlags);
        add.uCallbackMessage = CallbackMessage;
        add.hIcon = _icon.Handle;
        add.szTip = _options.Name;
        if (!Notify("ADD", Native.NIM_ADD, ref add) && _options.UseGuid)
        {
            // A GUID icon left behind by a killed run blocks re-adding the same GUID.
            var stale = NewData(0);
            Notify("DELETE", Native.NIM_DELETE, ref stale);
            Notify("ADD", Native.NIM_ADD, ref add);
        }

        var setVersion = NewData(0);
        setVersion.uVersion = _options.Version;
        Notify("SETVERSION", Native.NIM_SETVERSION, ref setVersion);

        var modify = NewData(tipFlags);
        modify.szTip = $"{_options.Name} v{_options.Version}";
        Notify("MODIFY", Native.NIM_MODIFY, ref modify);

        if (_options.HideSelf)
        {
            var hide = NewData(Native.NIF_STATE);
            hide.dwState = Native.NIS_HIDDEN;
            hide.dwStateMask = Native.NIS_HIDDEN;
            Notify("MODIFY(hide)", Native.NIM_MODIFY, ref hide);
        }

        if (_options.PopupTitle is not null)
        {
            var popup = NewData(Native.NIF_INFO);
            popup.szInfoTitle = _options.PopupTitle;
            popup.szInfo = _options.PopupText ?? "";
            popup.dwInfoFlags = Native.NIIF_INFO;
            Notify("MODIFY(popup)", Native.NIM_MODIFY, ref popup);
        }
    }

    private void QueryRect()
    {
        var id = new Native.NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONIDENTIFIER>(),
            hWnd = Handle,
            uID = IconId,
            guidItem = _guid,
        };
        int hr = Native.Shell_NotifyIconGetRect(ref id, out var r);
        _log.Write($"RECT {r.left},{r.top},{r.right},{r.bottom} hr=0x{hr:X8}");
    }

    private void SpinLoop()
    {
        Native.timeBeginPeriod(1);
        try
        {
            var clock = Stopwatch.StartNew();
            var period = TimeSpan.FromSeconds(1 / _options.SpinHz);
            uint tipFlags = Native.NIF_TIP | (_options.Version >= 4 ? Native.NIF_SHOWTIP : 0);
            long count = 0, reported = 0;
            var lastReport = TimeSpan.Zero;
            _log.Write($"SPIN start hz={_options.SpinHz.ToString(CultureInfo.InvariantCulture)}");

            while (!_spinStop.IsSet)
            {
                count++;
                var data = NewData(tipFlags);
                data.szTip = $"{_options.Name} spin {count}";
                lock (_nidGate)
                {
                    if (_closing)
                        break;
                    Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref data);
                }

                var now = clock.Elapsed;
                if (now - lastReport >= TimeSpan.FromSeconds(1))
                {
                    double rate = (count - reported) / (now - lastReport).TotalSeconds;
                    _log.Write($"SPIN count={count} rate={rate.ToString("F1", CultureInfo.InvariantCulture)}/s");
                    reported = count;
                    lastReport = now;
                }

                var wait = period * count - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                    _spinStop.Wait(wait);
            }
        }
        finally
        {
            Native.timeEndPeriod(1);
        }
    }

    private void Shutdown()
    {
        if (_closing)
            return;

        _rectTimer?.Dispose();
        _spinStop.Set();
        lock (_nidGate)
            _closing = true;
        _spinThread?.Join(TimeSpan.FromSeconds(2));

        var delete = NewData(0);
        Notify("DELETE", Native.NIM_DELETE, ref delete);
        _log.Write("EXIT");

        DestroyHandle();
        _icon.Dispose();
        Application.ExitThread();
    }

    private Native.NOTIFYICONDATAW NewData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
        hWnd = Handle,
        uID = IconId,
        uFlags = flags | (_options.UseGuid ? Native.NIF_GUID : 0),
        guidItem = _guid,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private bool Notify(string label, uint message, ref Native.NOTIFYICONDATAW data)
    {
        bool ok;
        lock (_nidGate)
            ok = Native.Shell_NotifyIconW(message, ref data);
        _log.Write($"NIM {label} {(ok ? "ok" : "failed")}");
        return ok;
    }

    private string Describe(nint wParam, nint lParam)
    {
        // Version 4 packs the event in LOWORD(lParam) and the uID in HIWORD(lParam);
        // older versions put the uID in wParam and the event in lParam.
        uint evt, uid;
        if (_options.Version >= 4)
        {
            evt = (uint)lParam & 0xFFFF;
            uid = ((uint)lParam >> 16) & 0xFFFF;
        }
        else
        {
            evt = (uint)lParam;
            uid = (uint)wParam;
        }

        return EventNames.TryGetValue(evt, out var name) ? $" ({name} uid={uid})" : $" (0x{evt:X} uid={uid})";
    }

    private static readonly Dictionary<uint, string> EventNames = new()
    {
        [0x007B] = "WM_CONTEXTMENU",
        [0x0200] = "WM_MOUSEMOVE",
        [0x0201] = "WM_LBUTTONDOWN",
        [0x0202] = "WM_LBUTTONUP",
        [0x0203] = "WM_LBUTTONDBLCLK",
        [0x0204] = "WM_RBUTTONDOWN",
        [0x0205] = "WM_RBUTTONUP",
        [0x0206] = "WM_RBUTTONDBLCLK",
        [0x0207] = "WM_MBUTTONDOWN",
        [0x0208] = "WM_MBUTTONUP",
        [0x0209] = "WM_MBUTTONDBLCLK",
        [0x0400] = "NIN_SELECT",
        [0x0401] = "NIN_KEYSELECT",
        [0x0402] = "NIN_BALLOONSHOW",
        [0x0403] = "NIN_BALLOONHIDE",
        [0x0404] = "NIN_BALLOONTIMEOUT",
        [0x0405] = "NIN_BALLOONUSERCLICK",
        [0x0406] = "NIN_POPUPOPEN",
        [0x0407] = "NIN_POPUPCLOSE",
    };

    // Stable per name so a --guid probe keeps the same identity across runs.
    private static Guid GuidFor(string name) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes("CleanTray.TrayProbe/" + name)));
}

internal static class Native
{
    public const int WM_CLOSE = 0x0010;
    public const int WM_APP = 0x8000;

    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_STATE = 0x08, NIF_INFO = 0x10, NIF_GUID = 0x20, NIF_SHOWTIP = 0x80;
    public const uint NIS_HIDDEN = 0x01;
    public const uint NIIF_INFO = 0x01;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONIDENTIFIER
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left, top, right, bottom;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("shell32.dll")]
    public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("winmm.dll")]
    public static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    public static extern uint timeEndPeriod(uint uPeriod);
}
