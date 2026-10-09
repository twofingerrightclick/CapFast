using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CapFast.Core;

/// <summary>A local display-only blacklist. It does not alter translation context or call a service.</summary>
public sealed class EnglishCaptionFilter
{
    private const string ResourceName = "CapFast.EnglishCaptionBlacklist.txt";
    private static readonly Lazy<EnglishCaptionFilter> Instance = new(LoadDefault);
    private readonly Node _root = new();
    private readonly List<string> _symbols = new();
    private static readonly Regex Whitespace = Pattern(@"\s+");
    private static readonly Regex BeforePunctuation = Pattern(@" +([,.;:!?%\)\]\}])");
    private static readonly Regex AfterOpening = Pattern(@"([\(\[\{]) +");
    private static readonly Regex EmptyPairs = Pattern("\\( *\\)|\\[ *\\]|\\{ *\\}|\" *\"|' *'|“ *”|‘ *’");
    private static readonly Regex RepeatedCommas = Pattern(@",[ ,]*,");
    private static readonly Regex LeadingPunctuation = Pattern(@"^[.,;:!?]+ *");

    public static EnglishCaptionFilter Default => Instance.Value;

    public EnglishCaptionFilter(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var raw in entries)
        {
            var entry = raw.Trim();
            if (entry.Length == 0 || entry.StartsWith('#')) continue;
            var tokens = Tokenize(entry);
            if (tokens.Count == 0) { _symbols.Add(entry); continue; }
            var node = _root;
            for (var index = 0; index < tokens.Count; index++)
            {
                var key = index == 0 ? tokens[index].Value :
                    Connector(entry.AsSpan(tokens[index - 1].End, tokens[index].Start - tokens[index - 1].End)) + "\0" + tokens[index].Value;
                if (!node.Children.TryGetValue(key, out var next)) node.Children[key] = next = new Node();
                node = next;
            }
            // Preserve explicit symbol endings (e.g. masterbat*) when removing that entry.
            var suffix = entry[tokens[^1].End..];
            if (!node.Endings.Contains(suffix)) node.Endings.Add(suffix);
        }
    }

    public string Filter(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var tokens = Tokenize(text);
        var removed = new bool[text.Length];
        var changed = false;
        for (var start = 0; start < tokens.Count; start++)
        {
            if (!_root.Children.TryGetValue(tokens[start].Value, out var node)) continue;
            var longest = -1;
            var removalEnd = -1;
            for (var end = start; ; end++)
            {
                foreach (var suffix in node.Endings)
                {
                    var tokenEnd = tokens[end].End;
                    if (MatchesSuffix(text.AsSpan(tokenEnd), suffix) &&
                        (end > longest || tokenEnd + suffix.Length > removalEnd))
                    { longest = end; removalEnd = tokenEnd + suffix.Length; }
                }
                if (end + 1 >= tokens.Count) break;
                var key = Connector(text.AsSpan(tokens[end].End, tokens[end + 1].Start - tokens[end].End)) + "\0" + tokens[end + 1].Value;
                if (!node.Children.TryGetValue(key, out var next)) break;
                node = next;
            }
            if (longest < 0) continue;
            var removalStart = tokens[start].Start;
            // Quotes wrapped around an omitted term are punctuation debris, not replacement text.
            if (removalStart > 0 && removalEnd > removalStart &&
                IsQuotePair(text[removalStart - 1], text[removalEnd - 1])) removalStart--;
            else if (removalStart > 0 && removalEnd < text.Length &&
                IsQuotePair(text[removalStart - 1], text[removalEnd])) { removalStart--; removalEnd++; }
            Array.Fill(removed, true, removalStart, removalEnd - removalStart);
            changed = true;
            start = longest;
        }
        foreach (var symbol in _symbols)
        {
            var offset = 0;
            while (offset < text.Length)
            {
                var found = text.IndexOf(symbol, offset, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                Array.Fill(removed, true, found, symbol.Length);
                changed = true;
                offset = found + symbol.Length;
            }
        }
        if (!changed) return text;
        var output = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++) if (!removed[index]) output.Append(text[index]);
        var cleaned = Whitespace.Replace(output.ToString(), " ").Trim();
        cleaned = EmptyPairs.Replace(cleaned, " ");
        cleaned = Whitespace.Replace(cleaned, " ").Trim();
        cleaned = BeforePunctuation.Replace(cleaned, "$1");
        cleaned = AfterOpening.Replace(cleaned, "$1");
        cleaned = RepeatedCommas.Replace(cleaned, ",");
        cleaned = LeadingPunctuation.Replace(cleaned, "").TrimEnd(' ', ',', ';', ':');
        return cleaned.EnumerateRunes().Any(Rune.IsLetterOrDigit) ? cleaned : string.Empty;
    }

    private static EnglishCaptionFilter LoadDefault()
    {
        using var stream = typeof(EnglishCaptionFilter).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The English caption blacklist resource is missing.");
        using var reader = new StreamReader(stream);
        var entries = new List<string>();
        while (reader.ReadLine() is { } line) entries.Add(line);
        return new EnglishCaptionFilter(entries);
    }

    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static bool MatchesSuffix(ReadOnlySpan<char> text, string suffix) => suffix.Length == 0 ||
        (text.Length >= suffix.Length && string.Equals(text[..suffix.Length].ToString().Normalize(NormalizationForm.FormKC),
            suffix.Normalize(NormalizationForm.FormKC), StringComparison.OrdinalIgnoreCase));

    private static bool IsQuotePair(char left, char right) => (left, right) is
        ('\'', '\'') or ('"', '"') or ('\u2018', '\u2019') or ('\u201c', '\u201d');

    private static List<Token> Tokenize(string text)
    {
        var result = new List<Token>();
        for (var index = 0; index < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(index), out var first, out var width) != OperationStatus.Done || !Rune.IsLetterOrDigit(first))
            { index += Math.Max(1, width); continue; }
            var start = index;
            index += width;
            while (index < text.Length && Rune.DecodeFromUtf16(text.AsSpan(index), out var next, out width) == OperationStatus.Done && IsTokenCharacter(next)) index += width;
            var token = NormalizeToken(text[start..index]);
            if (token.Length > 0) result.Add(new Token(start, index, token));
        }
        return result;
    }

    private static bool IsTokenCharacter(Rune value) => Rune.IsLetterOrDigit(value) ||
        value.Value is '\'' or '\u2018' or '\u2019' or '\u02bc' or '\uff07' ||
        Rune.GetUnicodeCategory(value) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.Format;

    private static string NormalizeToken(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant()
            .Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u02bc', '\'').TrimEnd('\'');
        if (normalized.EndsWith("'s", StringComparison.Ordinal)) normalized = normalized[..^2];
        if (!normalized.Any(character => char.GetUnicodeCategory(character) == UnicodeCategory.Format)) return normalized;
        return string.Concat(normalized.Where(character => char.GetUnicodeCategory(character) != UnicodeCategory.Format));
    }

    private static string Connector(ReadOnlySpan<char> value)
    {
        var normalized = value.ToString().Normalize(NormalizationForm.FormKC);
        if (normalized.All(character => char.IsWhiteSpace(character) || char.GetUnicodeCategory(character) == UnicodeCategory.DashPunctuation)) return " ";
        return string.Concat(normalized.Where(character => !char.IsWhiteSpace(character))).ToLowerInvariant();
    }

    private sealed class Node
    {
        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
        public List<string> Endings { get; } = new();
    }
    private readonly record struct Token(int Start, int End, string Value);
}
