using System.Security.Cryptography;
using System.Text;

namespace CapFast.Core;

public interface IGoogleKeyStore
{
    string Load(out string? error);
    string? Save(string key);
    string? Remove();
}

public interface ISecretProtector
{
    bool IsSupported { get; }
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

public sealed class WindowsCurrentUserProtector : ISecretProtector
{
    public bool IsSupported => OperatingSystem.IsWindows();
    public byte[] Protect(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
    }
    public byte[] Unprotect(byte[] ciphertext)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
    }
}

public sealed class SavedGoogleKeyStore : IGoogleKeyStore
{
    private readonly string _path;
    private readonly ISecretProtector _protector;
    private const string Unsupported = "Secure API key storage is available only on Windows. Use the local API key file on this platform.";
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CapFast", "google-api-key.dpapi");

    public SavedGoogleKeyStore(string? path = null, ISecretProtector? protector = null)
    {
        _path = path ?? DefaultPath;
        _protector = protector ?? new WindowsCurrentUserProtector();
    }

    public string Load(out string? error)
    {
        error = null;
        if (!_protector.IsSupported) return "";
        byte[]? plaintext = null;
        try
        {
            using var file = File.OpenRead(_path);
            if (file.Length is <= 0 or > 65536) throw new IOException();
            var ciphertext = new byte[(int)file.Length];
            file.ReadExactly(ciphertext);
            plaintext = _protector.Unprotect(ciphertext);
            var key = new UTF8Encoding(false, true).GetString(plaintext);
            if (!ValidKey(key)) throw new IOException();
            return key;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return ""; }
        catch
        {
            // Never include exception messages: a provider or filesystem can echo a secret.
            error = "Could not read the saved API key for this Windows account. Enter your key and click Save API key to replace it, or remove the saved key. Environment variables and the local API key file are still available.";
            return "";
        }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    public string? Save(string key)
    {
        if (!_protector.IsSupported) return Unsupported;
        if (!ValidKey(key)) return "Enter an API key (up to 4096 characters) before saving.";
        byte[]? plaintext = null;
        string? temporary = null;
        try
        {
            plaintext = Encoding.UTF8.GetBytes(key);
            var ciphertext = _protector.Protect(plaintext);
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".google-api-key-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(ciphertext);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
            return null;
        }
        catch { return "Could not save the API key. Check access to your CapFast application data folder and try Save API key again. Your current key remains in memory."; }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (temporary is not null) { try { File.Delete(temporary); } catch { } }
        }
    }

    public string? Remove()
    {
        if (!_protector.IsSupported) return Unsupported;
        try { File.Delete(_path); return null; }
        catch { return "Could not remove the saved API key. Check access to your CapFast application data folder and try Remove saved key again."; }
    }

    private static bool ValidKey(string key) => !string.IsNullOrWhiteSpace(key) && key.Length <= 4096;
}
