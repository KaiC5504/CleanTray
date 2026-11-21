namespace CleanTray.Core.Tests;

public class CommandWriterTests
{
    [Fact]
    public void CommandWriter_ClickMatchesFixture() =>
        Assert.Equal(Fixtures.Load("click_v1.hex"), CommandWriter.Click(42, ClickKind.Right, 100, 200));

    [Fact]
    public void CommandWriter_RulesChangedIsHeaderOnly() =>
        Assert.Equal(new byte[] { 0, 0, 0, 0, 101, 0 }, CommandWriter.RulesChanged());

    [Fact]
    public void CommandWriter_ReleaseAllAndIdleIsHeaderOnly() =>
        Assert.Equal(new byte[] { 0, 0, 0, 0, 105, 0 }, CommandWriter.ReleaseAllAndIdle());

    [Fact]
    public void CommandWriter_PopupResultLayout() =>
        Assert.Equal(
            new byte[] { 9, 0, 0, 0, 103, 0, 42, 0, 0, 0, 0, 0, 0, 0, 2 },
            CommandWriter.PopupResult(42, PopupOutcome.TimedOut));

    [Fact]
    public void CommandWriter_SetAnchorLayout() =>
        Assert.Equal(
            new byte[] { 16, 0, 0, 0, 104, 0, 1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF },
            CommandWriter.SetAnchor(1, 2, 3, -1));
}
