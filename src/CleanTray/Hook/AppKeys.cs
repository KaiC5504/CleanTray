namespace CleanTray.Hook;

/// <summary>App keys computed by the hook DLL itself, so a key made here always matches the one it sends.</summary>
static unsafe class AppKeys
{
    const string Export = "CleanTrayNormalizeAppKey";

    static readonly object Gate = new();
    static delegate* unmanaged[Stdcall]<char*, char*, char*, int, int> _normalize;

    /// <summary>Null when the DLL is unavailable or rejects the path.</summary>
    public static string? Normalize(string exePath, string commandLine = "")
    {
        var normalize = Function();
        if (normalize == null) return null;
        fixed (char* path = exePath)
        fixed (char* cl = commandLine)
        {
            var size = 260;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = new char[size];
                fixed (char* output = buffer)
                {
                    var n = normalize(path, cl, output, size);
                    if (n < 0) return null;
                    if (n < size) return new string(output, 0, n);
                    size = n;
                }
            }
            return null;
        }
    }

    static delegate* unmanaged[Stdcall]<char*, char*, char*, int, int> Function()
    {
        lock (Gate)
        {
            if (_normalize == null)
                _normalize = (delegate* unmanaged[Stdcall]<char*, char*, char*, int, int>)HookModule.Export(Export);
            return _normalize;
        }
    }
}
