using System.Diagnostics;

namespace MultiLogin.Services;

/// <summary>
/// Builds Chrome user-agent strings matching the installed browser's major version, so the
/// spoofed UA doesn't advertise a different Chrome version than the engine actually is.
/// </summary>
public static class UserAgents
{
    private static readonly string[] Platforms =
    {
        "Windows NT 10.0; Win64; x64",
        "Macintosh; Intel Mac OS X 10_15_7",
        "X11; Linux x86_64"
    };

    public static string[] PlatformNames { get; } = { "Windows", "macOS", "Linux" };

    public static int GetMajorVersion(string browserPath)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(browserPath).ProductMajorPart;
            if (v > 0) return v;
        }
        catch { }
        return 140;
    }

    public static string Build(int platformIndex, int majorVersion) =>
        $"Mozilla/5.0 ({Platforms[platformIndex]}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{majorVersion}.0.0.0 Safari/537.36";
}
