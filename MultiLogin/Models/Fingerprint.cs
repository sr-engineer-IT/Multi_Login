namespace MultiLogin.Models;

/// <summary>
/// The device a profile pretends to be. Generated once per profile and kept stable across
/// launches, since a fingerprint that changes every session is itself suspicious.
/// </summary>
public class Fingerprint
{
    public const string Windows = "Windows";
    public const string MacOS = "macOS";
    public const string Linux = "Linux";
    public static readonly string[] Platforms = { Windows, MacOS, Linux };

    /// <summary>When off, the browser runs with no overrides except proxy, UA and language.</summary>
    public bool Enabled { get; set; } = true;

    public string Platform { get; set; } = Windows;

    /// <summary>IANA timezone, e.g. Europe/Berlin. Empty = take it from the proxy's IP.</summary>
    public string Timezone { get; set; } = "";

    /// <summary>Report the proxy IP's location to the Geolocation API.</summary>
    public bool GeolocationFromIp { get; set; } = true;

    public int HardwareConcurrency { get; set; }
    public int DeviceMemory { get; set; }
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }

    public string WebGlVendor { get; set; } = "";
    public string WebGlRenderer { get; set; } = "";

    public bool CanvasNoise { get; set; } = true;
    public bool AudioNoise { get; set; } = true;
    public bool WebGlSpoof { get; set; } = true;

    /// <summary>Seed for the canvas/audio noise. 0 means "not generated yet".</summary>
    public int NoiseSeed { get; set; }

    public Fingerprint Copy() => (Fingerprint)MemberwiseClone();

    public static readonly int[] CpuChoices = { 2, 4, 6, 8, 12, 16, 20, 24 };
    public static readonly int[] MemoryChoices = { 2, 4, 8 };

    private static readonly (int W, int H)[] WindowsScreens =
        { (1920, 1080), (1920, 1080), (1920, 1080), (1366, 768), (1536, 864), (2560, 1440), (1600, 900), (1440, 900), (1680, 1050), (1920, 1200) };
    private static readonly (int W, int H)[] MacScreens =
        { (1440, 900), (1512, 982), (1728, 1117), (1680, 1050), (1920, 1080), (2560, 1440) };
    private static readonly (int W, int H)[] LinuxScreens =
        { (1920, 1080), (1920, 1080), (1366, 768), (2560, 1440), (1600, 900), (1920, 1200) };

    public static IReadOnlyList<(int W, int H)> ScreensFor(string platform) => platform switch
    {
        MacOS => MacScreens,
        Linux => LinuxScreens,
        _ => WindowsScreens
    };

    // Strings in the exact format Chrome reports through WEBGL_debug_renderer_info.
    private static readonly (string Vendor, string Renderer)[] WindowsGpus =
    {
        ("Google Inc. (NVIDIA)", "ANGLE (NVIDIA, NVIDIA GeForce GTX 1650 (0x00001F82) Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        ("Google Inc. (NVIDIA)", "ANGLE (NVIDIA, NVIDIA GeForce GTX 1660 SUPER (0x000021C4) Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        ("Google Inc. (NVIDIA)", "ANGLE (NVIDIA, NVIDIA GeForce RTX 3060 (0x00002504) Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        ("Google Inc. (NVIDIA)", "ANGLE (NVIDIA, NVIDIA GeForce RTX 4060 (0x00002882) Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        ("Google Inc. (Intel)", "ANGLE (Intel, Intel(R) UHD Graphics 620 (0x00005917) Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        ("Google Inc. (Intel)", "ANGLE (Intel, Intel(R) Iris(R) Xe Graphics (0x00009A49) Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        ("Google Inc. (Intel)", "ANGLE (Intel, Intel(R) UHD Graphics 770 (0x00004680) Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        ("Google Inc. (AMD)", "ANGLE (AMD, AMD Radeon RX 6600 (0x000073FF) Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        ("Google Inc. (AMD)", "ANGLE (AMD, AMD Radeon(TM) Graphics (0x00001638) Direct3D11 vs_5_0 ps_5_0, D3D11)"),
    };
    private static readonly (string Vendor, string Renderer)[] MacGpus =
    {
        ("Google Inc. (Apple)", "ANGLE (Apple, ANGLE Metal Renderer: Apple M1, Unspecified Version)"),
        ("Google Inc. (Apple)", "ANGLE (Apple, ANGLE Metal Renderer: Apple M2, Unspecified Version)"),
        ("Google Inc. (Apple)", "ANGLE (Apple, ANGLE Metal Renderer: Apple M3, Unspecified Version)"),
        ("Google Inc. (Apple)", "ANGLE (Apple, ANGLE Metal Renderer: Apple M1 Pro, Unspecified Version)"),
    };
    private static readonly (string Vendor, string Renderer)[] LinuxGpus =
    {
        ("Google Inc. (Intel)", "ANGLE (Intel, Mesa Intel(R) UHD Graphics 620 (KBL GT2), OpenGL 4.6)"),
        ("Google Inc. (Intel)", "ANGLE (Intel, Mesa Intel(R) Xe Graphics (TGL GT2), OpenGL 4.6)"),
        ("Google Inc. (AMD)", "ANGLE (AMD, AMD Radeon Graphics (radeonsi, renoir, LLVM 15.0.7, DRM 3.54, 6.5.0-35-generic), OpenGL 4.6)"),
        ("Google Inc. (NVIDIA Corporation)", "ANGLE (NVIDIA Corporation, NVIDIA GeForce GTX 1660/PCIe/SSE2, OpenGL 4.5.0)"),
    };

    public static IReadOnlyList<(string Vendor, string Renderer)> GpusFor(string platform) => platform switch
    {
        MacOS => MacGpus,
        Linux => LinuxGpus,
        _ => WindowsGpus
    };

    /// <summary>Creates a random but internally consistent fingerprint for the given platform.</summary>
    public static Fingerprint Generate(string platform)
    {
        var r = Random.Shared;
        var fp = new Fingerprint { Platform = platform };

        fp.HardwareConcurrency = platform == MacOS ? Pick(r, 8, 8, 10, 12) : Pick(r, 4, 8, 8, 12, 16);
        fp.DeviceMemory = Pick(r, 8, 8, 8, 4);
        (fp.ScreenWidth, fp.ScreenHeight) = Pick(r, ScreensFor(platform));
        (fp.WebGlVendor, fp.WebGlRenderer) = Pick(r, GpusFor(platform));
        fp.NoiseSeed = r.Next(1, int.MaxValue);
        return fp;
    }

    private static T Pick<T>(Random r, params IReadOnlyList<T> items) => items[r.Next(items.Count)];
}
