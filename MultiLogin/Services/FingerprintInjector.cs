using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiLogin.Models;

namespace MultiLogin.Services;

/// <summary>Values resolved at launch time that the fingerprint overrides need.</summary>
public record LaunchContext(
    string UserAgent,
    string Platform,
    string Language,
    string? Timezone,
    double? Latitude,
    double? Longitude,
    bool IsEdge,
    int WindowWidth,
    int WindowHeight);

/// <summary>
/// Connects to the browser over DevTools and applies the profile's fingerprint to every
/// tab, frame and worker before its first line of JavaScript runs. Overrides only last
/// while this connection is open, so it lives as long as the browser does.
/// </summary>
public sealed class FingerprintInjector : IAsyncDisposable
{
    // At browser level Chrome attaches pages through their "tab" target, so pages are excluded
    // there and picked up by the nested auto-attach on each tab (which uses Chrome's default filter).
    private static readonly object[] BrowserAutoAttachFilter =
    {
        new { type = "browser", exclude = true },
        new { type = "page", exclude = true },
        new { }
    };

    private readonly CdpConnection _cdp;
    private readonly Fingerprint _fp;
    private readonly LaunchContext _ctx;
    private readonly string _script;
    private object? _uaMetadata;

    private FingerprintInjector(CdpConnection cdp, Fingerprint fp, LaunchContext ctx)
    {
        _cdp = cdp;
        _fp = fp;
        _ctx = ctx;
        _script = BuildScript(fp, ctx);
    }

    public static async Task<FingerprintInjector> AttachAsync(Uri endpoint, Fingerprint fp, LaunchContext ctx, CancellationToken ct)
    {
        var cdp = await CdpConnection.ConnectAsync(endpoint, ct);
        var injector = new FingerprintInjector(cdp, fp, ctx);
        try
        {
            injector._uaMetadata = await injector.BuildUserAgentMetadataAsync(ct);
            // Subscribe only now, so the temporary session used above isn't treated as a tab.
            cdp.EventReceived += injector.OnEvent;
            await cdp.SendAsync("Target.setAutoAttach", new
            {
                autoAttach = true,
                waitForDebuggerOnStart = true,
                flatten = true,
                filter = BrowserAutoAttachFilter
            });
        }
        catch
        {
            await injector.DisposeAsync();
            throw;
        }
        return injector;
    }

    /// <summary>
    /// Opens the start URLs as new tabs (each is attached and spoofed before it loads), then
    /// closes the startup new-tab page. New tabs are used rather than navigating the existing
    /// one because Chrome also keeps hidden, preloaded new-tab pages that look identical.
    /// </summary>
    public async Task OpenStartPagesAsync(IReadOnlyList<string> urls)
    {
        if (urls.Count == 0) return;

        var targets = await _cdp.SendAsync("Target.getTargets");
        var startupTabs = targets.GetProperty("targetInfos").EnumerateArray()
            .Where(t => t.GetProperty("type").GetString() == "page")
            .Where(t => t.GetProperty("url").GetString() is { } u
                        && (u.StartsWith("chrome://new", StringComparison.Ordinal) || u == "about:blank"))
            .Select(t => t.GetProperty("targetId").GetString())
            .ToList();

        foreach (var url in urls)
            await _cdp.TrySendAsync("Target.createTarget", new { url });
        foreach (var targetId in startupTabs)
            await _cdp.TrySendAsync("Target.closeTarget", new { targetId });
    }

    /// <summary>Closes the browser gracefully so cookies and session state are flushed.</summary>
    public Task CloseBrowserAsync() => _cdp.TrySendAsync("Browser.close");

    private void OnEvent(string method, JsonElement p, string? parentSession)
    {
        if (method != "Target.attachedToTarget") return;
        var session = p.GetProperty("sessionId").GetString()!;
        var info = p.GetProperty("targetInfo");
        var type = info.GetProperty("type").GetString() ?? "";
        var url = info.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        var waiting = p.TryGetProperty("waitingForDebugger", out var w) && w.GetBoolean();
        _ = SetupTargetAsync(session, type, url, waiting);
    }

    private static bool IsInternal(string url) =>
        url.StartsWith("chrome-extension://", StringComparison.Ordinal)
        || url.StartsWith("chrome://", StringComparison.Ordinal)
        || url.StartsWith("devtools://", StringComparison.Ordinal);

    private async Task SetupTargetAsync(string session, string type, string url, bool waiting)
    {
        try
        {
            switch (type)
            {
                // Tabs can navigate anywhere later, so pages are always spoofed, even on chrome:// URLs.
                case "page":
                case "iframe":
                    await ApplyCommonOverridesAsync(session);
                    await _cdp.TrySendAsync("Page.enable", null, session);
                    await _cdp.TrySendAsync("Page.addScriptToEvaluateOnNewDocument",
                        new { source = _script, runImmediately = true }, session);
                    break;

                case "worker":
                case "shared_worker":
                case "service_worker" when !IsInternal(url):
                    await ApplyCommonOverridesAsync(session);
                    // Workers have no "on new document" hook; while paused before their first
                    // statement, evaluating the script here has the same effect.
                    await _cdp.TrySendAsync("Runtime.evaluate", new { expression = _script }, session);
                    break;

                // background_page, browser_ui, extension workers, ...: Chrome's own UI, leave alone.
            }

            // Recurse so out-of-process iframes and dedicated workers inside this target are caught too.
            // Tabs on chrome:// (e.g. the new-tab page) are real tabs; only extension background tabs are skipped.
            if (type is "page" or "iframe" or "worker"
                || (type == "tab" && !url.StartsWith("chrome-extension://", StringComparison.Ordinal)))
                await _cdp.TrySendAsync("Target.setAutoAttach", new
                {
                    autoAttach = true,
                    waitForDebuggerOnStart = true,
                    flatten = true
                }, session);
        }
        finally
        {
            if (waiting)
                await _cdp.TrySendAsync("Runtime.runIfWaitingForDebugger", null, session);
        }

        // A tab that was already showing a site before we attached (e.g. a restored
        // session) loaded without overrides; reload it so they apply.
        if (type == "page" && !waiting && url.StartsWith("http", StringComparison.Ordinal))
            await _cdp.TrySendAsync("Page.reload", null, session);
    }

    private async Task ApplyCommonOverridesAsync(string session)
    {
        await _cdp.TrySendAsync("Network.setUserAgentOverride", new
        {
            userAgent = _ctx.UserAgent,
            acceptLanguage = AcceptLanguage(_ctx.Language),
            platform = NavigatorPlatform(_ctx.Platform),
            userAgentMetadata = _uaMetadata
        }, session);

        if (_ctx.Timezone != null)
            await _cdp.TrySendAsync("Emulation.setTimezoneOverride", new { timezoneId = _ctx.Timezone }, session);
        if (_ctx.Language.Length > 0)
            await _cdp.TrySendAsync("Emulation.setLocaleOverride", new { locale = _ctx.Language }, session);
        if (_fp.HardwareConcurrency > 0)
            await _cdp.TrySendAsync("Emulation.setHardwareConcurrencyOverride", new { hardwareConcurrency = _fp.HardwareConcurrency }, session);
        if (_ctx.Latitude is { } lat && _ctx.Longitude is { } lon)
            await _cdp.TrySendAsync("Emulation.setGeolocationOverride", new { latitude = lat, longitude = lon, accuracy = 30 }, session);
    }

    /// <summary>
    /// Client-hint metadata. Brands and full versions come from the real browser, so they
    /// match what this Chrome version really sends; only the platform fields are replaced.
    /// </summary>
    private async Task<object> BuildUserAgentMetadataAsync(CancellationToken ct)
    {
        var (brands, fullVersionList, fullVersion) =
            await ReadRealBrandsAsync(ct) ?? await GreasedBrandsAsync();

        var (platform, platformVersion, architecture) = _ctx.Platform switch
        {
            Fingerprint.MacOS => ("macOS", "15.6.1", _fp.WebGlRenderer.Contains("Apple M") ? "arm" : "x86"),
            Fingerprint.Linux => ("Linux", "6.8.0", "x86"),
            _ => ("Windows", "19.0.0", "x86")
        };

        return new
        {
            brands,
            fullVersionList,
            fullVersion,
            platform,
            platformVersion,
            architecture,
            model = "",
            mobile = false,
            bitness = "64",
            wow64 = false
        };
    }

    /// <summary>
    /// Reads navigator.userAgentData from the initial tab (the browser is started on its
    /// new-tab page, a secure context, since about:blank doesn't expose userAgentData).
    /// </summary>
    private async Task<(JsonNode, JsonNode, string?)?> ReadRealBrandsAsync(CancellationToken ct)
    {
        string? session = null;
        try
        {
            string? targetId = null;
            for (var i = 0; i < 50 && targetId == null; i++)
            {
                var targets = await _cdp.SendAsync("Target.getTargets");
                targetId = targets.GetProperty("targetInfos").EnumerateArray()
                    .Where(t => t.GetProperty("type").GetString() == "page")
                    .Select(t => t.GetProperty("targetId").GetString())
                    .FirstOrDefault();
                if (targetId == null) await Task.Delay(100, ct);
            }
            if (targetId == null) return null;

            var attach = await _cdp.SendAsync("Target.attachToTarget", new { targetId, flatten = true });
            session = attach.GetProperty("sessionId").GetString();

            // The tab may still be loading; userAgentData appears once the page is up.
            for (var i = 0; i < 40; i++)
            {
                try
                {
                    var r = await _cdp.SendAsync("Runtime.evaluate", new
                    {
                        expression = "navigator.userAgentData ? navigator.userAgentData.getHighEntropyValues(['fullVersionList','uaFullVersion']) : null",
                        awaitPromise = true,
                        returnByValue = true
                    }, session);
                    if (r.GetProperty("result").TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object)
                        return (JsonNode.Parse(value.GetProperty("brands").GetRawText())!,
                                JsonNode.Parse(value.GetProperty("fullVersionList").GetRawText())!,
                                value.GetProperty("uaFullVersion").GetString());
                }
                catch (CdpException) { /* context replaced by a navigation; retry */ }
                await Task.Delay(150, ct);
            }
            return null;
        }
        catch (CdpException)
        {
            return null;
        }
        finally
        {
            if (session != null) await _cdp.TrySendAsync("Target.detachFromTarget", new { sessionId = session });
        }
    }

    /// <summary>Fallback: Chromium's own GREASE brand algorithm (user_agent_utils.cc), seeded by major version.</summary>
    private async Task<(JsonNode, JsonNode, string?)> GreasedBrandsAsync()
    {
        var v = await _cdp.SendAsync("Browser.getVersion");
        var fullVersion = v.GetProperty("product").GetString()?.Split('/').LastOrDefault() ?? "0.0.0.0";
        var major = int.TryParse(fullVersion.Split('.')[0], out var m) ? m : 0;

        string[] chars = { " ", "(", ":", "-", ".", "/", ")", ";", "=", "?", "_" };
        string[] greaseVersions = { "8", "99", "24" };
        var grease = $"Not{chars[major % chars.Length]}A{chars[(major + 1) % chars.Length]}Brand";
        var greaseVersion = greaseVersions[major % greaseVersions.Length];
        var product = _ctx.IsEdge ? "Microsoft Edge" : "Google Chrome";

        int[][] orders = { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } };
        var order = orders[major % orders.Length];

        JsonArray Build(string greaseVer, string ver)
        {
            var list = new (string Brand, string Version)[] { (grease, greaseVer), ("Chromium", ver), (product, ver) };
            var shuffled = new (string Brand, string Version)[3];
            for (var i = 0; i < 3; i++) shuffled[order[i]] = list[i];
            return new JsonArray(shuffled.Select(b => (JsonNode)new JsonObject { ["brand"] = b.Brand, ["version"] = b.Version }).ToArray());
        }

        return (Build(greaseVersion, major.ToString()), Build($"{greaseVersion}.0.0.0", fullVersion), fullVersion);
    }

    public static string NavigatorPlatform(string platform) => platform switch
    {
        Fingerprint.MacOS => "MacIntel",
        Fingerprint.Linux => "Linux x86_64",
        _ => "Win32"
    };

    /// <summary>"de-DE" → "de-DE,de" (Chrome adds the q-values itself). Empty keeps the browser's own list.</summary>
    private static string? AcceptLanguage(string language)
    {
        if (language.Length == 0) return null;
        var baseLang = language.Split('-')[0];
        return baseLang == language ? language : $"{language},{baseLang}";
    }

    private static string BuildScript(Fingerprint fp, LaunchContext ctx)
    {
        var (gpuVendor, gpuArch) = WebGpuIdentity(fp.WebGlRenderer);
        var config = new JsonObject
        {
            ["seed"] = fp.NoiseSeed,
            ["cores"] = fp.HardwareConcurrency,
            ["memory"] = Math.Min(fp.DeviceMemory, Fingerprint.MaxReportedMemory),
            ["navPlatform"] = NavigatorPlatform(ctx.Platform),
            ["screenW"] = fp.ScreenWidth,
            ["screenH"] = fp.ScreenHeight,
            ["taskbar"] = TaskbarHeight(ctx.Platform),
            ["winW"] = ctx.WindowWidth,
            ["winH"] = ctx.WindowHeight,
            ["canvas"] = fp.CanvasNoise,
            ["audio"] = fp.AudioNoise,
            ["webgl"] = fp.WebGlSpoof,
            ["glVendor"] = fp.WebGlVendor,
            ["glRenderer"] = fp.WebGlRenderer,
            ["gpuVendor"] = gpuVendor,
            ["gpuArch"] = gpuArch
        };

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MultiLogin.fingerprint.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("/*CONFIG*/", config.ToJsonString());
    }

    public static int TaskbarHeight(string platform) => platform switch
    {
        Fingerprint.MacOS => 25,
        Fingerprint.Linux => 27,
        _ => 40
    };

    private static (string vendor, string arch) WebGpuIdentity(string renderer)
    {
        if (renderer.Contains("Apple")) return ("apple", "metal-3");
        if (renderer.Contains("NVIDIA"))
            return ("nvidia", renderer.Contains("RTX 40") ? "lovelace" : renderer.Contains("RTX 30") ? "ampere" : "turing");
        if (renderer.Contains("Intel"))
        {
            if (renderer.Contains("Arc")) return ("intel", "xe-lpg");
            string[] gen12 = { "Xe", "730", "770", "ADL", "0x000046", "0x0000A7", "0x00009A" };
            return ("intel", gen12.Any(renderer.Contains) ? "gen-12lp" : "gen-9");
        }
        if (renderer.Contains("AMD"))
        {
            string[] rdna3 = { "760M", "780M", "890M", "gfx1103" };
            string[] rdna2 = { "RX 6", "610M", "0x0000164E", "0x000013C0", "raphael" };
            string[] gcn5 = { "Vega", "0x00001636", "0x00001638", "renoir" };
            return ("amd", rdna3.Any(renderer.Contains) ? "rdna-3"
                : rdna2.Any(renderer.Contains) ? "rdna-2"
                : gcn5.Any(renderer.Contains) ? "gcn-5" : "");
        }
        return ("", "");
    }

    public async ValueTask DisposeAsync()
    {
        _cdp.EventReceived -= OnEvent;
        await _cdp.DisposeAsync();
    }
}
