namespace CleanTray.Core;

public class IconRegistry
{
    const uint NisHidden = 0x1;

    readonly Dictionary<ulong, TrayIconInfo> _icons = new();

    public event Action? Changed;

    public IReadOnlyList<TrayIconInfo> All => _icons.Values.ToList();

    public IReadOnlyList<TrayIconInfo> PanelIcons =>
        _icons.Values
            .Where(i => i.Captured && !i.Hidden)
            .OrderBy(i => i.Id)
            .ToList();

    public void Apply(HookEvent e)
    {
        switch (e)
        {
            case IconUpsertEvent u:
                _icons[u.IconId] = new TrayIconInfo(u.IconId, u.Pid, u.AppKey, IconKey.Part(u), u.ExePath, u.Tip,
                    u.Captured, (u.State & NisHidden) != 0, u.Width, u.Height, u.Bgra);
                Changed?.Invoke();
                break;
            case IconRemovedEvent r:
                if (_icons.Remove(r.IconId)) Changed?.Invoke();
                break;
        }
    }

    /// <summary>True when the upsert is for an unknown icon or flips its captured state.</summary>
    public bool CaptureChanged(IconUpsertEvent u) =>
        !_icons.TryGetValue(u.IconId, out var known) || known.Captured != u.Captured;

    public void Clear()
    {
        if (_icons.Count == 0) return;
        _icons.Clear();
        Changed?.Invoke();
    }
}
