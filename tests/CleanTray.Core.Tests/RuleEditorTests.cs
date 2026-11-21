namespace CleanTray.Core.Tests;

public sealed class RuleEditorTests
{
    static AppRule Rule(RuleMode mode = RuleMode.Show) => new() { Key = "k", Mode = mode };

    [Fact]
    public void Set_UnchangedValueNeverAdoptsTheDraft()
    {
        var adopted = 0;
        var editor = new RuleEditor(Rule(), true, r => { adopted++; return r; });

        Assert.False(editor.Set(RuleMode.Show, r => r.Mode, (r, v) => r.Mode = v));

        Assert.Equal(0, adopted);
        Assert.True(editor.IsDraft);
    }

    [Fact]
    public void Set_ChangedValueAdoptsOnceThenWritesToTheAdoptedRule()
    {
        var draft = Rule();
        var settingsRule = Rule(RuleMode.Show);
        var adopted = 0;
        var editor = new RuleEditor(draft, true, _ => { adopted++; return settingsRule; });

        Assert.True(editor.Set(RuleMode.Captured, r => r.Mode, (r, v) => r.Mode = v));
        Assert.True(editor.Set(false, r => r.Popups, (r, v) => r.Popups = v));

        Assert.Equal(1, adopted);
        Assert.False(editor.IsDraft);
        Assert.Same(settingsRule, editor.Rule);
        Assert.Equal(RuleMode.Captured, settingsRule.Mode);
        Assert.False(settingsRule.Popups);
        Assert.Equal(RuleMode.Show, draft.Mode);
    }

    [Fact]
    public void Set_NonDraftNeverAdopts()
    {
        var rule = Rule();
        var editor = new RuleEditor(rule, false, _ => throw new InvalidOperationException());

        Assert.True(editor.Set(RuleMode.Captured, r => r.Mode, (r, v) => r.Mode = v));

        Assert.Equal(RuleMode.Captured, rule.Mode);
    }

    [Fact]
    public void Bind_SwitchesToTheSettingsRuleAndEndsTheDraft()
    {
        var editor = new RuleEditor(Rule(), true, r => r);
        var settingsRule = Rule();

        editor.Bind(settingsRule);

        Assert.Same(settingsRule, editor.Rule);
        Assert.False(editor.IsDraft);
        Assert.False(editor.Set(RuleMode.Show, r => r.Mode, (r, v) => r.Mode = v));
    }
}
