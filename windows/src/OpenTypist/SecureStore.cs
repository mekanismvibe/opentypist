using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenTypist;

/// Personal data (snippets, the never-correct list) encrypted at rest with
/// DPAPI for the current Windows user, in %APPDATA%\OpenTypist\Personal (or
/// the OPENTYPIST_TEST_STORE folder). The
/// Windows side of SecureStore.swift. A file that can't be decrypted (another
/// user, a reinstalled Windows) is set aside as `.unreadable`, never
/// overwritten.
public sealed class SecureStore(string? directory = null)
{
    public static readonly SecureStore Shared = new();

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OpenTypist personal data v1");

    public string Directory { get; } = directory ?? Path.Combine(AppSettings.DataDirectory, "Personal");

    public T? Load<T>(string name) where T : class
    {
        var path = Path.Combine(Directory, name);
        if (!File.Exists(path)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<T>(plain);
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            AppStatus.Log($"{name} can't be read ({e.GetType().Name}); keeping it as {name}.unreadable");
            try { File.Move(path, path + ".unreadable", overwrite: true); } catch (IOException) { }
            return null;
        }
        catch (IOException) { return null; }
    }

    public void Delete(string name)
    {
        try { File.Delete(Path.Combine(Directory, name)); } catch (IOException) { }
    }

    public void Save<T>(string name, T value)
    {
        var path = Path.Combine(Directory, name);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path + ".tmp", cipher);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (IOException e)
        {
            AppStatus.Log($"couldn't save {name}: {e.Message}");
        }
    }
}
