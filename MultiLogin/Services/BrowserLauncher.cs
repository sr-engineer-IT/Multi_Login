using System.Diagnostics;
using MultiLogin.Models;

namespace MultiLogin.Services;

/// <summary>Launches and tracks one Chromium instance per profile.</summary>
public class BrowserLauncher
{
    private sealed record Session(Process Process, ProxyForwarder? Forwarder, FingerprintInjector? Injector);

    private readonly ProfileStore _store;
    private readonly Dictionary<string, Session> _sessions = new();
    private readonly HashSet<string> _launching = new();

    /// <summary>Raised (on a worker thread) when a profile's browser exits.</summary>
    public event Action<string>? ProfileExited;

    public BrowserLauncher(ProfileStore store) => _store = store;

    public bool IsRunning(string profileId)
    {
        lock (_sessions) return _sessions.ContainsKey(profileId) || _launching.Contains(profileId);
    }

    public int RunningCount
    {
        get { lock (_sessions) return _sessions.Count; }
    }

    public static string? DetectBrowser()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        {
            Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public string? ResolveBrowserPath()
    {
        var configured = _store.Settings.BrowserPath;
        return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? configured : DetectBrowser();
    }

    public async Task LaunchAsync(Profile profile)
    {
        lock (_sessions)
        {
            if (_sessions.ContainsKey(profile.Id) || !_launching.Add(profile.Id))
                throw new InvalidOperationException($"\"{profile.Name}\" is already running.");
        }

        ProxyForwarder? forwarder = null;
        FingerprintInjector? injector = null;
        Process? process = null;
        try
        {
            var browser = ResolveBrowserPath()
                ?? throw new InvalidOperationException("No Chrome, Edge or Brave installation found. Set the browser path in Settings.");

            var userDataDir = _store.GetUserDataDir(profile);
            Directory.CreateDirectory(userDataDir);
            if (IsUserDataDirLocked(userDataDir))
                throw new InvalidOperationException(
                    $"\"{profile.Name}\" is already open in a browser window that this app didn't start. Close it first.");

            forwarder = profile.HasProxy ? new ProxyForwarder(profile) : null;
            var fp = profile.Fingerprint;
            var context = fp.Enabled ? await ResolveContextAsync(profile, browser, forwarder) : null;

            var activePortFile = Path.Combine(userDataDir, "DevToolsActivePort");
            File.Delete(activePortFile);

            var psi = new ProcessStartInfo(browser) { UseShellExecute = false };
            foreach (var arg in BuildArguments(profile, userDataDir, forwarder, context))
                psi.ArgumentList.Add(arg);

            process = Process.Start(psi) ?? throw new InvalidOperationException("Browser failed to start.");

            if (context != null)
            {
                var endpoint = await WaitForDevToolsAsync(activePortFile, process);
                injector = await FingerprintInjector.AttachAsync(endpoint, fp, context, CancellationToken.None);
                await injector.OpenStartPagesAsync(SplitLines(profile.StartUrls).ToList());
            }

            lock (_sessions)
            {
                _launching.Remove(profile.Id);
                _sessions[profile.Id] = new Session(process, forwarder, injector);
            }
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => OnExited(profile.Id);
            if (process.HasExited) OnExited(profile.Id);
        }
        catch
        {
            lock (_sessions) _launching.Remove(profile.Id);
            if (injector != null) await injector.DisposeAsync();
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            forwarder?.Dispose();
            throw;
        }

        profile.LastLaunchedUtc = DateTime.UtcNow;
        _store.Save();
    }

    /// <summary>Works out the UA, timezone and location the fingerprint should report.</summary>
    private static async Task<LaunchContext> ResolveContextAsync(Profile profile, string browser, ProxyForwarder? forwarder)
    {
        var fp = profile.Fingerprint;
        var customUa = profile.UserAgent.Trim();
        var platform = customUa.Length > 0 ? UserAgents.DetectPlatform(customUa) ?? fp.Platform : fp.Platform;
        var isEdge = UserAgents.IsEdge(browser);
        var userAgent = customUa.Length > 0
            ? customUa
            : UserAgents.Build(platform, UserAgents.GetMajorVersion(browser), isEdge);

        string? timezone = string.IsNullOrWhiteSpace(fp.Timezone) ? null : fp.Timezone.Trim();
        double? lat = null, lon = null;

        // Without a proxy the real timezone and location already match the real IP.
        if (forwarder != null && (timezone == null || fp.GeolocationFromIp))
        {
            var ip = await ProxyChecker.LookupAsync(forwarder.LocalProxyUrl);
            if (!ip.Ok)
                throw new InvalidOperationException($"Couldn't connect through the proxy, so the timezone can't be matched to it:\n{ip.Message}");
            timezone ??= ip.Timezone;
            if (fp.GeolocationFromIp) (lat, lon) = (ip.Latitude, ip.Longitude);
        }

        var (width, height) = (profile.WindowWidth, profile.WindowHeight);
        if (width <= 0 || height <= 0)
        {
            // Fit the window inside both the spoofed screen (a window bigger than the
            // "monitor" is a giveaway) and the real one.
            var real = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
            width = (int)(Math.Min(fp.ScreenWidth, real.Width) * 0.85);
            height = (int)(Math.Min(fp.ScreenHeight - FingerprintInjector.TaskbarHeight(platform), real.Height) * 0.9);
        }

        return new LaunchContext(userAgent, platform, profile.Language.Trim(), timezone, lat, lon, isEdge, width, height);
    }

    private static async Task<Uri> WaitForDevToolsAsync(string activePortFile, Process process)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException("The browser closed during startup.");
            try
            {
                var lines = File.ReadAllLines(activePortFile);
                if (lines.Length >= 2 && int.TryParse(lines[0], out var port))
                    return new Uri($"ws://127.0.0.1:{port}{lines[1]}");
            }
            catch (IOException) { }
            await Task.Delay(100);
        }
        throw new InvalidOperationException("The browser didn't open its DevTools connection in time.");
    }

    public async Task StopAsync(string profileId)
    {
        Session? session;
        lock (_sessions) _sessions.TryGetValue(profileId, out session);
        if (session == null) return;

        var p = session.Process;
        try
        {
            // Ask nicely first so Chrome can flush cookies and session state to disk.
            if (session.Injector != null)
                await session.Injector.CloseBrowserAsync();
            else if (!p.HasExited)
                p.CloseMainWindow();
            await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }

        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }

    public async Task StopAllAsync()
    {
        string[] ids;
        lock (_sessions) ids = _sessions.Keys.ToArray();
        await Task.WhenAll(ids.Select(StopAsync));
    }

    private void OnExited(string profileId)
    {
        Session? session;
        lock (_sessions)
        {
            if (!_sessions.Remove(profileId, out session)) return;
        }
        if (session.Injector != null) _ = session.Injector.DisposeAsync().AsTask();
        session.Forwarder?.Dispose();
        session.Process.Dispose();
        ProfileExited?.Invoke(profileId);
    }

    private static IEnumerable<string> BuildArguments(Profile profile, string userDataDir, ProxyForwarder? forwarder, LaunchContext? context)
    {
        yield return $"--user-data-dir={userDataDir}";
        yield return "--no-first-run";
        yield return "--no-default-browser-check";
        // Chrome sync uploads the real computer name (e.g. DESKTOP-7F3K2A) to the Google account's
        // device list. Without sync, Google only sees "Windows · Chrome", like a plain web sign-in.
        yield return "--disable-sync";

        if (forwarder != null)
            yield return $"--proxy-server={forwarder.LocalProxyUrl}";

        if (profile.BlockWebRtcLeak)
            yield return "--force-webrtc-ip-handling-policy=disable_non_proxied_udp";

        var userAgent = context?.UserAgent ?? profile.UserAgent.Trim();
        if (userAgent.Length > 0)
            yield return $"--user-agent={userAgent}";

        if (!string.IsNullOrWhiteSpace(profile.Language))
            yield return $"--lang={profile.Language.Trim()}";

        var (width, height) = context != null
            ? (context.WindowWidth, context.WindowHeight)
            : (profile.WindowWidth, profile.WindowHeight);
        if (width > 0 && height > 0)
            yield return $"--window-size={width},{height}";

        foreach (var arg in SplitLines(profile.ExtraArgs))
            yield return arg;

        if (context != null)
        {
            // Debugging port 0 = pick a free port; Chrome writes it to DevToolsActivePort.
            // Start on the new-tab page: the real start pages open only once overrides are in place.
            yield return "--remote-debugging-port=0";
            // Chrome sets navigator.webdriver = true whenever the debugging port is open, which tells
            // every site (Google, LinkedIn, ...) the browser is robot-controlled. This keeps it false.
            yield return "--disable-blink-features=AutomationControlled";
            yield return "chrome://newtab/";
        }
        else
        {
            foreach (var url in SplitLines(profile.StartUrls))
                yield return url;
        }
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Chromium holds "lockfile" in the user-data-dir open exclusively while running.</summary>
    private static bool IsUserDataDirLocked(string userDataDir)
    {
        var lockFile = Path.Combine(userDataDir, "lockfile");
        if (!File.Exists(lockFile)) return false;
        try
        {
            using var _ = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
