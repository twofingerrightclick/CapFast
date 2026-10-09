using System.Text;
using System.Text.Json;

namespace CapFast.Core;

// Live outputTranscription.text contains deltas, not replacement hypotheses.
// Repeated completion events finalize once; repeated text deltas remain meaningful.
public sealed class GeminiTranscript
{
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly StringBuilder _text = new();
    private long _utterance;

    public IReadOnlyList<CaptionUpdate> Apply(JsonElement message)
    {
        var updates = new List<CaptionUpdate>(2);
        if (!message.TryGetProperty("serverContent", out var content)) return updates;
        var finished = false;
        if (content.TryGetProperty("outputTranscription", out var transcript))
        {
            if (transcript.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                _text.Append(text.GetString());
                // Bound memory even if an abnormal stream never sends a completion.
                if (_text.Length > 16384)
                {
                    var remove = _text.Length - 16384;
                    if (char.IsLowSurrogate(_text[remove])) remove++;
                    _text.Remove(0, remove);
                }
                if (_text.Length > 0) updates.Add(new(Id, _text.ToString(), false));
            }
            finished = IsTrue(transcript, "finished");
        }
        if (finished || IsTrue(content, "generationComplete") || IsTrue(content, "turnComplete") || IsTrue(content, "interrupted"))
        {
            if (_text.Length > 0)
            {
                updates.Add(new(Id, _text.ToString(), true));
                _text.Clear();
                _utterance++;
            }
        }
        return updates;
    }

    private string Id => $"gemini:{_session}:{_utterance}";
    private static bool IsTrue(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
}
