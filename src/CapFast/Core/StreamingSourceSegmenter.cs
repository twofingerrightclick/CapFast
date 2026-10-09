using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CapFast.Core;

// Server interims are replacing snapshots, not append-only deltas. Only agreed
// whole words leave this component; submitted words are never translated twice.
public sealed class StreamingSourceSegmenter
{
    private sealed record Word(string Text, string Key, bool SentenceEnd);
    private readonly HashSet<string> _closed = new(StringComparer.Ordinal);
    private readonly Queue<string> _recentClosed = new();
    private readonly List<Word> _consumed = new();
    private List<Word>? _previous;
    private string? _id;
    private int _chunk;
    private double? _pendingSince;
    public bool LastAlignmentWasApproximate { get; private set; }

    public IReadOnlyList<CaptionUpdate> Apply(CaptionUpdate update, double nowSeconds)
    {
        if (!double.IsFinite(nowSeconds)) throw new ArgumentOutOfRangeException(nameof(nowSeconds));
        if (update.Text.Length > 16384) throw new InvalidDataException("Transcription segment exceeded the limit.");
        LastAlignmentWasApproximate = false;
        if (_closed.Contains(update.UtteranceId)) return Array.Empty<CaptionUpdate>();
        if (_id != update.UtteranceId)
        {
            if (_id is not null) Close(_id);
            Reset(update.UtteranceId);
        }
        var words = Tokenize(update.Text);
        var start = Align(words);
        var result = new List<CaptionUpdate>();
        if (update.IsFinal)
        {
            while (start < words.Count)
            {
                var count = Math.Min(18, words.Count - start);
                var end = words.FindIndex(start, count, word => word.SentenceEnd);
                if (end >= 0) count = end - start + 1;
                Emit(words, start, count, result);
                start += count;
            }
            Close(update.UtteranceId);
            Reset(null);
            return result;
        }
        if (start < words.Count) _pendingSince ??= nowSeconds;
        if (_previous is not null)
        {
            var previousStart = Align(_previous);
            var stable = 0;
            while (start + stable < words.Count && previousStart + stable < _previous.Count &&
                words[start + stable].Key == _previous[previousStart + stable].Key) stable++;
            // Punctuation itself must agree, too. Newly guessed punctuation alone
            // does not turn an unconfirmed clause into a committed sentence.
            var initialStart = start;
            var sentenceEnd = -1;
            for (var offset = 0; offset < stable; offset++)
            {
                if (!words[initialStart + offset].SentenceEnd || !_previous[previousStart + offset].SentenceEnd) continue;
                sentenceEnd = initialStart + offset;
                while (start <= sentenceEnd)
                {
                    var count = Math.Min(18, sentenceEnd - start + 1);
                    Emit(words, start, count, result);
                    start += count;
                }
            }
            if (sentenceEnd >= 0) _pendingSince = start < words.Count ? nowSeconds : null;
            else if (_pendingSince is { } since && nowSeconds - since >= 3 && stable - 3 >= 6)
            {
                Emit(words, start, Math.Min(18, stable - 3), result);
                _pendingSince = nowSeconds;
            }
        }
        _previous = words;
        return result;
    }

    private void Emit(List<Word> words, int start, int count, List<CaptionUpdate> result)
    {
        result.Add(new($"{_id}:stream-chunk:{_chunk++}",
            string.Join(" ", words.Skip(start).Take(count).Select(word => word.Text)), true));
        // Keep the current authoritative spelling of the prefix in the ledger,
        // including small revisions to already submitted source words.
        _consumed.Clear();
        _consumed.AddRange(words.Take(start + count));
    }

    private int Align(List<Word> words)
    {
        if (_consumed.Count == 0) return 0;
        var common = 0;
        while (common < _consumed.Count && common < words.Count && _consumed[common].Key == words[common].Key) common++;
        if (common == _consumed.Count) return common;
        if (common == words.Count) return words.Count; // A temporarily shorter snapshot has no new suffix.

        // Banded token edit distance aligns a small correction/insertion/deletion
        // in the submitted prefix without an unbounded N-by-N matrix. Prefer an
        // exact-length endpoint on ties so repeated words do not consume a new one.
        const int limit = 4;
        var n = _consumed.Count - common;
        var m = Math.Min(words.Count - common, n + limit);
        const int unreachable = 1000000;
        var prior = new int[m + 1];
        var current = new int[m + 1];
        Array.Fill(prior, unreachable);
        Array.Fill(current, unreachable);
        for (var j = 0; j <= Math.Min(m, limit); j++) prior[j] = j;
        for (var i = 1; i <= n; i++)
        {
            var clearStart = Math.Max(0, i - limit - 2);
            var clearEnd = Math.Min(m, i + limit);
            if (clearEnd >= clearStart) Array.Fill(current, unreachable, clearStart, clearEnd - clearStart + 1);
            if (i <= limit) current[0] = i;
            for (var j = Math.Max(1, i - limit); j <= Math.Min(m, i + limit); j++)
                current[j] = Math.Min(prior[j] + 1, Math.Min(current[j - 1] + 1,
                    prior[j - 1] + (_consumed[common + i - 1].Key == words[common + j - 1].Key ? 0 : 1)));
            (prior, current) = (current, prior);
        }
        var best = -1;
        var cost = limit + 1;
        for (var j = Math.Max(0, n - limit); j <= m; j++)
            if (prior[j] < cost || (prior[j] == cost && best >= 0 && Math.Abs(j - n) < Math.Abs(best - n)))
            { best = j; cost = prior[j]; }
        if (best >= 0 && cost <= limit) return common + best;
        // Major rewrites cannot repair already displayed English. Preserve the
        // submitted position rather than replaying it as a fresh translation.
        LastAlignmentWasApproximate = true;
        return Math.Min(_consumed.Count, words.Count);
    }

    private static List<Word> Tokenize(string text)
    {
        return Regex.Matches(text, @"\S+").Select(match =>
        {
            var value = match.Value;
            var key = new StringBuilder();
            foreach (var rune in value.Normalize(NormalizationForm.FormC).EnumerateRunes())
            {
                var category = Rune.GetUnicodeCategory(rune);
                if (Rune.IsLetterOrDigit(rune) || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
                    key.Append(Rune.ToLowerInvariant(rune));
            }
            var normalized = key.Length == 0 ? value.ToLowerInvariant() : key.ToString();
            var tail = value.TrimEnd('"', '\'', '”', '’', ')', ']', '»');
            return new Word(value, normalized, tail.Length > 0 && tail[^1] is '.' or '!' or '?');
        }).ToList();
    }

    private void Close(string id)
    {
        if (!_closed.Add(id)) return;
        _recentClosed.Enqueue(id);
        if (_recentClosed.Count > 256) _closed.Remove(_recentClosed.Dequeue());
    }

    private void Reset(string? id)
    {
        _id = id; _chunk = 0; _previous = null; _consumed.Clear(); _pendingSince = null;
    }
}
