using System.Text.RegularExpressions;

namespace CapFast.Core;

// Text is laid out immediately; only paint opacity changes as words arrive.
public sealed class CaptionWordReveal
{
    public sealed record Word(string Text, double StartsAt)
    {
        public double Opacity(double nowSeconds) => Math.Clamp((nowSeconds - StartsAt) / .12, 0, 1);
    }
    private double _nextWordAt;
    private List<IReadOnlyList<Word>> _rows = new();
    public IReadOnlyList<IReadOnlyList<Word>> Rows => _rows;
    public bool IsAnimating(double nowSeconds) => _rows.Any(row => row.Any(word => word.Opacity(nowSeconds) < 1));

    public void Update(IReadOnlyList<string> lines, double nowSeconds, bool revealNewWords = true)
    {
        var next = new List<IReadOnlyList<Word>>();
        for (var row = 0; row < lines.Count; row++)
        {
            var prior = row < _rows.Count ? _rows[row] : Array.Empty<Word>();
            var words = new List<Word>();
            var common = true;
            foreach (Match token in Regex.Matches(lines[row], @"\S+\s*|\s+"))
            {
                var index = words.Count;
                common = common && index < prior.Count && prior[index].Text.TrimEnd() == token.Value.TrimEnd();
                if (common && revealNewWords) words.Add(prior[index] with { Text = token.Value });
                else if (!revealNewWords || string.IsNullOrWhiteSpace(token.Value))
                    words.Add(new(token.Value, double.NegativeInfinity));
                else
                {
                    var start = Math.Max(nowSeconds, _nextWordAt);
                    words.Add(new(token.Value, start));
                    _nextWordAt = start + .1; // Ten words per second across an arriving batch.
                }
            }
            next.Add(words);
        }
        _rows = next;
        if (!revealNewWords) _nextWordAt = nowSeconds;
    }

    public void RemoveLeadingRows(int count) => _rows = _rows.Skip(count).ToList();
    public void Clear() { _rows.Clear(); _nextWordAt = 0; }
}
