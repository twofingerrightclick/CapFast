using System.Security.Cryptography;
using System.Text;
using CapFast.Core;

internal static class SecureKeyChecks
{
    private const string Secret = "test-api-secret-never-log-🌍";

    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "capfast-secure-key-" + Guid.NewGuid());
        var previousGemini = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        var previousGoogle = Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        Directory.CreateDirectory(folder);
        try
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", null);
            var path = Path.Combine(folder, "key.dpapi");
            var protector = new TestProtector();
            var store = new SavedGoogleKeyStore(path, protector);
            Require(store.Load(out var error) == "" && error is null, "Missing saved key did not load safely.");
            Require(store.Save("") is not null && store.Save("  ") is not null && store.Save(new string('x', 4097)) is not null,
                "Empty or oversized key was accepted.");
            Require(!File.Exists(path), "Invalid save wrote a file.");
            Require(store.Save(Secret) is null, "Encrypted save failed.");
            var firstCiphertext = File.ReadAllBytes(path);
            Require(!Encoding.UTF8.GetString(firstCiphertext).Contains(Secret), "Saved file contained plaintext.");
            Require(new SavedGoogleKeyStore(path, protector).Load(out error) == Secret && error is null,
                "Saved key did not survive store recreation.");
            Require(protector.LastPlaintext is not null && protector.LastPlaintext.All(value => value == 0),
                "Save did not clear its plaintext byte buffer.");

            protector.FailProtect = true;
            SafeFailure(store.Save("replacement"));
            Require(File.ReadAllBytes(path).SequenceEqual(firstCiphertext), "Failed save replaced the previous key.");
            protector.FailProtect = false;
            Require(store.Save("replacement") is null && store.Load(out error) == "replacement" && error is null,
                "Replacing the saved key failed.");
            Require(store.Save(Secret) is null, "Restoring test key failed.");

            var env = Path.Combine(folder, ".env");
            const string envContents = "GEMINI_API_KEY=file-key\nGOOGLE_API_KEY=other-file-key\n";
            File.WriteAllText(env, envContents);
            var local = new LocalConfiguration(env, savedKeyStore: store);
            Require(local.GoogleKey == Secret && local.Error is null, "Saved key did not override the local file.");
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", "google-environment");
            Require(local.GoogleKey == "google-environment" && LocalConfiguration.HasEnvironmentGoogleKey,
                "GOOGLE environment key did not override saved key.");
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", "gemini-environment");
            Require(local.GoogleKey == "gemini-environment", "GEMINI environment key did not take first priority.");
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", null);

            // A late load must not overwrite edits, including edit-back and remove
            // operations where string equality alone would accept the stale snapshot.
            var settings = new CaptionSettings(loadLocalConfiguration: false) { IsLoadingConfiguration = true };
            var original = settings.GeminiKey;
            var revision = settings.GoogleKeyRevision;
            settings.GeminiKey = "manual";
            settings.GeminiKey = original;
            settings.ApplyLocalConfiguration(local, original, revision);
            Require(settings.GeminiKey == original && !settings.IsLoadingConfiguration && !settings.IsGoogleKeyFromConfiguration,
                "Late load overwrote an edit-back or lost its unsaved state.");
            revision = settings.GoogleKeyRevision;
            settings.PreserveCurrentGoogleKey();
            settings.ApplyLocalConfiguration(local, original, revision);
            Require(settings.GeminiKey == original, "Late load undid an explicit save/remove action.");
            revision = settings.GoogleKeyRevision;
            settings.ApplyLocalConfiguration(local, original, revision);
            Require(settings.GeminiKey == Secret && settings.IsGoogleKeyFromConfiguration,
                "Untouched late load did not apply the saved key or mark it loaded.");

            protector.FailUnprotect = true;
            Require(store.Load(out error) == "", "Failed decrypt returned a key.");
            SafeFailure(error);
            var fallback = new LocalConfiguration(env, savedKeyStore: store);
            Require(fallback.GoogleKey == "file-key", "Decrypt failure did not allow .env fallback.");
            SafeFailure(fallback.Error);
            var errors = new ErrorLog();
            errors.Add(error!);
            errors.Add(fallback.Error!);
            Require(errors.Entries.All(entry => !entry.Message.Contains(Secret)), "Secret leaked into diagnostics.");
            protector.FailUnprotect = false;
            Require(new LocalConfiguration(env, readValues: false, savedKeyStore: store).GoogleKey == "",
                "Deferred startup read a saved key.");

            File.WriteAllBytes(path, [1, 2, 3]);
            Require(store.Load(out error) == "", "Corrupt ciphertext returned a key.");
            SafeFailure(error);
            File.WriteAllBytes(path, new byte[65537]);
            Require(store.Load(out error) == "", "Oversized ciphertext was accepted.");
            SafeFailure(error);
            Require(store.Remove() is null && !File.Exists(path) && store.Remove() is null,
                "Removing a key was not safe and repeatable.");
            Require(new LocalConfiguration(env, savedKeyStore: store).GoogleKey == "file-key", "Removal did not restore file fallback.");
            File.WriteAllText(env, "GOOGLE_API_KEY=other-file-key\n");
            Require(new LocalConfiguration(env, savedKeyStore: store).GoogleKey == "other-file-key", "GOOGLE .env fallback failed.");
            File.WriteAllText(env, envContents);
            Require(store.Save(Secret) is null, "Preparing saved key failed.");
            Require(new LocalConfiguration(env + ".missing", savedKeyStore: store).GoogleKey == Secret,
                "Missing .env hid a saved key.");
            File.WriteAllText(env, new string('x', 65537));
            var badFile = new LocalConfiguration(env, savedKeyStore: store);
            Require(badFile.GoogleKey == Secret && badFile.Error is not null, "Unreadable .env hid a saved key.");
            File.WriteAllText(env, envContents);

            // Real filesystem failures must be sanitized too, and leave no temp files.
            var blocked = Path.Combine(folder, Secret);
            Directory.CreateDirectory(blocked);
            File.WriteAllText(Path.Combine(blocked, "keep"), "untouched");
            var blockedStore = new SavedGoogleKeyStore(blocked, protector);
            SafeFailure(blockedStore.Save(Secret));
            SafeFailure(blockedStore.Remove());
            Require(blockedStore.Load(out error) == "", "Directory loaded as a saved key.");
            SafeFailure(error);
            Require(Directory.GetFiles(folder, ".google-api-key-*.tmp").Length == 0, "Failed save left temporary files.");
            Require(File.ReadAllText(env) == envContents, "Secure persistence changed the .env file.");

            var unsupportedPath = Path.Combine(folder, "unsupported", "key");
            var unsupported = new SavedGoogleKeyStore(unsupportedPath, new TestProtector { IsSupported = false });
            Require(unsupported.Load(out error) == "" && error is null && unsupported.Save(Secret) is not null
                && unsupported.Remove() is not null && !Directory.Exists(Path.GetDirectoryName(unsupportedPath)),
                "Unsupported storage touched disk or pretended to save.");
            if (!OperatingSystem.IsWindows())
            {
                var native = new SavedGoogleKeyStore(path);
                Require(native.Load(out error) == "" && error is null && native.Save(Secret) is not null,
                    "Non-Windows path invoked DPAPI.");
            }
            else
            {
                var native = new SavedGoogleKeyStore(Path.Combine(folder, "windows-roundtrip.dpapi"));
                Require(native.Save(Secret) is null && native.Load(out error) == Secret && error is null,
                    "Windows CurrentUser DPAPI round trip failed.");
                Require(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(folder, "windows-roundtrip.dpapi"))).Contains(Secret),
                    "DPAPI file contained plaintext.");
                Require(native.Remove() is null, "Windows DPAPI key removal failed.");
            }
            Console.WriteLine("Secure key checks passed: persistence, replacement, removal, precedence, pending-load protection, sanitized failures, and platform guards.");
            if (!OperatingSystem.IsWindows()) Console.WriteLine("Windows DPAPI round trip skipped: requires Windows.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", previousGemini);
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", previousGoogle);
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void SafeFailure(string? error) => Require(!string.IsNullOrEmpty(error) && !error.Contains(Secret),
        "Persistence failure was hidden or leaked a secret.");

    // Cross-platform test seam; production always uses WindowsCurrentUserProtector.
    private sealed class TestProtector : ISecretProtector
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public bool IsSupported { get; init; } = true;
        public bool FailProtect { get; set; }
        public bool FailUnprotect { get; set; }
        public byte[]? LastPlaintext { get; private set; }
        public byte[] Protect(byte[] plaintext)
        {
            LastPlaintext = plaintext;
            if (FailProtect) throw new CryptographicException(Secret);
            var result = new byte[28 + plaintext.Length];
            RandomNumberGenerator.Fill(result.AsSpan(0, 12));
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(result.AsSpan(0, 12), plaintext, result.AsSpan(28), result.AsSpan(12, 16));
            return result;
        }
        public byte[] Unprotect(byte[] ciphertext)
        {
            if (FailUnprotect) throw new CryptographicException(Secret);
            var result = new byte[ciphertext.Length - 28];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(ciphertext.AsSpan(0, 12), ciphertext.AsSpan(28), ciphertext.AsSpan(12, 16), result);
            return result;
        }
    }
}
