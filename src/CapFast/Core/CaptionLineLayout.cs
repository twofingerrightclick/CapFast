using System.Globalization;

namespace CapFast.Core;

// A caption page fills fixed rows from the top. Only a full page is replaced;
// adding another row never moves earlier rows upward.
public sealed class CaptionLineLayout
{
    private sealed record Part(long Word, string Text, bool Space);
    private readonly List<List<Part>> _lines = new();
    private string[] _previous = Array.Empty<string>();
    private long _previousStart;
    private double _width;
    private int _rows;
    private string _layoutKey = "";
    private long _pageGeneration;

    public IReadOnlyList<string> Update(string text, double width, Func<string, double> measure,
        int rows = 3, string layoutKey = "")
    {
        rows = Math.Clamp(rows, 1, 3);
        width = Math.Max(1, width);
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            _lines.Clear(); _previous = words; _previousStart = 0;
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
            _lines.Clear();
            start = _previousStart + _previous.Length;
        }
        var changedAt = start + common;
        foreach (var line in _lines) line.RemoveAll(part => part.Word >= changedAt);
        while (_lines.Count > 0 && _lines[^1].Count == 0) _lines.RemoveAt(_lines.Count - 1);
        // A shorter correction can retract the only words on the new page.
        // Restore the corrected preceding page instead of leaving a blank caption.
        if (_lines.Count == 0 && common == words.Length && common > 0) common = 0;
        var generation = _pageGeneration;
        for (var index = common; index < words.Length; index++) Append(start + index, words[index], measure);
        if (_pageGeneration != generation && common < words.Length)
        {
            // A service result arrives as one batch. Do not consume its opening
            // words into a completed page that never reaches the screen.
            var overflowPage = _lines.Select(line => line.ToList()).ToArray();
            _lines.Clear();
            generation = _pageGeneration;
            for (var index = common; index < words.Length; index++) Append(start + index, words[index], measure);
            if (_pageGeneration != generation)
            {
                // A batch larger than a whole page still uses the bounded tail.
                _lines.Clear();
                _lines.AddRange(overflowPage);
            }
        }
        _previous = words;
        _previousStart = start;
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
            return;
        }
        if (line.Count > 0) { NewLine(); line = _lines[^1]; }
        if (measure(word) <= _width) { line.Add(new(index, word, false)); return; }

        // Long unbroken tokens split only between Unicode text elements.
        var elements = StringInfo.GetTextElementEnumerator(word);
        var fragment = "";
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (fragment.Length > 0 && measure(fragment + element) > _width)
            {
                line.Add(new(index, fragment, false));
                NewLine(); line = _lines[^1]; fragment = "";
            }
            fragment += element;
        }
        if (fragment.Length > 0) line.Add(new(index, fragment, false));
    }

    private void NewLine()
    {
        if (_lines.Count >= _rows) { _lines.Clear(); _pageGeneration++; }
        _lines.Add(new());
    }
    private IReadOnlyList<string> Render() => _lines.Select(RenderLine).ToArray();
    private static string RenderLine(List<Part> line) => string.Concat(line.Select(part => (part.Space ? " " : "") + part.Text));
}
