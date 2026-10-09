using CapFast.Core;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;

namespace CapFast.Speech;

public sealed class AzureCaptionSession : ICaptionSession
{
    private readonly AudioConfig _audio;
    private readonly TranslationRecognizer _recognizer;
    private readonly object _gate = new();
    private readonly CaptionSequence _sequence = new();
    private bool _disposed;

    public event Action<CaptionUpdate>? Caption;
    public event Action<string>? Status;
    public event Action<string>? Ended;

    public AzureCaptionSession(string key, string region, string locale, bool stabilize, string? audioDeviceId = null)
    {
        var config = SpeechTranslationConfig.FromSubscription(key.Trim(), region.Trim());
        config.SpeechRecognitionLanguage = locale;
        config.AddTargetLanguage("en");
        config.SetProperty(PropertyId.SpeechServiceResponse_TranslationRequestStablePartialResult,
            stabilize ? "true" : "false");
        // Microsoft's macOS device-selection guide specifies the CoreAudio device UID.
        _audio = string.IsNullOrWhiteSpace(audioDeviceId)
            ? AudioConfig.FromDefaultMicrophoneInput()
            : AudioConfig.FromMicrophoneInput(audioDeviceId);
        try { _recognizer = new TranslationRecognizer(config, _audio); }
        catch { _audio.Dispose(); throw; }
        _recognizer.Recognizing += (_, e) => Publish(e.Result, false);
        _recognizer.Recognized += (_, e) =>
        {
            Publish(e.Result, true);
            if (e.Result.Reason == ResultReason.NoMatch) Status?.Invoke("No speech recognized. Check the audio input.");
        };
        _recognizer.Canceled += (_, e) => Ended?.Invoke(
            e.Reason == CancellationReason.Error
                ? $"Azure stopped ({e.ErrorCode}). Check credentials, connection, and microphone access; then start again."
                : "Translation canceled. Start again to resume.");
        _recognizer.SessionStopped += (_, _) => Ended?.Invoke("Translation session ended. Start again to resume.");
    }

    private void Publish(TranslationRecognitionResult result, bool final)
    {
        lock (_gate)
        {
            if (_disposed) return;
            result.Translations.TryGetValue("en", out var text);
            var update = final ? _sequence.Complete(result.ResultId, text ?? "") : _sequence.Partial(text ?? "");
            if (update is not null) Caption?.Invoke(update);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Speech SDK startup is not cancellable; finish it before stopping native resources.
        await _recognizer.StartContinuousRecognitionAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { await _recognizer.StopContinuousRecognitionAsync(); }
        finally { _recognizer.Dispose(); _audio.Dispose(); }
    }
}
