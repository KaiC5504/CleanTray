namespace CleanTray.Core.Tests;

public sealed class FileStagingTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "CleanTrayTests-" + Guid.NewGuid().ToString("N"));
    string Root => Path.Combine(_dir, "hook");
    string Source => Path.Combine(_dir, "Hook.dll");

    public FileStagingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Stage_CopiesIntoContentAddressedFolder()
    {
        File.WriteAllBytes(Source, [1, 2, 3]);

        var dest = FileStaging.Stage(Source, Root);

        Assert.Equal("Hook.dll", Path.GetFileName(dest));
        Assert.Equal(Root, Path.GetDirectoryName(Path.GetDirectoryName(dest)));
        Assert.Matches("^[0-9a-f]{16}$", Path.GetFileName(Path.GetDirectoryName(dest))!);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(dest));
    }

    [Fact]
    public void Stage_SameContentSamePath_DifferentContentNewPath()
    {
        File.WriteAllBytes(Source, [1, 2, 3]);
        var first = FileStaging.Stage(Source, Root);
        var again = FileStaging.Stage(Source, Root);
        File.WriteAllBytes(Source, [4, 5]);
        var changed = FileStaging.Stage(Source, Root);

        Assert.Equal(first, again);
        Assert.NotEqual(first, changed);
        Assert.Equal([4, 5], File.ReadAllBytes(changed));
    }

    [Fact]
    public void Stage_RewritesATamperedCopy()
    {
        File.WriteAllBytes(Source, [1, 2, 3]);
        var dest = FileStaging.Stage(Source, Root);
        File.WriteAllBytes(dest, [9, 9, 9]);

        Assert.Equal(dest, FileStaging.Stage(Source, Root));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(dest));
    }

    [Fact]
    public void Stage_RemovesOldFoldersAndLeavesNoTempFiles()
    {
        File.WriteAllBytes(Source, [1]);
        var old = FileStaging.Stage(Source, Root);
        File.WriteAllBytes(Source, [2]);

        var dest = FileStaging.Stage(Source, Root);

        Assert.False(Directory.Exists(Path.GetDirectoryName(old)));
        Assert.Equal([dest], Directory.GetFiles(Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Stage_ALockedOldFolderDoesNotFailStaging()
    {
        File.WriteAllBytes(Source, [1]);
        var old = FileStaging.Stage(Source, Root);
        File.WriteAllBytes(Source, [2]);

        string dest;
        using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.None))
            dest = FileStaging.Stage(Source, Root);

        Assert.True(File.Exists(old));
        Assert.Equal([2], File.ReadAllBytes(dest));
    }
}
