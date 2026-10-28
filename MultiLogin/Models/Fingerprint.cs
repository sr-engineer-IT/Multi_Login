using System.Text.RegularExpressions;

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

    /// <summary>
    /// The CPU this device claims to have, from <see cref="CpusFor"/> or typed by the user. Sites
    /// can't read the name itself; it decides the WebGL graphics (the CPU's built-in GPU) so the
    /// fingerprint works without a dedicated graphics card. Empty on profiles made before CPUs existed.
    /// </summary>
    public string CpuModel { get; set; } = "";

    public string WebGlVendor { get; set; } = "";
    public string WebGlRenderer { get; set; } = "";

    public bool CanvasNoise { get; set; } = true;
    public bool AudioNoise { get; set; } = true;
    public bool WebGlSpoof { get; set; } = true;

    /// <summary>Seed for the canvas/audio noise. 0 means "not generated yet".</summary>
    public int NoiseSeed { get; set; }

    public Fingerprint Copy() => (Fingerprint)MemberwiseClone();

    public static readonly int[] CpuChoices = { 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 28, 32 };
    public static readonly int[] MemoryChoices = { 2, 4, 8, 16, 32, 64 };

    /// <summary>
    /// Desktop Chrome caps navigator.deviceMemory at 32 GB (raised from 8 in 2026), so a real
    /// 64 GB machine reports 32.
    /// </summary>
    public const int MaxReportedMemory = 32;

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

    /// <summary>A CPU and the graphics built into it, as Chrome reports them through WEBGL_debug_renderer_info.</summary>
    public record Cpu(string Name, int Threads, string GlVendor, string GlRenderer);

    private static Cpu Win(string name, int threads, string gpu, string deviceId)
    {
        var vendor = name.StartsWith("AMD") ? "AMD" : "Intel";
        return new Cpu(name, threads, $"Google Inc. ({vendor})", $"ANGLE ({vendor}, {gpu} (0x{deviceId}) Direct3D11 vs_5_0 ps_5_0, D3D11)");
    }

    private static Cpu Mac(string name, int threads, string? intelGpu = null) => intelGpu == null
        ? new Cpu(name, threads, "Google Inc. (Apple)", $"ANGLE (Apple, ANGLE Metal Renderer: {name}, Unspecified Version)")
        : new Cpu(name, threads, "Google Inc. (Intel)", $"ANGLE (Intel, ANGLE Metal Renderer: {intelGpu}, Unspecified Version)");

    private static Cpu Lin(string name, int threads, string gpu)
    {
        var vendor = name.StartsWith("AMD") ? "AMD" : "Intel";
        return new Cpu(name, threads, $"Google Inc. ({vendor})", $"ANGLE ({vendor}, {gpu}, OpenGL 4.6)");
    }

    private const string UhdGraphics = "Intel(R) UHD Graphics";
    private const string IrisXe = "Intel(R) Iris(R) Xe Graphics";
    private const string Radeon = "AMD Radeon(TM) Graphics";

    private static readonly Cpu[] WindowsCpus =
    {
        Win("Intel Celeron N4020", 2, "Intel(R) UHD Graphics 600", "00003185"),
        Win("Intel Pentium Gold G6400", 4, "Intel(R) UHD Graphics 610", "00009BA8"),
        Win("Intel Core i3-10100", 8, "Intel(R) UHD Graphics 630", "00009BC8"),
        Win("Intel Core i3-1115G4", 4, UhdGraphics, "00009A78"),
        Win("Intel Core i3-12100", 8, "Intel(R) UHD Graphics 730", "00004692"),
        Win("Intel Core i3-1215U", 8, UhdGraphics, "000046B3"),
        Win("Intel Core i5-8250U", 8, "Intel(R) UHD Graphics 620", "00005917"),
        Win("Intel Core i5-8400", 6, "Intel(R) UHD Graphics 630", "00003E92"),
        Win("Intel Core i5-10210U", 8, UhdGraphics, "00009B41"),
        Win("Intel Core i5-10400", 12, "Intel(R) UHD Graphics 630", "00009BC8"),
        Win("Intel Core i5-1135G7", 8, IrisXe, "00009A49"),
        Win("Intel Core i5-1235U", 12, IrisXe, "000046A8"),
        Win("Intel Core i5-12400", 12, "Intel(R) UHD Graphics 730", "00004692"),
        Win("Intel Core i5-12500H", 16, IrisXe, "000046A6"),
        Win("Intel Core i5-1335U", 12, IrisXe, "0000A7A1"),
        Win("Intel Core i5-13400", 16, "Intel(R) UHD Graphics 730", "00004692"),
        Win("Intel Core i5-13500H", 16, IrisXe, "0000A7A0"),
        Win("Intel Core i5-14400", 16, "Intel(R) UHD Graphics 730", "00004692"),
        Win("Intel Core i7-8550U", 8, "Intel(R) UHD Graphics 620", "00005917"),
        Win("Intel Core i7-8700", 12, "Intel(R) UHD Graphics 630", "00003E92"),
        Win("Intel Core i7-10510U", 8, UhdGraphics, "00009B41"),
        Win("Intel Core i7-10700", 16, "Intel(R) UHD Graphics 630", "00009BC5"),
        Win("Intel Core i7-1165G7", 8, IrisXe, "00009A49"),
        Win("Intel Core i7-1255U", 12, IrisXe, "000046A8"),
        Win("Intel Core i7-12700", 20, "Intel(R) UHD Graphics 770", "00004680"),
        Win("Intel Core i7-12700H", 20, IrisXe, "000046A6"),
        Win("Intel Core i7-13700", 24, "Intel(R) UHD Graphics 770", "0000A780"),
        Win("Intel Core i7-13700H", 20, IrisXe, "0000A7A0"),
        Win("Intel Core i7-14700", 28, "Intel(R) UHD Graphics 770", "0000A780"),
        Win("Intel Core i9-12900", 24, "Intel(R) UHD Graphics 770", "00004680"),
        Win("Intel Core i9-13900", 32, "Intel(R) UHD Graphics 770", "0000A780"),
        Win("Intel Core i9-14900", 32, "Intel(R) UHD Graphics 770", "0000A780"),
        Win("Intel Core Ultra 5 125H", 18, "Intel(R) Arc(TM) Graphics", "00007D55"),
        Win("Intel Core Ultra 7 155H", 22, "Intel(R) Arc(TM) Graphics", "00007D55"),
        Win("Intel Core Ultra 7 258V", 8, "Intel(R) Arc(TM) 140V GPU (16GB)", "000064A0"),
        Win("AMD Ryzen 3 3200G", 4, "AMD Radeon(TM) Vega 8 Graphics", "000015D8"),
        Win("AMD Ryzen 5 3400G", 8, "AMD Radeon(TM) RX Vega 11 Graphics", "000015D8"),
        Win("AMD Ryzen 3 5300U", 8, Radeon, "00001636"),
        Win("AMD Ryzen 5 5500U", 12, Radeon, "00001636"),
        Win("AMD Ryzen 5 5600G", 12, Radeon, "00001638"),
        Win("AMD Ryzen 5 5625U", 12, Radeon, "00001638"),
        Win("AMD Ryzen 7 5700U", 16, Radeon, "00001636"),
        Win("AMD Ryzen 7 5700G", 16, Radeon, "00001638"),
        Win("AMD Ryzen 7 5800H", 16, Radeon, "00001638"),
        Win("AMD Ryzen 5 7520U", 8, "AMD Radeon(TM) 610M", "00001506"),
        Win("AMD Ryzen 5 7530U", 12, Radeon, "00001638"),
        Win("AMD Ryzen 5 7600", 12, Radeon, "0000164E"),
        Win("AMD Ryzen 5 7640HS", 12, "AMD Radeon(TM) 760M", "000015BF"),
        Win("AMD Ryzen 7 7700", 16, Radeon, "0000164E"),
        Win("AMD Ryzen 7 7840HS", 16, "AMD Radeon(TM) 780M", "000015BF"),
        Win("AMD Ryzen 9 7900", 24, Radeon, "0000164E"),
        Win("AMD Ryzen 9 7950X", 32, Radeon, "0000164E"),
        Win("AMD Ryzen 5 8600G", 12, "AMD Radeon(TM) 760M", "000015BF"),
        Win("AMD Ryzen 7 8700G", 16, "AMD Radeon(TM) 780M", "000015BF"),
        Win("AMD Ryzen 9 9900X", 24, Radeon, "000013C0"),
        Win("AMD Ryzen AI 9 HX 370", 24, "AMD Radeon(TM) 890M", "0000150E"),
    };

    private static readonly Cpu[] MacCpus =
    {
        Mac("Apple M1", 8), Mac("Apple M1 Pro", 10), Mac("Apple M1 Max", 10),
        Mac("Apple M2", 8), Mac("Apple M2 Pro", 12), Mac("Apple M2 Max", 12),
        Mac("Apple M3", 8), Mac("Apple M3 Pro", 12), Mac("Apple M3 Max", 16),
        Mac("Apple M4", 10), Mac("Apple M4 Pro", 14), Mac("Apple M4 Max", 16),
        Mac("Intel Core i5 (MacBook Pro 2020)", 8, "Intel(R) Iris(TM) Plus Graphics"),
        Mac("Intel Core i7 (MacBook Pro 2019)", 12, "Intel(R) UHD Graphics 630"),
    };

    private static readonly Cpu[] LinuxCpus =
    {
        Lin("Intel Core i5-8250U", 8, "Mesa Intel(R) UHD Graphics 620 (KBL GT2)"),
        Lin("Intel Core i5-10400", 12, "Mesa Intel(R) UHD Graphics 630 (CML GT2)"),
        Lin("Intel Core i5-1135G7", 8, "Mesa Intel(R) Xe Graphics (TGL GT2)"),
        Lin("Intel Core i5-12400", 12, "Mesa Intel(R) UHD Graphics 730 (ADL-S GT1)"),
        Lin("Intel Core i7-1165G7", 8, "Mesa Intel(R) Xe Graphics (TGL GT2)"),
        Lin("Intel Core i7-1255U", 12, "Mesa Intel(R) Graphics (ADL GT2)"),
        Lin("Intel Core i7-12700", 20, "Mesa Intel(R) UHD Graphics 770 (ADL-S GT1)"),
        Lin("AMD Ryzen 5 5600G", 12, "AMD Radeon Graphics (radeonsi, renoir, LLVM 15.0.7, DRM 3.54, 6.5.0-35-generic)"),
        Lin("AMD Ryzen 7 5700U", 16, "AMD Radeon Graphics (radeonsi, renoir, LLVM 15.0.7, DRM 3.54, 6.5.0-35-generic)"),
        Lin("AMD Ryzen 7 7700", 16, "AMD Radeon Graphics (radeonsi, raphael_mendocino, LLVM 15.0.7, DRM 3.54, 6.5.0-35-generic)"),
        Lin("AMD Ryzen 7 7840U", 16, "AMD Radeon 780M (radeonsi, gfx1103_r1, LLVM 15.0.7, DRM 3.54, 6.5.0-35-generic)"),
    };

    public static IReadOnlyList<Cpu> CpusFor(string platform) => platform switch
    {
        MacOS => MacCpus,
        Linux => LinuxCpus,
        _ => WindowsCpus
    };

    /// <summary>
    /// Finds the CPU by name, or for a name typed by the user, a listed CPU of the same brand
    /// whose graphics stand in for it. Returns null for an empty name.
    /// </summary>
    public static Cpu? FindCpu(string platform, string name, out bool known)
    {
        name = name.Trim();
        var list = CpusFor(platform);
        var match = list.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        known = match != null;
        if (match != null || name.Length == 0) return match;

        var n = name.ToLowerInvariant();
        var brand = n.Contains("apple") || Regex.IsMatch(n, @"\bm[1-9]\b") ? "Apple"
            : n.Contains("amd") || n.Contains("ryzen") || n.Contains("athlon") ? "AMD"
            : "Intel";
        // Apple chip names map one-to-one onto Metal renderer strings, so a typed "Apple M2 Ultra" is exact.
        if (brand == "Apple" && platform == MacOS)
            return Mac(name.StartsWith("Apple", StringComparison.OrdinalIgnoreCase) ? name : "Apple " + name, 8) with { Name = name };
        var standIn = list.FirstOrDefault(c => c.Name.StartsWith(brand, StringComparison.Ordinal)) ?? list[0];
        return standIn with { Name = name };
    }

    /// <summary>Sets the CPU name and the graphics that go with it; a listed CPU also sets the thread count.</summary>
    public void ApplyCpu(string name)
    {
        CpuModel = name.Trim();
        var cpu = FindCpu(Platform, CpuModel, out var known);
        if (cpu == null) return;
        (WebGlVendor, WebGlRenderer) = (cpu.GlVendor, cpu.GlRenderer);
        if (known) HardwareConcurrency = cpu.Threads;
    }

    /// <summary>Creates a random but internally consistent fingerprint for the given platform.</summary>
    public static Fingerprint Generate(string platform)
    {
        var r = Random.Shared;
        var fp = new Fingerprint { Platform = platform };

        fp.ApplyCpu(Pick(r, CpusFor(platform)).Name);
        // Budget CPUs ship in 4–8 GB machines; everything else in 8–32 GB ones.
        fp.DeviceMemory = fp.HardwareConcurrency <= 4 ? Pick(r, 4, 4, 8) : Pick(r, 8, 16, 16, 16, 32);
        (fp.ScreenWidth, fp.ScreenHeight) = Pick(r, ScreensFor(platform));
        fp.NoiseSeed = r.Next(1, int.MaxValue);
        return fp;
    }

    private static T Pick<T>(Random r, params IReadOnlyList<T> items) => items[r.Next(items.Count)];
}
