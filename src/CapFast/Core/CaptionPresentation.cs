using System.Globalization;

namespace CapFast.Core;

public sealed record CaptionPresentationFrame(IReadOnlyList<string> Lines, int ScrollRows, TimeSpan HoldFor,
    IReadOnlyList<string> AnimationLines);

// UI-independent presentation clock. Accepted phrases retain their source text
// until every chunk has actually been presented, even across resize and pause.
public sealed class CaptionPresentation
{
    private sealed class Phrase(CaptionUpdate update)
    {
        public string Id = update.UtteranceId;
        public string Text = update.Text;
        public bool Final = update.IsFinal;
        public int Offset;
        public bool Queued;
        public bool ReviseTail;
        public int ReplayEnd;
        public List<Line>? CachedIncoming;
    }
    private sealed record Line(Phrase Phrase, string Text, int Start, int End, string Source, double ReadyAt = 0);
    private sealed record Pending(Phrase Phrase, int End, List<Line> Previous, List<Line> Incoming, TimeSpan Hold, bool Replay);
    private readonly LinkedList<Phrase> _queue = new();
    private readonly Dictionary<string, Phrase> _phrases = new();
    private readonly HashSet<string> _finished = new();
    private readonly Queue<string> _finishedOrder = new();
    private List<Line> _visible = new();
    private Pending? _pending;
    private double _readingCursor;
    private double _width = 1;
    private int _rows = 1;
    private string _geometry = "";
    private long _replaySequence;
    private Func<string, double> _measure = text => text.Length;
    public int Generation { get; private set; }
    public int PendingPhrases => _queue.Count;
    public IReadOnlyList<string> CurrentLines => _visible.Select(line => line.Text).ToArray();
    public int ReadingWordsPerMinute { get; set; } = 180;
    public bool HasPendingPresentation => _pending is not null;
    public bool CanAcceptCaptions => _queue.Count(item => UnreadCaptureLength(item) > 0) <= 32 &&
        _queue.Sum(UnreadCaptureLength) <= 131072;
    private static int UnreadCaptureLength(Phrase phrase) => Math.Max(0, phrase.Text.Length - Math.Max(phrase.Offset, phrase.ReplayEnd));

    // False means the entire overflow phrase was retained, but capture must stop.
    public bool Submit(CaptionUpdate update)
    {
        if (_finished.Contains(update.UtteranceId)) return true;
        if (string.IsNullOrWhiteSpace(update.Text))
        {
            if (update.IsFinal && _phrases.TryGetValue(update.UtteranceId, out var withdrawn))
            {
                CancelPending(); _queue.Remove(withdrawn); _phrases.Remove(withdrawn.Id);
                if (_visible.LastOrDefault()?.Phrase == withdrawn) _visible.RemoveAt(_visible.Count - 1);
                _finished.Add(withdrawn.Id); _finishedOrder.Enqueue(withdrawn.Id);
                while (_finishedOrder.Count > 256) _finished.Remove(_finishedOrder.Dequeue());
                Generation++;
            }
            return CanAcceptCaptions;
        }
        if (_phrases.TryGetValue(update.UtteranceId, out var phrase))
        {
            if (phrase.Text == update.Text && phrase.Final == update.IsFinal) return true;
            CancelPending();
            if (_visible.LastOrDefault()?.Phrase == phrase)
            {
                phrase.Offset = _visible[^1].Start;
                phrase.ReviseTail = true;
            }
            phrase.Text = update.Text; phrase.Final = update.IsFinal;
            phrase.CachedIncoming = null;
            phrase.Offset = Boundary(phrase.Text, Math.Min(phrase.Offset, phrase.Text.Length));
            phrase.ReplayEnd = Boundary(phrase.Text, Math.Min(phrase.ReplayEnd, phrase.Text.Length));
            if (!phrase.Queued) { _queue.AddFirst(phrase); phrase.Queued = true; }
        }
        else
        {
            // The controller stops capture synchronously on the first overflow.
            // A further event is explicitly rejected rather than silently lost.
            if (_queue.Count(item => UnreadCaptureLength(item) > 0) >= 33) throw new InvalidOperationException("Caption display backlog is full.");
            foreach (var stale in _phrases.Values.Where(item => !item.Queued && item != _visible.LastOrDefault()?.Phrase).ToArray())
                _phrases.Remove(stale.Id);
            phrase = new(update); _phrases.Add(phrase.Id, phrase);
            _queue.AddLast(phrase); phrase.Queued = true;
        }
        return CanAcceptCaptions;
    }

    public void Configure(double width, int rows, Func<string, double> measure, string geometry, double nowSeconds = 0)
    {
        _measure = measure;
        if (_width == width && _rows == rows && _geometry == geometry) return;
        CancelPending(); Generation++;
        foreach (var queued in _queue) queued.CachedIncoming = null;
        _width = Math.Max(1, width); _rows = Math.Max(1, rows); _geometry = geometry;
        var reflowed = new List<Line>();
        foreach (var group in _visible.GroupBy(line => (line.Phrase, line.Source)))
        {
            var start = group.Min(line => line.Start);
            var end = group.Max(line => line.End);
            if (start < end) reflowed.AddRange(Wrap(group.Key.Phrase, start, end, group.Key.Source)
                .Select(line => line with { ReadyAt = group.Max(old => old.ReadyAt) }));
        }
        if (reflowed.Count > _rows && _visible.Any(line => line.ReadyAt > nowSeconds))
        {
            // Restart unread visible spans from their opening. Smaller geometry
            // must never turn the beginning of an unread phrase into a tail.
            var unread = _visible.Where(line => line.ReadyAt > nowSeconds).GroupBy(line => (line.Phrase, line.Source)).ToArray();
            foreach (var group in unread.Reverse())
            {
                var phrase = group.Key.Phrase;
                var end = group.Max(line => line.End);
                if (phrase.Text != group.Key.Source)
                    phrase = new(new CaptionUpdate($"display-replay-{++_replaySequence}", group.Key.Source[..end], true));
                if (phrase.Queued) _queue.Remove(phrase);
                phrase.Offset = group.Min(line => line.Start); phrase.ReviseTail = false;
                phrase.CachedIncoming = null;
                phrase.ReplayEnd = Math.Max(phrase.ReplayEnd, end);
                _queue.AddFirst(phrase); phrase.Queued = true;
                _phrases[phrase.Id] = phrase;
            }
            var unreadSpans = unread.Select(group => group.Key).ToHashSet();
            reflowed.RemoveAll(line => unreadSpans.Contains((line.Phrase, line.Source)));
        }
        // Read context can be trimmed; queued original spans remain intact.
        _visible = reflowed.TakeLast(_rows).ToList();
    }

    public bool TryPresent(double nowSeconds, out CaptionPresentationFrame? frame)
    {
        frame = null;
        if (_pending is not null) return false;
        while (_queue.First is { } node)
        {
            var phrase = node.Value;
            var replay = phrase.Offset < phrase.ReplayEnd;
            var incoming = phrase.CachedIncoming ??= Wrap(phrase, phrase.Offset,
                replay ? phrase.ReplayEnd : phrase.Text.Length).Take(_rows).ToList();
            if (incoming.Count == 0) { FinishChunk(phrase, phrase.Text.Length); continue; }
            var before = _visible;
            var kept = phrase.ReviseTail && before.LastOrDefault()?.Phrase == phrase
                ? before.Take(before.Count - 1).ToList() : before.ToList();
            var scroll = Math.Max(0, kept.Count + incoming.Count - _rows);
            if (scroll > 0 && kept.Take(scroll).Any(line => line.ReadyAt > nowSeconds)) return false;
            var animationLines = kept.Concat(incoming).Select(line => line.Text).ToArray();
            kept = kept.Skip(scroll).ToList(); kept.AddRange(incoming);
            var exposed = string.Join(" ", incoming.Select(line => line.Text));
            if (phrase.ReviseTail && before.LastOrDefault()?.Phrase == phrase)
            {
                var prior = before[^1].Text;
                var common = 0;
                while (common < exposed.Length && common < prior.Length && exposed[common] == prior[common]) common++;
                exposed = exposed[common..];
            }
            var words = exposed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            var units = Math.Max(words, StringInfo.ParseCombiningCharacters(exposed).Length / 6d);
            var seconds = units * 60d / Math.Clamp(ReadingWordsPerMinute, 80, 300);
            var hold = TimeSpan.FromSeconds(units == 0 ? 0 : phrase.ReviseTail ? seconds : Math.Max(1.5, seconds));
            _pending = new(phrase, incoming[^1].End, before, incoming, hold, replay);
            _visible = kept;
            frame = new(CurrentLines, scroll, hold, animationLines);
            return true;
        }
        return false;
    }

    // Start reading time after the frame is fully visible, not at service receipt.
    public void Presented(double nowSeconds)
    {
        if (_pending is not { } pending) return;
        _pending = null;
        var deadline = pending.Replay
            ? nowSeconds + pending.Hold.TotalSeconds
            : Math.Max(_readingCursor, nowSeconds) + pending.Hold.TotalSeconds;
        if (!pending.Replay) _readingCursor = deadline;
        _visible = _visible.Select(line => line.Phrase == pending.Phrase ? line with { ReadyAt = deadline } : line).ToList();
        pending.Phrase.ReviseTail = false;
        FinishChunk(pending.Phrase, pending.End);
    }

    private void FinishChunk(Phrase phrase, int end)
    {
        phrase.Offset = end;
        phrase.CachedIncoming = null;
        if (end < phrase.Text.Length) return;
        if (phrase.Queued) { _queue.Remove(phrase); phrase.Queued = false; }
        if (!phrase.Final) return;
        _phrases.Remove(phrase.Id);
        _finished.Add(phrase.Id); _finishedOrder.Enqueue(phrase.Id);
        while (_finishedOrder.Count > 256) _finished.Remove(_finishedOrder.Dequeue());
        // Only a current partial needs to remain revisable.
        foreach (var stale in _phrases.Values.Where(item => !item.Queued && item != phrase).ToArray()) _phrases.Remove(stale.Id);
    }

    private void CancelPending()
    {
        if (_pending is not { } pending) return;
        _visible = pending.Previous; _pending = null; Generation++;
    }

    private static int Boundary(string text, int offset)
    {
        if (offset == text.Length) return offset;
        var starts = StringInfo.ParseCombiningCharacters(text);
        return starts.LastOrDefault(start => start <= offset);
    }

    public void Clear()
    {
        _queue.Clear(); _phrases.Clear(); _finished.Clear(); _finishedOrder.Clear();
        _visible.Clear(); _pending = null; _readingCursor = 0; Generation++;
    }

    private IEnumerable<Line> Wrap(Phrase phrase, int offset, int end, string? source = null)
    {
        var text = source ?? phrase.Text;
        end = Math.Clamp(end, 0, text.Length);
        offset = Math.Clamp(offset, 0, end);
        while (offset < end)
        {
            while (offset < end && char.IsWhiteSpace(text[offset])) offset++;
            if (offset >= end) break;
            var start = offset; var cursor = offset; var lastBreak = -1;
            var elements = StringInfo.GetTextElementEnumerator(text, offset);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                var next = cursor + element.Length;
                if (next > end) break;
                if (cursor > start && _measure(text[start..next].TrimEnd()) > _width) break;
                cursor = next;
                if (char.IsWhiteSpace(element[0])) lastBreak = cursor;
                if (_measure(text[start..cursor].TrimEnd()) > _width) break; // one oversized grapheme
            }
            var stop = cursor < end && lastBreak > start ? lastBreak : cursor;
            if (stop <= start) stop = Math.Min(end, start + StringInfo.GetNextTextElement(text, start).Length);
            yield return new(phrase, text[start..stop].TrimEnd(), start, stop, text);
            offset = stop;
        }
    }
}
