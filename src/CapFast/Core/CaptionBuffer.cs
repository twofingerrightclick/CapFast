namespace CapFast.Core;

public sealed record CaptionUpdate(string UtteranceId, string Text, bool IsFinal);

// Partial translations replace a hypothesis. They are never appended as new words.
public sealed class CaptionBuffer
{
    private readonly Queue<string> _finals = new();
    private readonly HashSet<string> _committedIds = new();
    private readonly Queue<string> _recentIds = new();
    private string _partial = "";
    private string? _partialId;

    public string Text { get; private set; } = "";
    // Text is a bounded history preview. Presentation consumes the accepted
    // original update so long phrases are not truncated to that preview tail.
    public CaptionUpdate? LastAcceptedUpdate { get; private set; }

    public bool Apply(CaptionUpdate update)
    {
        LastAcceptedUpdate = null;
        if (_committedIds.Contains(update.UtteranceId)) return false;
        if (!update.IsFinal && string.IsNullOrWhiteSpace(update.Text)) return false;
        LastAcceptedUpdate = update;
        if (update.IsFinal)
        {
            _committedIds.Add(update.UtteranceId);
            _recentIds.Enqueue(update.UtteranceId);
            if (_recentIds.Count > 256) _committedIds.Remove(_recentIds.Dequeue());
            if (!string.IsNullOrWhiteSpace(update.Text)) _finals.Enqueue(update.Text.Trim());
            while (_finals.Count > 8) _finals.Dequeue();
            if (_partialId == update.UtteranceId)
            {
                _partial = "";
                _partialId = null;
            }
        }
        else
        {
            _partialId = update.UtteranceId;
            _partial = update.Text.Trim();
        }

        var next = Tail(string.Join(" ", _finals.Append(_partial)), 42);
        if (next == Text) return false;
        Text = next;
        return true;
    }

    public void Clear()
    {
        _finals.Clear();
        _partial = "";
        _partialId = null;
        Text = "";
        LastAcceptedUpdate = null;
    }

    public static string Tail(string text, int wordLimit)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var tail = string.Join(" ", words.TakeLast(wordLimit));
        var elements = System.Globalization.StringInfo.ParseCombiningCharacters(tail);
        // Also bound pathological unbroken tokens before measuring the UI text.
        return elements.Length <= 512 ? tail : tail[elements[^512]..];
    }
}
