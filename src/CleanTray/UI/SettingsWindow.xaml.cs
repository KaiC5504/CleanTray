using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CleanTray.Core;

namespace CleanTray.UI;

public partial class SettingsWindow : Window
{
    static readonly TimeSpan HotkeyCommitDelay = TimeSpan.FromMilliseconds(700);
    static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(1);

    readonly SettingsHost _host;
    readonly ObservableCollection<AppRow> _rows = new();
    readonly Dictionary<string, AppRow> _byKey = new();
    readonly Dictionary<string, byte[]?> _snapshots = new();
    readonly DispatcherTimer _refresh;
    readonly DispatcherTimer _hotkeyCommit;
    bool _loading;
    bool _closed;

    internal SettingsWindow(SettingsHost host)
    {
        InitializeComponent();
        _host = host;
        Apps.ItemsSource = _rows;

        _loading = true;
        ShowIcon.IsChecked = host.Settings.ShowMasterIcon;
        ShowIconHint.Visibility = host.Settings.ShowMasterIcon ? Visibility.Collapsed : Visibility.Visible;
        HotkeyBox.Text = host.Settings.Hotkey;
        Autostart.IsChecked = host.Settings.StartWithWindows;
        HideNew.IsChecked = host.Settings.CaptureNewByDefault;
        _loading = false;

        ShowBanner(host.CanSave ? SaveOutcome.Saved : SaveOutcome.Blocked);
        if (!host.HotkeyRegistered()) HotkeyStatus.Text = "Shortcut in use";

        _hotkeyCommit = new DispatcherTimer(HotkeyCommitDelay, DispatcherPriority.Normal, (_, _) => CommitHotkey(), Dispatcher);
        _hotkeyCommit.Stop();
        _refresh = new DispatcherTimer(RefreshEvery, DispatcherPriority.Background, (_, _) => Sync(), Dispatcher);
        foreach (var entry in AppList.Build(host.Settings, [], host.OwnKey)) Add(entry);
        Sync();
        Closed += (_, _) =>
        {
            _closed = true;
            _refresh.Stop();
            _hotkeyCommit.Stop();
        };
        _ = AddKnownAppsAsync();
    }

    // Reading version info from every exe Windows remembers can take seconds on a cold disk.
    async Task AddKnownAppsAsync()
    {
        IReadOnlyList<KnownApp> known;
        try
        {
            known = await Task.Run(_host.KnownApps);
        }
        catch (Exception e)
        {
            Log.Write($"settings: reading Windows' tray apps failed: {LogText.Error(e)}");
            return;
        }
        if (_closed) return;
        foreach (var app in known) _snapshots.TryAdd(app.Key, app.IconPng);
        foreach (var row in _rows) row.UseSnapshot(_snapshots.GetValueOrDefault(row.Key));
        foreach (var entry in AppList.Build(_host.Settings, known, _host.OwnKey))
            if (!_byKey.ContainsKey(entry.Rule.Key)) Add(entry);
        Sync();
    }

    /// <summary>Shows the window and takes the foreground, which a hotkey or a second launch may need help with.</summary>
    internal void Present()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Adds rows for newly seen apps and refreshes the existing ones, keeping them in place.</summary>
    internal void Sync()
    {
        var now = DateTime.UtcNow;
        var running = _host.Registry.All.ToLookup(i => i.AppKey);
        foreach (var rule in _host.Settings.Apps)
        {
            if (AppList.IsCleanTray(rule.Key, _host.OwnKey)) continue;
            if (_byKey.TryGetValue(rule.Key, out var row)) row.Bind(rule);
            else Add(new AppListEntry(AppListGroup.Seen, rule, _snapshots.GetValueOrDefault(rule.Key)));
        }
        foreach (var row in _rows) row.Refresh(running[row.Key], now);
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Add(AppListEntry entry)
    {
        var row = new AppRow(entry, draft => AppList.Adopt(_host.Settings, draft, _host.OwnKey), Commit);
        _byKey[row.Key] = row;
        var i = 0;
        while (i < _rows.Count && AppList.Compare(_rows[i].Group, _rows[i].Name, row.Group, row.Name) <= 0) i++;
        _rows.Insert(i, row);
    }

    void Commit() => ShowBanner(_host.Commit());

    void ShowBanner(SaveOutcome outcome)
    {
        Banner.Text = outcome switch
        {
            SaveOutcome.Blocked => "CleanTray couldn't read your settings file, so changes made now won't be saved.",
            SaveOutcome.Failed => "CleanTray couldn't save your settings.",
            _ => "",
        };
        Banner.Visibility = outcome == SaveOutcome.Saved ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnShowIconChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var show = ShowIcon.IsChecked == true;
        _host.Settings.ShowMasterIcon = show;
        ShowIconHint.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        _host.ShowMasterIcon(show);
        Commit();
    }

    void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.Settings.StartWithWindows = Autostart.IsChecked == true;
        Commit();
        _host.ApplyAutostart();
    }

    // Asked after the toggle returns, so a UI Automation caller is not left waiting on the dialog.
    void OnHideNewChecked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (new CaptureWarningDialog { Owner = this }.ShowDialog() == true)
            {
                _host.Settings.CaptureNewByDefault = true;
                Commit();
                return;
            }
            _loading = true;
            HideNew.IsChecked = false;
            _loading = false;
        });
    }

    void OnHideNewUnchecked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.Settings.CaptureNewByDefault = false;
        Commit();
    }

    void OnHotkeyTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _hotkeyCommit.Stop();
        var text = HotkeyBox.Text.Trim();
        if (text == _host.Settings.Hotkey && _host.HotkeyRegistered())
        {
            HotkeyStatus.Text = "";
            return;
        }
        if (!HotkeyParser.TryParse(text, out _, out _))
        {
            HotkeyStatus.Text = "Use Ctrl, Alt, Shift or Win plus a key, like Ctrl+Alt+T";
            return;
        }
        HotkeyStatus.Text = "";
        _hotkeyCommit.Start();
    }

    void OnHotkeyCommit(object sender, RoutedEventArgs e)
    {
        if (_hotkeyCommit.IsEnabled) CommitHotkey();
    }

    void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_hotkeyCommit.IsEnabled) CommitHotkey();
    }

    void CommitHotkey()
    {
        _hotkeyCommit.Stop();
        var text = HotkeyBox.Text.Trim();
        if (!HotkeyParser.TryParse(text, out var modifiers, out var vk)) return;
        if (!_host.RegisterHotkey(text))
        {
            HotkeyStatus.Text = "Shortcut in use";
            return;
        }
        HotkeyStatus.Text = "";
        var normal = HotkeyParser.Format(modifiers, vk);
        if (normal != HotkeyBox.Text)
        {
            _loading = true;
            HotkeyBox.Text = normal;
            _loading = false;
        }
        _host.Settings.Hotkey = normal;
        Commit();
    }
}
