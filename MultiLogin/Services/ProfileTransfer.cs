using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiLogin.Models;

namespace MultiLogin.Services;

public class WrongPasswordException() : Exception("The password is wrong, or the file is damaged.");

/// <summary>
/// Exports and imports profile settings (name, notes, start pages, proxy, browser options and
/// the exact fingerprint) so a profile can be recreated on another computer. Browser data such
/// as cookies and logins is not included.
///
/// Proxy passwords are stored with Windows DPAPI, which only this PC can decrypt, so they're
/// exported in plain form, which is why they're only included when the file has a password.
/// With a password the whole profile list is encrypted with AES-256-GCM (key via PBKDF2).
/// </summary>
public static class ProfileTransfer
{
    public const string FileFilter = "Multi Login profiles (*.mlprofiles)|*.mlprofiles";
    private const string Format = "MultiLoginProfiles";
    private const int Pbkdf2Iterations = 600_000;
    private const string FingerprintCodePrefix = "MLFP1.";

    public static void Export(IEnumerable<Profile> profiles, string path, string? password)
    {
        var encrypt = !string.IsNullOrEmpty(password);
        var list = new JsonArray();
        foreach (var p in profiles)
        {
            var node = JsonSerializer.SerializeToNode(p, ProfileStore.JsonOptions)!.AsObject();
            node.Remove(nameof(Profile.ProxyPasswordProtected));
            if (encrypt && p.ProxyPassword.Length > 0)
                node["ProxyPassword"] = p.ProxyPassword;
            list.Add(node);
        }

        var file = new JsonObject
        {
            ["format"] = Format,
            ["version"] = 1,
            ["exportedUtc"] = DateTime.UtcNow,
            ["encrypted"] = encrypt
        };

        if (encrypt)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var plain = Encoding.UTF8.GetBytes(list.ToJsonString());
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(DeriveKey(password!, salt), tag.Length))
                aes.Encrypt(nonce, plain, cipher, tag);

            file["salt"] = Convert.ToBase64String(salt);
            file["nonce"] = Convert.ToBase64String(nonce);
            file["tag"] = Convert.ToBase64String(tag);
            file["data"] = Convert.ToBase64String(cipher);
        }
        else
        {
            file["profiles"] = list;
        }

        File.WriteAllText(path, file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static bool IsEncrypted(string path) =>
        ReadFile(path)["encrypted"]?.GetValue<bool>() == true;

    /// <summary>Reads profiles from an export file. Throws <see cref="WrongPasswordException"/> on a bad password.</summary>
    public static List<Profile> Import(string path, string? password)
    {
        var file = ReadFile(path);
        JsonArray list;

        if (file["encrypted"]?.GetValue<bool>() == true)
        {
            if (string.IsNullOrEmpty(password)) throw new WrongPasswordException();
            var salt = Convert.FromBase64String(file["salt"]!.GetValue<string>());
            var nonce = Convert.FromBase64String(file["nonce"]!.GetValue<string>());
            var tag = Convert.FromBase64String(file["tag"]!.GetValue<string>());
            var cipher = Convert.FromBase64String(file["data"]!.GetValue<string>());
            var plain = new byte[cipher.Length];
            try
            {
                using var aes = new AesGcm(DeriveKey(password, salt), tag.Length);
                aes.Decrypt(nonce, cipher, tag, plain);
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new WrongPasswordException();
            }
            list = JsonNode.Parse(plain)!.AsArray();
        }
        else
        {
            list = file["profiles"]!.AsArray();
        }

        var result = new List<Profile>();
        foreach (var node in list)
        {
            var obj = node!.AsObject();
            var proxyPassword = obj["ProxyPassword"]?.GetValue<string>() ?? "";
            obj.Remove("ProxyPassword");
            var profile = obj.Deserialize<Profile>(ProfileStore.JsonOptions)!;
            profile.ProxyPassword = proxyPassword; // re-encrypted with this PC's DPAPI key
            if (profile.Fingerprint == null || profile.Fingerprint.NoiseSeed == 0)
                profile.Fingerprint = Fingerprint.Generate(Fingerprint.Windows);
            result.Add(profile);
        }
        return result;
    }

    private static JsonObject ReadFile(string path)
    {
        JsonObject? file;
        try { file = JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (JsonException) { file = null; }
        if (file?["format"]?.GetValue<string>() != Format)
            throw new InvalidDataException("This isn't a Multi Login profiles file.");
        return file;
    }

    private static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);

    // ---- Fingerprint codes ------------------------------------------------------------------

    /// <summary>A short text code holding an exact fingerprint, for copying between computers.</summary>
    public static string ToFingerprintCode(Fingerprint fp)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(fp, ProfileStore.JsonOptions);
        return FingerprintCodePrefix + Convert.ToBase64String(json);
    }

    public static Fingerprint? FromFingerprintCode(string code)
    {
        code = code.Trim();
        if (!code.StartsWith(FingerprintCodePrefix, StringComparison.Ordinal)) return null;
        try
        {
            var json = Convert.FromBase64String(code[FingerprintCodePrefix.Length..]);
            var fp = JsonSerializer.Deserialize<Fingerprint>(json, ProfileStore.JsonOptions);
            return fp is { NoiseSeed: not 0 } ? fp : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}
