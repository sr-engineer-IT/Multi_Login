using System.Text.Json;
using System.Text.Json.Serialization;
using MultiLogin.Models;

namespace MultiLogin.Services;

/// <summary>Persists profiles and settings as JSON under %LOCALAPPDATA%\MultiLogin.</summary>
public class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string DataDir { get; }
    public string ProfilesDir => Path.Combine(DataDir, "Profiles");
    private string ProfilesFile => Path.Combine(DataDir, "profiles.json");
    private string SettingsFile => Path.Combine(DataDir, "settings.json");

    public List<Profile> Profiles { get; private set; } = new();
    public AppSettings Settings { get; private set; } = new();

    public ProfileStore()
    {
        DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiLogin");
        Directory.CreateDirectory(ProfilesDir);
    }

    public void Load()
    {
        Profiles = ReadJson<List<Profile>>(ProfilesFile) ?? new();
        Settings = ReadJson<AppSettings>(SettingsFile) ?? new();
    }

    public void Save()
    {
        WriteJson(ProfilesFile, Profiles);
        WriteJson(SettingsFile, Settings);
    }

    /// <summary>The Chromium user-data-dir that holds this profile's cookies, storage and cache.</summary>
    public string GetUserDataDir(Profile profile) => Path.Combine(ProfilesDir, profile.Id);

    public void Delete(Profile profile)
    {
        Profiles.Remove(profile);
        Save();
        var dir = GetUserDataDir(profile);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    private static T? ReadJson<T>(string path)
    {
        if (!File.Exists(path)) return default;
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
    }

    private static void WriteJson<T>(string path, T value)
    {
        // Write to a temp file first so a crash mid-write can't corrupt the existing file.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
