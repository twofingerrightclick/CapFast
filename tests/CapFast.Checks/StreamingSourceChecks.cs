using CapFast.Core;

internal static class StreamingSourceChecks
{
    public static void Run()
    {
        var sentence = new StreamingSourceSegmenter();
        Empty(sentence.Apply(new("continuous", "Karibu katika mkutano wetu. Leo tunazungumza pamoja", false), 0));
        var first = sentence.Apply(new("continuous", "Karibu katika mkutano wetu. Leo tunazungumza pamoja kuhusu jamii", false), .4);
        Equal("Karibu katika mkutano wetu.", first);
        Equal("Leo tunazungumza pamoja kuhusu jamii.", sentence.Apply(new("continuous",
            "Karibu katika mkutano wetu. Leo tunazungumza pamoja kuhusu jamii.", true), 1));
        Empty(sentence.Apply(new("continuous", "same duplicated final", true), 2));

        var multiple = new StreamingSourceSegmenter();
        multiple.Apply(new("multi", "Welcome everyone. Today is important. Please listen carefully. More", false), 0);
        var completed = multiple.Apply(new("multi", "Welcome everyone. Today is important. Please listen carefully. More words", false), .2);
        Equal("Welcome everyone. Today is important. Please listen carefully.", completed);
        if (completed.Count != 3) throw new Exception("Stable complete sentences were unnecessarily delayed.");

        var timed = new StreamingSourceSegmenter();
        var words = "moja mbili tatu nne tano sita saba nane tisa kumi kumi-na-moja kumi-na-mbili";
        Empty(timed.Apply(new("timed", words, false), 0));
        Equal("moja mbili tatu nne tano sita saba nane tisa kumi", timed.Apply(new("timed", words + " mwisho", false), 2.9));
        Empty(timed.Apply(new("timed", words + " mwisho zaidi", false), 3));
        Equal("kumi-na-moja kumi-na-mbili mwisho zaidi", timed.Apply(new("timed", words + " mwisho zaidi", true), 4));

        var revision = new StreamingSourceSegmenter();
        Empty(revision.Apply(new("rev", "Sisi tunahitaji maji safi kwa watu wote leo kesho labda", false), 0));
        Empty(revision.Apply(new("rev", "Sisi tunahitaji chakula bora kwa watu wote leo kesho labda", false), 3));
        // Only the first two words agreed; an unstable later guess cannot force a chunk.
        Equal("Sisi tunahitaji chakula bora kwa watu wote leo kesho labda", revision.Apply(new("rev",
            "Sisi tunahitaji chakula bora kwa watu wote leo kesho labda", true), 4));

        var correction = new StreamingSourceSegmenter();
        correction.Apply(new("correction", "We welcome all our friends today. Next we discuss water", false), 0);
        Equal("We welcome all our friends today.", correction.Apply(new("correction",
            "We welcome all our friends today. Next we discuss water", false), .5));
        Equal("Next we discuss water.", correction.Apply(new("correction",
            "we warmly welcome all our friends today! Next we discuss water.", true), 1));

        var deletion = new StreamingSourceSegmenter();
        deletion.Apply(new("del", "We warmly welcome all our friends today. Next is water", false), 0);
        deletion.Apply(new("del", "We warmly welcome all our friends today. Next is water", false), .1);
        Equal("Next is water.", deletion.Apply(new("del", "We welcome all our friends today. Next is water.", true), .2));

        var unicode = new StreamingSourceSegmenter();
        unicode.Apply(new("u", "Mkutano wetu ni mzuri. 👋🏽 jamii café", false), 0);
        Equal("Mkutano wetu ni mzuri.", unicode.Apply(new("u", "Mkutano wetu ni mzuri. 👋🏽 jamii café", false), .2));
        Equal("👋🏽 jamii café", unicode.Apply(new("u", "MKUTANO WETU NI MZURI! 👋🏽 jamii cafe\u0301", true), .3));
        // Unicode normalization is used only for alignment; emitted spelling stays untouched.

        var repeated = new StreamingSourceSegmenter();
        repeated.Apply(new("repeat", "yes yes yes. yes yes", false), 0);
        Equal("yes yes yes.", repeated.Apply(new("repeat", "yes yes yes. yes yes yes.", false), .1));
        Equal("yes yes yes.", repeated.Apply(new("repeat", "yes yes yes. yes yes yes.", true), .2));
        var a = repeated.Apply(new("next", "yes yes yes.", true), .3);
        Equal("yes yes yes.", a);
        if (a[0].UtteranceId == "repeat:stream-chunk:0") throw new Exception("Repeated utterances reused a caption ID.");

        var stale = new StreamingSourceSegmenter();
        stale.Apply(new("old", "old provisional words", false), 0);
        stale.Apply(new("new", "new provisional words", false), 1);
        Empty(stale.Apply(new("old", "late final must not replay", true), 2));
        Equal("new provisional words", stale.Apply(new("new", "new provisional words", true), 3));
        Empty(stale.Apply(new("empty", "", true), 4));
        Empty(stale.Apply(new("empty", "late repeated final", true), 5));

        var rejoined = new StreamingSourceSegmenter();
        Empty(rejoined.Apply(new("spacing", "Hasa kwa majukumu yaushahidi maalum wa jina la", false), 0));
        Equal("Hasa kwa majukumu yaushahidi maalum wa", rejoined.Apply(new("spacing",
            "Hasa kwa majukumu yaushahidi maalum wa jina la Yesu", false), 2.9));
        Empty(rejoined.Apply(new("spacing",
            "Hasa kwa majukumu yaushahidi maalum wa jina la Yesu Kristo", false), 3.1));
        Empty(rejoined.Apply(new("spacing",
            "Hasa kwa majukumu yaushahidi maalumwa jina la YesuKristo ulimwenguni", false), 3.6));
        Equal("jina la Yesu Kristo ulimwenguni kote.", rejoined.Apply(new("spacing",
            "Hasa kwa majukumu ya ushahidi maalum wa jina la Yesu Kristo ulimwenguni kote.", true), 4));

        var liveRevision = new StreamingSourceSegmenter();
        var emitted = new List<string>();
        foreach (var (at, snapshot) in new (double, string)[]
        {
            (61.196, "Hasa"), (61.707, "Hasa kwa majukumu"), (62.688, "Hasa kwa majukumu ya"),
            (63.166, "Hasa kwa majukumu ya ushahidi"),
            (63.599, "Hasa kwa majukumu ya ushahidi maalum"),
            (64.105, "Hasa kwa majukumu ya ushahidi maalum"),
            (64.593, "Hasa kwa majukumu yaushahidi maalum wa"),
            (65.072, "Hasa kwa majukumu yaushahidi maalum wa jina la"),
            (65.579, "Hasa kwa majukumu yaushahidi maalum wa jina la Yesu"),
            (66.019, "Hasa kwa majukumu yaushahidi maalum wa jina la Yesu Kristo"),
            (66.517, "Hasa kwa majukumu yaushahidi maalumwa jina la Yesu Kristo"),
            (67.036, "Hasa kwa majukumu yaushahidi maalumwa jina la Yesu Kristo ulimwenguni"),
            (67.530, "Hasa kwa majukumu yaushahidi maalumwa jina la YesuKristo ulimwenguni kote."),
            (68.038, "Hasa kwa majukumu yaushahidi maalumwa jina la YesuKristo ulimwenguni kote.")
        }) emitted.AddRange(liveRevision.Apply(new("live-spacing", snapshot, false), at).Select(chunk => chunk.Text));
        emitted.AddRange(liveRevision.Apply(new("live-spacing",
            "Hasa kwa majukumu ya ushahidi maalum wa jina la Yesu Kristo ulimwenguni kote.", true), 68.328)
            .Select(chunk => chunk.Text));
        var flattened = string.Concat(string.Join(" ", emitted).Where(char.IsLetterOrDigit)).ToLowerInvariant();
        if (flattened != "hasakwamajukumuyaushahidimaalumwajinalayesukristoulimwengunikote" ||
            emitted.Count(text => text.Contains("Kristo", StringComparison.Ordinal)) != 1)
            throw new Exception("Spacing corrections skipped 'jina' or repeated 'Kristo' in the live transcription trace.");

        var lexicalRevision = new StreamingSourceSegmenter();
        lexicalRevision.Apply(new("lexical", "I like cat.", false), 0);
        Equal("I like cat.", lexicalRevision.Apply(new("lexical", "I like cat.", false), .1));
        Equal("today.", lexicalRevision.Apply(new("lexical", "I like cats today.", true), .2));

        var bounded = new StreamingSourceSegmenter();
        var longText = string.Join(" ", Enumerable.Range(0, 75).Select(i => $"word{i}"));
        var chunks = bounded.Apply(new("long", longText, true), 0);
        if (chunks.Any(chunk => chunk.Text.Split(' ').Length > 18) || chunks.Select(chunk => chunk.UtteranceId).Distinct().Count() != chunks.Count)
            throw new Exception("Source chunk size or identity bound failed.");
        if (string.Join(" ", chunks.Select(chunk => chunk.Text)) != longText) throw new Exception("Long final lost source text.");
        try { bounded.Apply(new("oversized", new string('x', 16385), false), 1); throw new Exception("Oversized source was accepted."); }
        catch (InvalidDataException) { }
        // The final-ID cache is bounded: eviction permits legitimate reuse after its horizon.
        for (var i = 0; i < 257; i++) bounded.Apply(new($"closed-{i}", "hello", true), i + 2);
        Equal("hello", bounded.Apply(new("closed-0", "hello", true), 300));
    }

    private static void Empty(IReadOnlyList<CaptionUpdate> updates)
    { if (updates.Count != 0) throw new Exception("Unconfirmed or duplicate source unexpectedly emitted a chunk."); }
    private static void Equal(string text, IReadOnlyList<CaptionUpdate> updates)
    {
        var actual = string.Join(" ", updates.Select(update => update.Text));
        if (actual.Normalize() != text.Normalize() || updates.Any(update => !update.IsFinal))
            throw new Exception($"Streaming source expected '{text}', got '{actual}'.");
    }
}
