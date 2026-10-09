using System.Diagnostics;
using CapFast.Core;

internal static class CaptionFilterChecks
{
    public static void Run()
    {
        var filter = new EnglishCaptionFilter(new[] { "fuck", "fucks", "shit", "sex", "sexes", "blow job", "ass", "asshole", "f*ck", "f.u.c.k", "masterbat*", "🖕" });
        Equal("Welcome everyone.", filter.Filter("Welcome everyone."), "Ordinary captions must retain their original text.");
        Equal("Welcome everyone.", filter.Filter("Welcome fuck everyone."), "Omit the word without replacing it.");
        Equal("Welcome, everyone!", filter.Filter("Welcome, FUCK, everyone!"), "Clean repeated separators around omitted words.");
        Equal("", filter.Filter("(fuck)!"), "A removed sole word must not leave punctuation placeholders.");
        Equal("", filter.Filter("fuck shit sex"), "Remove multiple words entirely.");
        Equal("Welcome.", filter.Filter("Welcome ＦＵＣＫ."), "Fullwidth compatibility forms must be blocked.");
        Equal("Welcome.", filter.Filter("Welcome 𝓯𝓾𝓬𝓴."), "Supplementary compatibility letters must be blocked.");
        Equal("𝓱𝓲", filter.Filter("fuck 𝓱𝓲"), "Surviving supplementary letters must retain their original form.");
        Equal("Welcome.", filter.Filter("Welcome fuck’s."), "Curly possessives must be blocked.");
        Equal("Welcome.", filter.Filter("Welcome shit's."), "Straight possessives must be blocked.");
        Equal("Welcome.", filter.Filter("Welcome fu\u200bck."), "Invisible format characters must not defeat a word match.");
        Equal("Welcome.", filter.Filter("Welcome blow–job."), "A dash-separated phrase must match its space-separated entry.");
        Equal("Welcome.", filter.Filter("Welcome f*ck."), "Explicit masked forms must disappear without leaving stars.");
        Equal("Welcome.", filter.Filter("Welcome f.u.c.k."), "Explicit dotted forms must disappear.");
        Equal("Welcome.", filter.Filter("Welcome masterbat*."), "Explicit symbol endings must disappear with their term.");
        Equal("Welcome.", filter.Filter("Welcome masterbat＊."), "Compatibility-normalized symbol endings must also disappear.");
        Equal("Welcome.", filter.Filter("Welcome 🖕."), "A listed non-word symbol must disappear.");
        Equal("Welcome friend.", filter.Filter("Welcome 'fuck' friend."), "Omitted quoted words must not leave dangling quotes.");
        Equal("Welcome friend.", filter.Filter("Welcome \"fuck\" friend."), "Omitted double-quoted words must not leave dangling quotes.");
        Equal("Welcome friend.", new EnglishCaptionFilter(new[] { "masterbat*", "masterbat" }).Filter("Welcome masterbat* friend."), "Suffix omission must not depend on resource ordering.");
        Equal("Welcome friend.", new EnglishCaptionFilter(new[] { "a**", "a*" }).Filter("Welcome a** friend."), "The longest matching symbol suffix must be omitted.");
        const string boundaries = "Class assignment in Essex; assess the passage, assemble assistance and discuss sexuality's vocabulary. He'll tell us.";
        Equal(boundaries, filter.Filter(boundaries), "Whole-word matching must not remove innocent substrings or contractions.");
        Equal("Blow. Job interviews start tomorrow.", filter.Filter("Blow. Job interviews start tomorrow."), "Phrases must not join across sentence punctuation.");
        Equal("fu", filter.Filter("fu"), "An unfinished harmless prefix stays available until it becomes a blocked word.");
        Equal("", filter.Filter("fuck"), "A completed blocked interim must be empty so the display can retract its prefix.");
        Equal("sexes", new EnglishCaptionFilter(new[] { "sex" }).Filter("sexes"), "Inflections must be explicitly listed rather than guessed.");

        using var stream = typeof(EnglishCaptionFilter).Assembly.GetManifestResourceStream("CapFast.EnglishCaptionBlacklist.txt")
            ?? throw new Exception("The default blacklist must be embedded in both app and checks.");
        using var reader = new StreamReader(stream);
        var entries = new List<string>();
        while (reader.ReadLine() is { } line)
            if (line.Trim() is { Length: > 0 } entry && !entry.StartsWith('#')) entries.Add(entry);
        if (entries.Count < 700) throw new Exception("The comprehensive default blacklist is unexpectedly small.");
        foreach (var entry in entries)
        {
            Equal("", EnglishCaptionFilter.Default.Filter(entry), $"Default entry must be removed: {entry}");
            Equal("", EnglishCaptionFilter.Default.Filter(entry.ToUpperInvariant()), $"Case-insensitive default entry must be removed: {entry}");
            Equal("Welcome friend.", EnglishCaptionFilter.Default.Filter($"Welcome {entry} friend."), $"An embedded listed entry must leave neither symbols nor placeholders: {entry}");
        }
        Equal("The class assignment is in Essex.", EnglishCaptionFilter.Default.Filter("The class assignment is in Essex."), "Default resource must preserve safe word boundaries.");
        Equal("Gay and lesbian people are welcome; gender, religion and nationality do not matter.",
            EnglishCaptionFilter.Default.Filter("Gay and lesbian people are welcome; gender, religion and nationality do not matter."), "Neutral identities must not be blanket blocked.");
        Equal("We study.", EnglishCaptionFilter.Default.Filter("We study sex."), "The explicitly requested sex entry must be omitted and punctuation repaired.");

        // A generous ceiling catches accidentally quadratic repeated scans without making CPU speed part of correctness.
        var large = string.Join(' ', Enumerable.Repeat("Class assignment in Essex. Welcome fuck everyone.", 4_000));
        var timer = Stopwatch.StartNew();
        var cleaned = EnglishCaptionFilter.Default.Filter(large);
        if (timer.Elapsed > TimeSpan.FromSeconds(10) || cleaned.Contains("fuck", StringComparison.OrdinalIgnoreCase) || !cleaned.Contains("Essex"))
            throw new Exception("Filtering a large caption backlog must remain bounded and retain safe text.");
    }

    private static void Equal(string expected, string actual, string explanation)
    {
        if (actual != expected) throw new Exception($"{explanation} Expected '{expected}', received '{actual}'.");
    }
}
