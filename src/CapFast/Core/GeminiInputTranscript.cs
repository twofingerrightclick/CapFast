using System.Text.Json;

namespace CapFast.Core;

// Transcribe Live sends replacing interim hypotheses and authoritative final segments.
// These are different from Live Translate's output-transcription deltas.
public sealed class GeminiInputTranscript
{
    private readonly string _session = Guid.NewGuid().ToString("N");
    private long _utterance;
    private readonly HashSet<string> _offsets = new();
    private readonly Queue<string> _recentOffsets = new();

    public IReadOnlyList<CaptionUpdate> Apply(JsonElement message)
    {
        var updates = new List<CaptionUpdate>(2);
        if (!message.TryGetProperty("serverContent", out var content)) return updates;
        if (content.TryGetProperty("interimInputTranscription", out var interim) && ReadText(interim) is { } partial)
            updates.Add(new(Id, partial, false));
        if (content.TryGetProperty("inputTranscription", out var final) && ReadText(final) is { } text)
        {
            if (final.TryGetProperty("startOffset", out var start) && final.TryGetProperty("endOffset", out var end))
            {
                var offset = start.GetRawText() + ":" + end.GetRawText();
                if (!_offsets.Add(offset)) return updates;
                _recentOffsets.Enqueue(offset);
                if (_recentOffsets.Count > 256) _offsets.Remove(_recentOffsets.Dequeue());
            }
            updates.Add(new(Id, text, true));
            _utterance++;
        }
        return updates;
    }

    private string Id => $"google-input:{_session}:{_utterance}";
    private static string? ReadText(JsonElement value)
    {
        if (!value.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) return null;
        var result = text.GetString();
        if (string.IsNullOrWhiteSpace(result)) return null;
        if (result.Length > 16384) throw new InvalidDataException("Transcription segment exceeded the limit.");
        return result.Trim();
    }
}
