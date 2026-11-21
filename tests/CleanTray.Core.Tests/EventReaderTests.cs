namespace CleanTray.Core.Tests;

public class EventReaderTests
{
    [Fact]
    public void EventReader_ParsesIconUpsertFixture()
    {
        var buf = Fixtures.Load("icon_upsert_v1.hex");
        Assert.True(EventReader.TryRead(buf, out var ev, out var consumed));
        Assert.Equal(buf.Length, consumed);
        var e = Assert.IsType<IconUpsertEvent>(ev);
        Assert.Equal(42UL, e.IconId);
        Assert.Equal(1000u, e.Pid);
        Assert.Equal(0x1234u, e.Hwnd);
        Assert.Equal(7u, e.Uid);
        Assert.False(e.HasGuid);
        Assert.Equal(0x8001u, e.CallbackMsg);
        Assert.Equal(4u, e.Version);
        Assert.Equal(0u, e.State);
        Assert.True(e.Captured);
        Assert.Equal("c:\\a\\b.exe", e.AppKey);
        Assert.Equal("C:\\A\\b.exe", e.ExePath);
        Assert.Equal("Tip", e.Tip);
        Assert.Equal(1, e.Width);
        Assert.Equal(1, e.Height);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, e.Bgra);
    }

    [Fact]
    public void EventReader_ParsesGuidFixture()
    {
        var buf = Fixtures.Load("icon_upsert_guid_v1.hex");
        Assert.True(EventReader.TryRead(buf, out var ev, out _));
        var e = Assert.IsType<IconUpsertEvent>(ev);
        Assert.True(e.HasGuid);
        Assert.Equal(new Guid("3b3905d1-afd0-ca7e-3ca2-c9da5791fd04"), e.Guid);
    }

    [Fact]
    public void EventReader_IncompleteReturnsFalse()
    {
        var buf = Fixtures.Load("icon_upsert_v1.hex");
        Assert.False(EventReader.TryRead(buf.AsSpan(0, buf.Length - 1), out _, out var consumed));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void EventReader_ShortHeaderReturnsFalse()
    {
        Assert.False(EventReader.TryRead(new byte[] { 1, 0, 0 }, out _, out var consumed));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void EventReader_UnknownTypeIsConsumedAsNull()
    {
        var buf = new byte[] { 2, 0, 0, 0, 0x63, 0x00, 9, 9, 0xAA };
        Assert.True(EventReader.TryRead(buf, out var ev, out var consumed));
        Assert.Null(ev);
        Assert.Equal(8, consumed);
    }

    [Fact]
    public void EventReader_ParsesHelloIconRemovedAndPopup()
    {
        var hello = new byte[] { 8, 0, 0, 0, 1, 0, 0xE8, 3, 0, 0, 1, 0, 0, 0 };
        Assert.True(EventReader.TryRead(hello, out var h, out var hc));
        Assert.Equal(new HelloEvent(1000, 1), h);
        Assert.Equal(hello.Length, hc);

        var removed = new byte[] { 8, 0, 0, 0, 3, 0, 42, 0, 0, 0, 0, 0, 0, 0 };
        Assert.True(EventReader.TryRead(removed, out var r, out _));
        Assert.Equal(new IconRemovedEvent(42), r);

        var popup = new byte[]
        {
            8 + 4 + 2 + 2 + 2 + 4, 0, 0, 0, 4, 0,
            42, 0, 0, 0, 0, 0, 0, 0,
            2, 0, 0, 0,
            1, 0, (byte)'T', 0,
            2, 0, (byte)'h', 0, (byte)'i', 0,
        };
        Assert.True(EventReader.TryRead(popup, out var p, out var pc));
        Assert.Equal(new PopupEvent(42, 2, "T", "hi"), p);
        Assert.Equal(popup.Length, pc);
    }

    [Fact]
    public void EventReader_MalformedPayloadIsConsumedAsNull()
    {
        // Claims a 100-unit string but the frame holds only 2 payload bytes.
        var buf = new byte[] { 2, 0, 0, 0, 4, 0, 100, 0 };
        Assert.True(EventReader.TryRead(buf, out var ev, out var consumed));
        Assert.Null(ev);
        Assert.Equal(8, consumed);
    }

    [Fact]
    public void EventReader_OversizeFrameReportsCorrupt()
    {
        var buf = new byte[] { 0x01, 0x00, 0x80, 0x00, 2, 0 };
        Assert.False(EventReader.TryRead(buf, out var ev, out var consumed, out var corrupt));
        Assert.Null(ev);
        Assert.Equal(0, consumed);
        Assert.True(corrupt);
    }

    [Fact]
    public void EventReader_NormalFramesAreNotCorrupt()
    {
        var buf = Fixtures.Load("icon_upsert_v1.hex");
        Assert.True(EventReader.TryRead(buf, out _, out _, out var corrupt));
        Assert.False(corrupt);
        Assert.False(EventReader.TryRead(buf.AsSpan(0, 3), out _, out _, out corrupt));
        Assert.False(corrupt);
        Assert.False(EventReader.TryRead(buf.AsSpan(0, buf.Length - 1), out _, out var consumed, out corrupt));
        Assert.Equal(0, consumed);
        Assert.False(corrupt);
    }
}
