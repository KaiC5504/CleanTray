namespace CleanTray.Core;

/// <summary>
/// The rule a Settings row edits. A draft joins the settings only when a value really changes,
/// and that change lands on the rule the settings hold, which may not be the draft.
/// </summary>
public sealed class RuleEditor
{
    readonly Func<AppRule, AppRule> _adopt;

    /// <param name="adopt">Puts a draft into the settings, returning the rule to edit.</param>
    public RuleEditor(AppRule rule, bool isDraft, Func<AppRule, AppRule> adopt)
    {
        Rule = rule;
        IsDraft = isDraft;
        _adopt = adopt;
    }

    public AppRule Rule { get; private set; }
    public bool IsDraft { get; private set; }

    /// <summary>Writes the value unless the rule already holds it; true if it wrote.</summary>
    public bool Set<T>(T value, Func<AppRule, T> read, Action<AppRule, T> write)
    {
        if (EqualityComparer<T>.Default.Equals(read(Rule), value)) return false;
        if (IsDraft) Bind(_adopt(Rule));
        write(Rule, value);
        return true;
    }

    /// <summary>Switches to the settings' own rule, e.g. once a known app has been seen.</summary>
    public void Bind(AppRule rule)
    {
        Rule = rule;
        IsDraft = false;
    }
}
