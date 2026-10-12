using System.Text.Json;

namespace CapFast.Core;

public sealed record CaptionPreferences(string AudioDeviceId, double FontSize, int ReadingWordsPerMinute,
    int? EnglishReadingWordsPerMinute = null, int? SwahiliReadingWordsPerMinute = null,
    int? FrenchReadingWordsPerMinute = null, bool AutomaticCaptionCatchUp = true)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string DefaultPath => Path.Combine(
        Path.GetDirectoryName(LocalConfiguration.FallbackPath)!, "settings.json");

    public static CaptionPreferences Default { get; } = new("", 48, 180);

    public static CaptionPreferences Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path) || new FileInfo(path).Length > 16_384) return Default;
            var saved = JsonSerializer.Deserialize<CaptionPreferences>(File.ReadAllText(path), JsonOptions);
            if (saved is null) return Default;
            return new CaptionPreferences(
                saved.AudioDeviceId is { Length: <= 4096 } ? saved.AudioDeviceId : Default.AudioDeviceId,
                double.IsFinite(saved.FontSize) && saved.FontSize is >= 28 and <= 72 ? saved.FontSize : Default.FontSize,
                saved.ReadingWordsPerMinute is >= 80 and <= 300 ? saved.ReadingWordsPerMinute : Default.ReadingWordsPerMinute,
                CaptionSettings.ValidReadingSpeedOverride(saved.EnglishReadingWordsPerMinute),
                CaptionSettings.ValidReadingSpeedOverride(saved.SwahiliReadingWordsPerMinute),
                CaptionSettings.ValidReadingSpeedOverride(saved.FrenchReadingWordsPerMinute),
                saved.AutomaticCaptionCatchUp);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return Default; }
    }

    public static void Save(CaptionSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var preferences = new CaptionPreferences(settings.AudioDeviceId, settings.FontSize, settings.ReadingWordsPerMinute,
                settings.EnglishReadingWordsPerMinute, settings.SwahiliReadingWordsPerMinute,
                settings.FrenchReadingWordsPerMinute, settings.AutomaticCaptionCatchUp);
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
