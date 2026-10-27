using System.Text.Json.Serialization;
using MultiLogin.Services;

namespace MultiLogin.Models;

public enum ProxyType
{
    None,
    Http,
    Socks5
}

public class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New profile";
    public string Tags { get; set; } = "";
    public string Notes { get; set; } = "";

    /// <summary>One URL per line, opened when the profile launches.</summary>
    public string StartUrls { get; set; } = "";

    public ProxyType ProxyType { get; set; } = ProxyType.None;
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; }
    public string ProxyUser { get; set; } = "";

    /// <summary>Proxy password encrypted with Windows DPAPI (current user), base64.</summary>
    public string ProxyPasswordProtected { get; set; } = "";

    /// <summary>Empty means the browser's own user agent.</summary>
    public string UserAgent { get; set; } = "";

    /// <summary>Browser UI / Accept-Language locale, e.g. en-US. Empty means system default.</summary>
    public string Language { get; set; } = "";

    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }

    /// <summary>Stops WebRTC from exposing the real IP when a proxy is used.</summary>
    public bool BlockWebRtcLeak { get; set; } = true;

    /// <summary>Extra Chromium command-line switches, one per line.</summary>
    public string ExtraArgs { get; set; } = "";

    public Fingerprint Fingerprint { get; set; } = Fingerprint.Generate(Fingerprint.Windows);

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLaunchedUtc { get; set; }

    [JsonIgnore]
    public string ProxyPassword
    {
        get => Secret.Unprotect(ProxyPasswordProtected);
        set => ProxyPasswordProtected = Secret.Protect(value);
    }

    [JsonIgnore]
    public bool HasProxy => ProxyType != ProxyType.None && !string.IsNullOrWhiteSpace(ProxyHost) && ProxyPort > 0;

    [JsonIgnore]
    public string ProxyDisplay => HasProxy
        ? $"{ProxyType.ToString().ToLowerInvariant()}://{ProxyHost}:{ProxyPort}"
        : "—";

    public Profile Clone()
    {
        var copy = (Profile)MemberwiseClone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = Name + " (copy)";
        copy.CreatedUtc = DateTime.UtcNow;
        copy.LastLaunchedUtc = null;
        // Same settings, but a fresh device identity so the two profiles can't be linked.
        var fp = Fingerprint.Generate(Fingerprint.Platform);
        fp.Enabled = Fingerprint.Enabled;
        fp.Timezone = Fingerprint.Timezone;
        fp.GeolocationFromIp = Fingerprint.GeolocationFromIp;
        fp.CanvasNoise = Fingerprint.CanvasNoise;
        fp.AudioNoise = Fingerprint.AudioNoise;
        fp.WebGlSpoof = Fingerprint.WebGlSpoof;
        copy.Fingerprint = fp;
        return copy;
    }
}
