namespace CleanTray.Core;

/// <summary>
/// Maps the settings combo boxes' SelectedIndex to rule modes. WPF can push -1 while a list is
/// rebuilt, which must never be read as a choice.
/// </summary>
public static class ModeChoice
{
    public static bool TryApp(int index, out RuleMode mode)
    {
        mode = index == 1 ? RuleMode.Captured : RuleMode.Show;
        return index is 0 or 1;
    }

    /// <summary>Index 0 is "Same as app", which is a null mode.</summary>
    public static bool TryIcon(int index, out RuleMode? mode)
    {
        mode = index switch
        {
            1 => RuleMode.Show,
            2 => RuleMode.Captured,
            _ => null,
        };
        return index is >= 0 and <= 2;
    }

    public static int AppIndex(RuleMode mode) => mode == RuleMode.Captured ? 1 : 0;

    public static int IconIndex(RuleMode? mode) => mode is { } m ? AppIndex(m) + 1 : 0;
}
