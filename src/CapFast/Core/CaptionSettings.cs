namespace CapFast.Core;

public enum CaptionState { Paused, Starting, Live, Pausing }
public enum SpeechProvider { Google, Azure }
public sealed record AudioInput(string Id, string Name);

public sealed class CaptionSettings
{
    public CaptionSettings()
    {
        var local = new LocalConfiguration();
        ConfigurationPath = local.Path;
        ConfigurationError = local.Error;
        GeminiKey = local.GoogleKey;
        AzureKey = local.Get("AZURE_SPEECH_KEY");
        AzureRegion = local.Get("AZURE_SPEECH_REGION");
    }
    public string ConfigurationPath { get; }
    public string? ConfigurationError { get; }
    public SpeechProvider Provider { get; set; } = SpeechProvider.Google;
    public GoogleCaptionMode GoogleMode { get; set; } = GoogleCaptionMode.TwoStage;
    public string GeminiKey { get; set; } = "";
    public string AzureKey { get; set; } = "";
    public string AzureRegion { get; set; } = "";
    public string Locale { get; set; } = "sw-KE";
    public bool Stabilize { get; set; } = true;
    public double FontSize { get; set; } = 48;
    public string AudioDeviceId { get; set; } = "";
    public CaptionSettings Copy() => (CaptionSettings)MemberwiseClone();
}
