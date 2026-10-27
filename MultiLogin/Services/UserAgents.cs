using System.Diagnostics;
using MultiLogin.Models;

namespace MultiLogin.Services;

/// <summary>
/// Builds Chrome user-agent strings matching the installed browser's major version, so the
/// spoofed UA doesn't advertise a different Chrome version than the engine actually is.
/// </summary>
public static class UserAgents
{
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

    public static bool IsEdge(string browserPath) =>
        Path.GetFileName(browserPath).Equals("msedge.exe", StringComparison.OrdinalIgnoreCase);

    public static string Build(string platform, int majorVersion, bool edge = false)
    {
        var os = platform switch
        {
            Fingerprint.MacOS => "Macintosh; Intel Mac OS X 10_15_7",
            Fingerprint.Linux => "X11; Linux x86_64",
            _ => "Windows NT 10.0; Win64; x64"
        };
        var ua = $"Mozilla/5.0 ({os}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{majorVersion}.0.0.0 Safari/537.36";
        return edge ? $"{ua} Edg/{majorVersion}.0.0.0" : ua;
    }

    public static string? DetectPlatform(string userAgent) =>
        userAgent.Contains("Windows") ? Fingerprint.Windows
        : userAgent.Contains("Mac OS X") ? Fingerprint.MacOS
        : userAgent.Contains("Linux") ? Fingerprint.Linux
        : null;
}
