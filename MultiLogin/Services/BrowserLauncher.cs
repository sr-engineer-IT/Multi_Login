using System.Diagnostics;
using MultiLogin.Models;

namespace MultiLogin.Services;

/// <summary>Launches and tracks one Chromium instance per profile.</summary>
public class BrowserLauncher
{
    private sealed record Session(Process Process, ProxyForwarder? Forwarder);

    private readonly ProfileStore _store;
    private readonly Dictionary<string, Session> _sessions = new();

    /// <summary>Raised (on a worker thread) when a profile's browser exits.</summary>
    public event Action<string>? ProfileExited;

    public BrowserLauncher(ProfileStore store) => _store = store;

    public bool IsRunning(string profileId)
    {
        lock (_sessions) return _sessions.ContainsKey(profileId);
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

    public void Launch(Profile profile)
    {
        if (IsRunning(profile.Id))
            throw new InvalidOperationException($"\"{profile.Name}\" is already running.");

        var browser = ResolveBrowserPath()
            ?? throw new InvalidOperationException("No Chrome, Edge or Brave installation found. Set the browser path in Settings.");

        var userDataDir = _store.GetUserDataDir(profile);
        Directory.CreateDirectory(userDataDir);
        if (IsUserDataDirLocked(userDataDir))
            throw new InvalidOperationException(
                $"\"{profile.Name}\" is already open in a browser window that this app didn't start. Close it first.");

        var forwarder = profile.HasProxy ? new ProxyForwarder(profile) : null;
        try
        {
            var psi = new ProcessStartInfo(browser) { UseShellExecute = false };
            foreach (var arg in BuildArguments(profile, userDataDir, forwarder))
                psi.ArgumentList.Add(arg);

            var process = Process.Start(psi) ?? throw new InvalidOperationException("Browser failed to start.");
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => OnExited(profile.Id);

            lock (_sessions) _sessions[profile.Id] = new Session(process, forwarder);
            if (process.HasExited) OnExited(profile.Id);
        }
        catch
        {
            forwarder?.Dispose();
            throw;
        }

        profile.LastLaunchedUtc = DateTime.UtcNow;
        _store.Save();
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
            if (!p.HasExited && p.CloseMainWindow())
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
        session.Forwarder?.Dispose();
        session.Process.Dispose();
        ProfileExited?.Invoke(profileId);
    }

    private static IEnumerable<string> BuildArguments(Profile profile, string userDataDir, ProxyForwarder? forwarder)
    {
        yield return $"--user-data-dir={userDataDir}";
        yield return "--no-first-run";
        yield return "--no-default-browser-check";

        if (forwarder != null)
            yield return $"--proxy-server={forwarder.LocalProxyUrl}";

        if (profile.BlockWebRtcLeak)
            yield return "--force-webrtc-ip-handling-policy=disable_non_proxied_udp";

        if (!string.IsNullOrWhiteSpace(profile.UserAgent))
            yield return $"--user-agent={profile.UserAgent.Trim()}";

        if (!string.IsNullOrWhiteSpace(profile.Language))
            yield return $"--lang={profile.Language.Trim()}";

        if (profile.WindowWidth > 0 && profile.WindowHeight > 0)
            yield return $"--window-size={profile.WindowWidth},{profile.WindowHeight}";

        foreach (var arg in SplitLines(profile.ExtraArgs))
            yield return arg;

        foreach (var url in SplitLines(profile.StartUrls))
            yield return url;
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
