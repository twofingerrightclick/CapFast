namespace CapFast.Core;

public sealed record LanguageAssignment(string Input, string Primary, string Secondary, long Epoch);

// Route every fragment by its source while the operator indicator follows
// sustained language evidence within the current speech block.
public sealed class SpeechLanguageAssignment
{
    private double? _lastSpeech;
    private string? _hint;
    private bool _audioObserved;
    private bool _speechAvailable = true;
    private long _epoch;
    private string? _candidateDominant;
    private int _candidateWords;
    private int _candidateChunks;
    public string? PendingHint => _hint;
    public long AudioGeneration { get; private set; }
    public LanguageAssignment? Current { get; private set; }
    public string? LatestInput { get; private set; }
    public event Action<LanguageAssignment?>? Changed;
    public static string? Normalize(string? code)
    {
        var primary = string.IsNullOrWhiteSpace(code) ? null : code.Trim().Split('-', '_')[0].ToLowerInvariant();
        return primary == "und" ? null : primary;
    }
    public static string Name(string code)
    {
        if (code is "en" or "sw" or "fr" or "auto") return code switch { "en" => "English", "sw" => "Swahili", "fr" => "French", _ => "mixed-language" };
        try { return System.Globalization.CultureInfo.GetCultureInfo(code).EnglishName; }
        catch (System.Globalization.CultureNotFoundException) { return code.ToUpperInvariant(); }
    }
    public void Reset()
    {
        Current = null; LatestInput = null; _hint = null; ++_epoch;
        _candidateDominant = null; _candidateWords = 0; _candidateChunks = 0;
        Changed?.Invoke(null);
    }
    public void BeginFreshAudioGeneration() { Reset(); ++AudioGeneration; _speechAvailable = false; }
    public void Hint(string language) { Reset(); _hint = Normalize(language); }
    public void ObserveAudio(AudioActivity activity, double now)
    {
        _audioObserved = true;
        if (activity == AudioActivity.Speech) { _lastSpeech = now; _speechAvailable = true; return; }
        if (_speechAvailable && _lastSpeech is { } last && now - last >= 5)
        { Reset(); _speechAvailable = false; ++AudioGeneration; }
        else if (_lastSpeech is null) _speechAvailable = false;
    }
    public LanguageAssignment? Accept(string? detected, double now, int evidenceWords = 0)
    {
        var language = Normalize(detected);
        if ((language is null && _hint is null) || (_audioObserved && !_speechAvailable)) return null;
        // Receipt time must not extend a block when local audio provides timing.
        if (!_audioObserved) _lastSpeech = now;
        if (Current is null)
        {
            var hinted = _hint is not null;
            var input = _hint ?? language!; _hint = null;
            if (hinted) language = input;
            Current = ForSource(input, ++_epoch); Changed?.Invoke(Current);
        }
        else if (Current.Input == "auto" && language is not null && language != "auto")
        {
            Current = ForSource(language, Current.Epoch); Changed?.Invoke(Current);
        }
        else if (language is "en" or "sw" or "fr" && evidenceWords > 0)
        {
            if (language == Current.Input && evidenceWords >= 4)
            {
                _candidateDominant = null; _candidateWords = 0; _candidateChunks = 0;
            }
            else if (language != Current.Input && evidenceWords >= 2)
            {
                if (_candidateDominant != language)
                {
                    _candidateDominant = language; _candidateWords = 0; _candidateChunks = 0;
                }
                _candidateWords += Math.Min(evidenceWords, 18);
                ++_candidateChunks;
                if (_candidateChunks >= 2 && _candidateWords >= 8)
                {
                    Current = ForSource(language, Current.Epoch); Changed?.Invoke(Current);
                    _candidateDominant = null; _candidateWords = 0; _candidateChunks = 0;
                }
            }
        }
        LatestInput = language;
        return language == Current.Input ? Current : ForSource(language!, Current.Epoch);
    }
    public static LanguageAssignment ForSource(string input, long epoch)
    {
        return input switch
        {
            "en" => new(input, "sw", "fr", epoch),
            "sw" => new(input, "en", "fr", epoch),
            "fr" => new(input, "en", "sw", epoch),
            _ => new(input, "en", "fr", epoch)
        };
    }
}
