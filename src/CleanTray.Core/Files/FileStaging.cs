using System.Security.Cryptography;

namespace CleanTray.Core;

public static class FileStaging
{
    /// <summary>
    /// Copies <paramref name="source"/> to root\&lt;content hash&gt;\&lt;file name&gt; and returns that path.
    /// Other folders under root are removed where nothing holds them open. Throws on IO failure.
    /// </summary>
    public static string Stage(string source, string root)
    {
        // Hash and write the same bytes, so a file swapped mid-call can't land under the wrong hash.
        var bytes = File.ReadAllBytes(source);
        var hash = Hash(bytes);
        var dir = Path.Combine(root, hash);
        var dest = Path.Combine(dir, Path.GetFileName(source));

        if (!File.Exists(dest) || Hash(File.ReadAllBytes(dest)) != hash)
        {
            Directory.CreateDirectory(dir);
            var tmp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, dest, overwrite: true);
            }
            finally
            {
                TryDelete(tmp);
            }
        }

        RemoveOthers(root, dir);
        return dest;
    }

    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();

    static void RemoveOthers(string root, string keep)
    {
        try
        {
            foreach (var old in Directory.EnumerateDirectories(root))
            {
                if (string.Equals(old, keep, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    Directory.Delete(old, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Still loaded somewhere, e.g. pinned in Explorer until it restarts.
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
