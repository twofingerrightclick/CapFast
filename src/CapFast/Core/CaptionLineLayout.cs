using System.Globalization;

namespace CapFast.Core;

// Completed rows are sealed. Only the active tail is revised; new rows scroll
// one row at a time through a fixed-height caption band.
public sealed class CaptionLineLayout
{
    private sealed record Part(long Word, string Text, bool Space);
    private readonly List<List<Part>> _lines = new();
    private string[] _previous = Array.Empty<string>();
    private long _previousStart;
    private string? _previousUtteranceId;
    private double _width;
    private int _rows;
    private string _layoutKey = "";
    private readonly List<IReadOnlyList<string>> _scrollFrames = new();
    public IReadOnlyList<IReadOnlyList<string>> ScrollFrames => _scrollFrames;

    public IReadOnlyList<string> Update(string text, double width, Func<string, double> measure,
        int rows = 3, string layoutKey = "", string? utteranceId = null)
    {
        rows = Math.Clamp(rows, 1, 3);
        width = Math.Max(1, width);
        _scrollFrames.Clear();
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            _lines.Clear(); _previous = words; _previousStart = 0; _previousUtteranceId = null;
            _width = width; _rows = rows; _layoutKey = layoutKey;
            return Array.Empty<string>();
        }
        if (_width != width || _rows != rows || _layoutKey != layoutKey)
        {
            var page = _lines.SelectMany(line => line).GroupBy(part => part.Word)
                .Select(group => (Word: group.Key, Text: string.Concat(group.Select(part => part.Text)))).ToArray();
            _lines.Clear();
            _width = width; _rows = rows; _layoutKey = layoutKey;
            foreach (var word in page) Append(word.Word, word.Text, measure);
        }
        if (_previous.SequenceEqual(words)) return Render();

        var bestOffset = 0;
        var common = 0;
        // Find the retained prefix after CaptionBuffer discards old head words.
        // A changed final word remains a replacement, rather than appended text.
        for (var offset = 0; offset < _previous.Length; offset++)
        {
            var count = 0;
            while (offset + count < _previous.Length && count < words.Length && _previous[offset + count] == words[count]) count++;
            if (count > common) { common = count; bestOffset = offset; }
        }
        var start = _previousStart + bestOffset;
        if (common == 0)
        {
            // Source identity distinguishes an entirely revised partial from a
            // new final segment which displaced the entire bounded text tail.
            start = utteranceId is not null && utteranceId != _previousUtteranceId
                ? _previousStart + _previous.Length : _previousStart;
        }
        var changedAt = start + common;
        if (_lines.Count > 0 && _lines[^1].Count > 0)
        {
            var activeStart = _lines[^1][0].Word;
            common = Math.Max(common, (int)Math.Clamp(activeStart - start, 0, words.Length));
            _lines[^1].RemoveAll(part => part.Word >= Math.Max(changedAt, activeStart));
        }
        for (var index = common; index < words.Length; index++) Append(start + index, words[index], measure);
        _previous = words;
        _previousStart = start;
        _previousUtteranceId = utteranceId;
        return Render();
    }

    private void Append(long index, string word, Func<string, double> measure)
    {
        if (_lines.Count == 0) NewLine();
        var line = _lines[^1];
        var prefix = RenderLine(line);
        if (measure(prefix + (prefix.Length > 0 ? " " : "") + word) <= _width)
        {
            line.Add(new(index, word, line.Count > 0));
            CaptureActiveFrame();
            return;
        }
        if (line.Count > 0) { NewLine(); line = _lines[^1]; }
        if (measure(word) <= _width) { line.Add(new(index, word, false)); CaptureActiveFrame(); return; }

        // Long unbroken tokens split only between Unicode text elements.
        var elements = StringInfo.GetTextElementEnumerator(word);
        var fragment = "";
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (fragment.Length > 0 && measure(fragment + element) > _width)
            {
                line.Add(new(index, fragment, false));
                CaptureActiveFrame();
                NewLine(); line = _lines[^1]; fragment = "";
            }
            fragment += element;
        }
        if (fragment.Length > 0) line.Add(new(index, fragment, false));
        CaptureActiveFrame();
    }

    private void NewLine()
    {
        var scrolling = _lines.Count >= _rows;
        if (scrolling)
        {
            if (_scrollFrames.Count == 0) _scrollFrames.Add(Render());
            _lines.RemoveAt(0);
        }
        _lines.Add(new());
        if (scrolling) _scrollFrames.Add(Render());
    }
    private void CaptureActiveFrame()
    {
        if (_scrollFrames.Count > 0) _scrollFrames[^1] = Render();
    }
    private IReadOnlyList<string> Render() => _lines.Select(RenderLine).ToArray();
    private static string RenderLine(List<Part> line) => string.Concat(line.Select(part => (part.Space ? " " : "") + part.Text));
}
