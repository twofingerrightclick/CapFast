namespace CapFast.Core;

public enum CaptionState { Paused, Starting, Live, Pausing }
public enum SpeechProvider { Google, Azure }
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
        AzureKey = local.Get("AZURE_SPEECH_KEY");
        AzureRegion = local.Get("AZURE_SPEECH_REGION");
    }
    public string ConfigurationPath { get; private set; }
    public string? ConfigurationError { get; set; }
    public bool IsLoadingConfiguration { get; set; }
    public void ApplyLocalConfiguration(LocalConfiguration local, string originalGoogle, string originalAzure, string originalRegion)
    {
        ConfigurationPath = local.Path;
        ConfigurationError = local.Error;
        if (GeminiKey == originalGoogle) GeminiKey = local.GoogleKey;
        if (AzureKey == originalAzure) AzureKey = local.Get("AZURE_SPEECH_KEY");
        if (AzureRegion == originalRegion) AzureRegion = local.Get("AZURE_SPEECH_REGION");
        IsLoadingConfiguration = false;
    }
    public SpeechProvider Provider { get; set; } = SpeechProvider.Google;
    public GoogleCaptionMode GoogleMode { get; set; } = GoogleCaptionMode.TwoStage;
    public string GeminiKey { get; set; } = "";
    public string AzureKey { get; set; } = "";
    public string AzureRegion { get; set; } = "";
    public string Locale { get; set; } = "sw-KE";
    public bool Stabilize { get; set; } = true;
    public double FontSize { get; set; } = 48;
    public int ReadingWordsPerMinute { get; set; } = 180;
    public string AudioDeviceId { get; set; } = "";
    public CaptionSettings Copy() => (CaptionSettings)MemberwiseClone();
}
