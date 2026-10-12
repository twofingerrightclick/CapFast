namespace CapFast.Core;

// Live currently emits text-only events. Detect once from a substantial interim
// or final, then reuse within that utterance while its detected prefix survives.
// A replaced interim or final gets a fresh check. Provider metadata takes priority.
public sealed class TranscriptLanguageResolver
{
    private readonly Dictionary<string, string?> _languages = new();
    private readonly Dictionary<string, string> _samples = new();
    private readonly HashSet<string> _finalAttempts = new();
    private readonly Queue<string> _recent = new();
    public async Task<CaptionUpdate?> ResolveAsync(CaptionUpdate update,
        Func<string, CancellationToken, Task<string?>> detect, CancellationToken token, string? inputHint = null)
    {
        var language = SpeechLanguageAssignment.Normalize(inputHint ?? update.SourceLanguage);
        if (language is not null) Remember(update.UtteranceId, language);
        else if (!_languages.TryGetValue(update.UtteranceId, out language) ||
            (update.IsFinal && language is null && !_finalAttempts.Contains(update.UtteranceId)) ||
            (_samples.TryGetValue(update.UtteranceId, out var sample) &&
                !update.Text.StartsWith(sample, StringComparison.OrdinalIgnoreCase) &&
                (!update.IsFinal || !_finalAttempts.Contains(update.UtteranceId))))
        {
            if (!update.IsFinal && update.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length < 5) return null;
            // Limit the classification sample; source text remains intact for translation.
            if (update.IsFinal) _finalAttempts.Add(update.UtteranceId);
            var detectionSample = update.Text[..Math.Min(update.Text.Length, 512)];
            language = SpeechLanguageAssignment.Normalize(await detect(detectionSample, token));
            token.ThrowIfCancellationRequested();
            Remember(update.UtteranceId, language);
            _samples[update.UtteranceId] = detectionSample;
        }
        return language is null ? null : update with { SourceLanguage = language };
    }
    private void Remember(string id, string? language)
    {
        if (!_languages.ContainsKey(id))
        {
            _recent.Enqueue(id);
            if (_recent.Count > 256) { var old = _recent.Dequeue(); _languages.Remove(old); _finalAttempts.Remove(old); _samples.Remove(old); }
        }
        _languages[id] = language;
    }
}
