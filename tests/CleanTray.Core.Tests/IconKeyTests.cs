namespace CleanTray.Core.Tests;

public class IconKeyTests
{
    static IconUpsertEvent Event(uint uid, bool hasGuid = false, Guid guid = default) =>
        new(1, 1000, 0x1234, uid, hasGuid, guid, 0x8001, 4, 0, true,
            "c:\\apps\\a.exe", "C:\\Apps\\a.exe", "tip", 0, 0, Array.Empty<byte>());

    [Fact]
    public void IconKey_PartWithoutGuidIsTheUid()
    {
        Assert.Equal("uid:7", IconKey.Part(Event(7)));
    }

    [Fact]
    public void IconKey_PartWithGuidIsBracedLowercase()
    {
        var g = new Guid("3B3905D1-AFD0-CA7E-3CA2-C9DA5791FD04");
        Assert.Equal("{3b3905d1-afd0-ca7e-3ca2-c9da5791fd04}", IconKey.Part(Event(7, hasGuid: true, guid: g)));
    }

    [Fact]
    public void IconKey_OfJoinsAppKeyAndPartLowercased()
    {
        Assert.Equal("c:\\apps\\a.exe#{abc}", IconKey.Of("C:\\Apps\\A.exe", "{ABC}"));
    }
}
