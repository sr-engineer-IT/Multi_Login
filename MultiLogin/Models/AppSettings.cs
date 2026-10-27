namespace MultiLogin.Models;

public class AppSettings
{
    /// <summary>Path to chrome.exe / msedge.exe. Empty means auto-detect.</summary>
    public string BrowserPath { get; set; } = "";
}
