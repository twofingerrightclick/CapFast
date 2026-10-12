namespace CapFast.Core;

public enum CaptionState { Paused, Starting, Live, Pausing }
public sealed record AudioInput(string Id, string Name);

public sealed class CaptionSettings
{
    public CaptionSettings(bool loadLocalConfiguration = true)
    {
        var local = loadLocalConfiguration ? new LocalConfiguration()
            : new LocalConfiguration(LocalConfiguration.FallbackPath, readValues: false);
        ConfigurationPath = local.Path;
        ConfigurationError = local.Error;
        GeminiKey = local.GoogleKey;
    }
    public string ConfigurationPath { get; private set; }
    public string? ConfigurationError { get; set; }
    public bool IsLoadingConfiguration { get; set; }
    public void ApplyLocalConfiguration(LocalConfiguration local, string originalGoogle)
    {
        ConfigurationPath = local.Path;
        ConfigurationError = local.Error;
        if (GeminiKey == originalGoogle) GeminiKey = local.GoogleKey;
        IsLoadingConfiguration = false;
    }
    public AppAppearance Appearance { get; set; } = AppAppearance.Dark;
    public bool RejectMusic { get; set; }
    public string GeminiKey { get; set; } = "";
    public double FontSize { get; set; } = 48;
    public int ReadingWordsPerMinute { get; set; } = 180;
    private int? _englishReadingWordsPerMinute;
    private int? _swahiliReadingWordsPerMinute;
    private int? _frenchReadingWordsPerMinute;
    public int? EnglishReadingWordsPerMinute
    {
        get => _englishReadingWordsPerMinute;
        set => _englishReadingWordsPerMinute = ValidReadingSpeedOverride(value);
    }
    public int? SwahiliReadingWordsPerMinute
    {
        get => _swahiliReadingWordsPerMinute;
        set => _swahiliReadingWordsPerMinute = ValidReadingSpeedOverride(value);
    }
    public int? FrenchReadingWordsPerMinute
    {
        get => _frenchReadingWordsPerMinute;
        set => _frenchReadingWordsPerMinute = ValidReadingSpeedOverride(value);
    }
    public bool AutomaticCaptionCatchUp { get; set; } = true;
    public int ReadingSpeedFor(string? language) => (SpeechLanguageAssignment.Normalize(language) switch
    {
        "en" => EnglishReadingWordsPerMinute,
        "sw" => SwahiliReadingWordsPerMinute,
        "fr" => FrenchReadingWordsPerMinute,
        _ => null
    }) ?? ReadingWordsPerMinute;

    internal static int? ValidReadingSpeedOverride(int? value) => value is >= 80 and <= 300 ? value : null;
    public string AudioDeviceId { get; set; } = "";
    public CaptionSettings Copy() => (CaptionSettings)MemberwiseClone();
}
