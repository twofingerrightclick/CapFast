namespace CapFast.Core;

// Called serially by the SDK adapter. Timing offsets are deliberately not identities:
// Azure may revise them between interim and final recognition.
public sealed class CaptionSequence
{
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly HashSet<string> _completed = new();
    private readonly Queue<string> _recent = new();
    private long _utterance;

    public CaptionUpdate Partial(string text) => new($"{_sessionId}:{_utterance}", text, false);

    public CaptionUpdate? Complete(string resultId, string text)
    {
        if (!_completed.Add(resultId)) return null;
        _recent.Enqueue(resultId);
        if (_recent.Count > 256) _completed.Remove(_recent.Dequeue());
        return new CaptionUpdate($"{_sessionId}:{_utterance++}", text, true);
    }
}
