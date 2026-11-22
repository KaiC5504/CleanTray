using System.Buffers.Binary;
using CleanTray.Core;

namespace CleanTray.Core.Tests;

public sealed class OverflowOrderTests
{
    static byte[] List(params ulong[] ids)
    {
        var bytes = new byte[ids.Length * 8];
        for (var i = 0; i < ids.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8), ids[i]);
        return bytes;
    }

    [Fact]
    public void MoveToFront_KeepsTheRestInOrder() =>
        Assert.Equal(List(3, 1, 2, 4), OverflowOrder.MoveToFront(List(1, 2, 3, 4), 3));

    [Fact]
    public void MoveToFront_AlreadyFirstIsLeftAlone() =>
        Assert.Null(OverflowOrder.MoveToFront(List(7, 1, 2), 7));

    [Fact]
    public void MoveToFront_AddsAMissingId() =>
        Assert.Equal(List(9, 1, 2), OverflowOrder.MoveToFront(List(1, 2), 9));

    [Fact]
    public void MoveToFront_DropsDuplicatesOfTheId() =>
        Assert.Equal(List(5, 1, 2), OverflowOrder.MoveToFront(List(1, 5, 2, 5), 5));

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(12)]
    public void MoveToFront_LeavesDataThatIsNotAListOfIds(int length) =>
        Assert.Null(OverflowOrder.MoveToFront(new byte[length], 1));

    [Fact]
    public void FindIcon_MatchesPathAndUidIgnoringCase()
    {
        NotifyIconEntry[] entries =
        [
            new(1, @"d:\tools\cleantray\cleantray.exe", 1, HasGuid: true),
            new(2, @"d:\tools\cleantray\cleantray.exe", 2, HasGuid: false),
            new(3, @"D:\Tools\CleanTray\CleanTray.exe", 1, HasGuid: false),
            new(4, @"d:\other\cleantray.exe", 1, HasGuid: false),
        ];
        Assert.Equal(3UL, OverflowOrder.FindIcon(entries, @"d:\tools\cleantray\CleanTray.exe", 1));
    }

    [Fact]
    public void FindIcon_NullWhenWindowsHasNoEntryYet() =>
        Assert.Null(OverflowOrder.FindIcon([new(1, @"c:\x.exe", 1, false), new(2, null, 1, false)], @"c:\y.exe", 1));
}
