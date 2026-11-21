using System.Globalization;

namespace CleanTray.Core.Tests;

static class Fixtures
{
    public static byte[] Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        var bytes = new List<byte>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var hash = raw.IndexOf('#');
            var line = hash >= 0 ? raw[..hash] : raw;
            foreach (var tok in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                bytes.Add(byte.Parse(tok, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        return bytes.ToArray();
    }
}
