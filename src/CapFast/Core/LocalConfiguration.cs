namespace CapFast.Core;

public sealed class LocalConfiguration
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly string _savedGoogleKey = "";
    public string Path { get; }
    public string? Error { get; private set; }
    public LocalConfiguration(string? path = null, bool readValues = true, IGoogleKeyStore? savedKeyStore = null)
    {
        Path = path ?? FallbackPath;
        if (!readValues) return;
        try
        {
            _savedGoogleKey = (savedKeyStore ?? new SavedGoogleKeyStore()).Load(out var savedError);
            Error = savedError;
        }
        catch { Error = "Could not read the saved API key. Enter a key in Settings or use the local API key file."; }
        try
        {
            if (path is null) Path = FindPath();
            if (!File.Exists(Path)) return;
            if (new FileInfo(Path).Length > 65536) throw new IOException();
            foreach (var line in File.ReadLines(Path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                if (trimmed.StartsWith("export ", StringComparison.Ordinal)) trimmed = trimmed[7..].TrimStart();
                var equals = trimmed.IndexOf('=');
                if (equals <= 0) continue;
                var name = trimmed[..equals].Trim();
                if (name is not ("GEMINI_API_KEY" or "GOOGLE_API_KEY")) continue;
                var value = trimmed[(equals + 1)..].Trim();
                if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))) value = value[1..^1];
                _values[name] = value;
            }
        }
        catch { Error = (Error is null ? "" : Error + " ") + "Could not read the local API configuration file. Check its permissions and size."; }
    }
    public string Get(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } environment
        ? environment : _values.GetValueOrDefault(name, "");
    public string GoogleKey => Environment.GetEnvironmentVariable("GEMINI_API_KEY") is { Length: > 0 } gemini ? gemini
        : Environment.GetEnvironmentVariable("GOOGLE_API_KEY") is { Length: > 0 } google ? google
        : _savedGoogleKey is { Length: > 0 } saved ? saved
        : _values.GetValueOrDefault("GEMINI_API_KEY") is { Length: > 0 } local ? local : _values.GetValueOrDefault("GOOGLE_API_KEY", "");
    public static bool HasEnvironmentGoogleKey => Environment.GetEnvironmentVariable("GEMINI_API_KEY") is { Length: > 0 }
        || Environment.GetEnvironmentVariable("GOOGLE_API_KEY") is { Length: > 0 };
    private static string FindPath()
    {
        // A user-owned application file takes precedence. macOS can defer
        // Documents access after signing a new development build.
        if (File.Exists(FallbackPath)) return FallbackPath;
        // Packaged development builds find only this project's root, never arbitrary parent .env files.
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; folder != null && depth < 9; depth++, folder = folder.Parent)
            if (File.Exists(System.IO.Path.Combine(folder.FullName, "src", "CapFast", "CapFast.csproj")))
                return System.IO.Path.Combine(folder.FullName, ".env");
        return FallbackPath;
    }
    public static string FallbackPath
    {
        get
        {
            var dataDirectory = OperatingSystem.IsMacOS()
                ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return System.IO.Path.Combine(dataDirectory, "CapFast", ".env");
        }
    }
}
