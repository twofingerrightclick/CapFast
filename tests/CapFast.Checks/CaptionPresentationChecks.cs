using CapFast.Core;
using System.Globalization;

public static class CaptionPresentationChecks
{
    public static void Run()
    {
        var reading = New(1000, 1);
        reading.ReadingWordsPerMinute = 180;
        reading.Submit(new("visible-long-final", string.Join(" ", Enumerable.Repeat("word", 60)), true));
        if (!reading.HasUnreadCaptions(0)) throw new Exception("Queued words were not protected from fading.");
        if (!reading.TryPresent(0, out _) || !reading.HasUnreadCaptions(0))
            throw new Exception("Animating words were not protected from fading.");
        reading.Presented(0);
        if (reading.PendingPhrases != 0 || !reading.HasUnreadCaptions(15) || reading.HasUnreadCaptions(20))
            throw new Exception("Visible final words were not protected for their complete reading time.");

        var available = New(1, 4);
        available.Submit(new("prior", "a b c", true)); Present(available, 0);
        available.Submit(new("incoming", "d e f", true));
        Equal("a|b|c|d", Present(available, 0).Lines);

        var rowReading = New(1, 3);
        rowReading.Submit(new("paragraph", "a b c", true)); Present(rowReading, 0);
        rowReading.Submit(new("following", "d", true));
        Blocked(rowReading, .49);
        Equal("b|c|d", Present(rowReading, .5).Lines);

        var full = New(1, 8);
        full.Submit(new("large", "a b c d e f g h", true));
        Equal("a|b|c|d|e|f|g|h", Present(full, 0).Lines);
        if (full.PendingPhrases != 0) throw new Exception("Fitting phrase was unnecessarily queued.");

        var chunks = New(1, 3);
        chunks.Submit(new("large", "a b c d e f g h", true));
        Equal("a|b|c", Present(chunks, 0).Lines);
        Blocked(chunks, .49);
        var next = Present(chunks, .5);
        Equal("b|c|d", next.Lines);
        if (next.ScrollRows != 1) throw new Exception("Oversized phrase did not advance one readable row.");
        Equal("c|d|e", Present(chunks, 1).Lines);
        Equal("d|e|f", Present(chunks, 1.5).Lines);
        Blocked(chunks, 2.99);
        next = Present(chunks, 3);
        Equal("e|f|g", next.Lines);
        Equal("f|g|h", Present(chunks, 4.5).Lines);
        if (next.ScrollRows != 1) throw new Exception("Final phrase chunk shifted excessive rows.");

        var cumulative = New(1, 3);
        foreach (var word in new[] { "a", "b", "c" })
        { cumulative.Submit(new(word, word, true)); Present(cumulative, 0); }
        cumulative.Submit(new("d", "d", true)); Blocked(cumulative, 1.49); Present(cumulative, 1.5);
        cumulative.Submit(new("e", "e", true)); Blocked(cumulative, 2.99); Present(cumulative, 3);

        var multi = New(1, 4);
        multi.Submit(new("first", "a b", true)); Present(multi, 0);
        multi.Submit(new("second", "c d", true)); Present(multi, 0);
        multi.Submit(new("third", "e f g h", true)); Blocked(multi, .74);
        Equal("b|c|d|e", Present(multi, .75).Lines);
        Equal("c|d|e|f", Present(multi, 1.5).Lines);
        Equal("d|e|f|g", Present(multi, 2.25).Lines);
        Equal("e|f|g|h", Present(multi, 3).Lines);

        var resize = New(1, 8);
        resize.Submit(new("resize", "a b c d e f g h", true)); Present(resize, 0);
        resize.Configure(1, 2, text => text.Length, "smaller", .1);
        Equal("a|b", Present(resize, .1).Lines);
        Equal("b|c", Present(resize, 2).Lines);
        Equal("c|d", Present(resize, 4).Lines);
        Equal("d|e", Present(resize, 6).Lines);
        Equal("e|f", Present(resize, 8).Lines);
        Equal("f|g", Present(resize, 10).Lines);
        Equal("g|h", Present(resize, 12).Lines);

        var canceled = New(1, 8);
        canceled.Submit(new("resize", "a b c d e f g h", true));
        if (!canceled.TryPresent(0, out _)) throw new Exception("Initial chunk absent.");
        canceled.Configure(1, 2, text => text.Length, "smaller", .1);
        Equal("a|b", Present(canceled, .1).Lines);
        canceled.Clear(); canceled.Presented(99);
        Equal("", canceled.CurrentLines); Blocked(canceled, 999);

        var unicode = New(2, 2, text => StringInfo.ParseCombiningCharacters(text).Length);
        var emoji = string.Concat(Enumerable.Repeat("👋🏽", 7));
        unicode.Submit(new("unicode", emoji, true));
        Equal("👋🏽👋🏽|👋🏽👋🏽", Present(unicode, 0).Lines);
        Equal("👋🏽👋🏽|👋🏽👋🏽", Present(unicode, 5).Lines);
        Equal("👋🏽👋🏽|👋🏽", Present(unicode, 7).Lines);

        var partial = New(100, 1);
        partial.Submit(new("p", "one", false)); Present(partial, 0);
        var addedHold = 0d;
        for (var index = 2; index <= 10; index++)
        {
            partial.Submit(new("p", string.Join(" ", Enumerable.Repeat("one", index)), false));
            addedHold += Present(partial, 0).HoldFor.TotalSeconds;
        }
        partial.Submit(new("p", string.Join(" ", Enumerable.Repeat("one", 10)), true));
        if (Present(partial, 0).HoldFor != TimeSpan.Zero || addedHold > 4)
            throw new Exception("Partial revisions multiplied the reading budget for already visible words.");

        var revised = New(1, 3);
        revised.Submit(new("p", "a b c", false)); Present(revised, 0);
        revised.Submit(new("p", "a b d e", true));
        Equal("a|b|d", Present(revised, 1.5).AnimationLines);
        Equal("a|b|d|e", Present(revised, 1.5).AnimationLines);
        var noMatch = New(7, 3);
        noMatch.Submit(new("p", "one two three four", false)); Present(noMatch, 0);
        noMatch.Submit(new("p", "", true));
        Equal("one two|three", noMatch.CurrentLines);
        noMatch.Submit(new("p", "late duplicate", true));
        if (noMatch.PendingPhrases != 0) throw new Exception("NoMatch allowed a late duplicate.");

        var replayCap = New(1, 3);
        replayCap.Submit(new("large", "a b c d e f g h", true)); Present(replayCap, 0);
        for (var index = 0; index < 31; index++) replayCap.Submit(new($"cap-{index}", "z", true));
        replayCap.Configure(1, 2, text => text.Length, "resize", .1);
        if (replayCap.Submit(new("overflow", "z", true)))
            throw new Exception("Resizing exempted never-presented oversized text from the capture cap.");

        var extended = New(1, 3);
        extended.Submit(new("partial", "a b", false)); Present(extended, 0);
        extended.Submit(new("partial", "a b c", true)); Present(extended, 0);
        extended.Submit(new("next", "d", true)); Blocked(extended, .74);
        Present(extended, .75);

        var shortened = New(1, 4);
        shortened.Submit(new("partial", "a b c d e f", false)); Present(shortened, 0);
        shortened.Configure(1, 2, text => text.Length, "smaller", .1);
        shortened.Submit(new("partial", "a", true));
        Equal("a", Present(shortened, .1).Lines);
        Blocked(shortened, 99);

        var measurements = 0;
        var cached = New(1, 1, text => { measurements++; return text.Length; });
        cached.Submit(new("first", "a", true)); Present(cached, 0);
        cached.Submit(new("second", "b", true)); Blocked(cached, .1);
        var measured = measurements;
        for (var index = 0; index < 50; index++) Blocked(cached, .2);
        if (measurements != measured) throw new Exception("Reading dwell repeatedly measured unchanged queued text.");

        var backlog = New(1, 1);
        for (var index = 0; index < 32; index++)
            if (!backlog.Submit(new($"q{index}", "a", true))) throw new Exception("Backlog rejected below capacity.");
        if (backlog.Submit(new("overflow", "z", true)) || backlog.PendingPhrases != 33 || backlog.CanAcceptCaptions)
            throw new Exception("Overflow phrase was dropped or capture remained enabled.");
        for (var index = 0; index < 32; index++) Present(backlog, index * 2);
        Equal("z", Present(backlog, 64).Lines);
        if (!backlog.CanAcceptCaptions) throw new Exception("Drained presentation did not reopen capture capacity.");

        var bilingual = New(1000, 1);
        bilingual.ReadingSpeedForLanguage = language => language switch { "sw" => 120, "en" => 240, _ => 180 };
        var twelveWords = string.Join(" ", Enumerable.Repeat("word", 12));
        bilingual.Submit(new("sw", twelveWords, true, PrimaryLanguage: "sw"));
        if (Present(bilingual, 0).HoldFor.TotalSeconds != 6)
            throw new Exception("Swahili reading speed was not applied to its own phrase.");
        bilingual.Clear();
        bilingual.Submit(new("en", twelveWords, true, PrimaryLanguage: "en"));
        if (Present(bilingual, 0).HoldFor.TotalSeconds != 3)
            throw new Exception("English reading speed did not override Swahili on the shared surface.");
        bilingual.Clear();
        bilingual.Submit(new("fr", twelveWords, true, PrimaryLanguage: "fr"));
        if (Present(bilingual, 0).HoldFor.TotalSeconds != 4)
            throw new Exception("Unset language speed did not use the shared default.");

        var accelerated = New(1000, 1);
        accelerated.CatchUpEnabled = true;
        accelerated.Submit(new("long", string.Join(" ", Enumerable.Repeat("word", 60)), true), 0);
        Present(accelerated, 0); // A 20-second visible reading deadline is already established.
        accelerated.Submit(new("new", "new thought", true), 0);
        if (accelerated.DisplayDelaySeconds(3.1) < 3 || !accelerated.IsCatchingUp)
            throw new Exception("Three seconds of display waiting did not engage catch-up.");
        Blocked(accelerated, 3.1);
        Equal("new thought", Present(accelerated, 4.7).Lines);
        if (accelerated.DisplayDelaySeconds(4.7) != 0 || accelerated.IsCatchingUp)
            throw new Exception("Catch-up remained active after the waiting queue drained.");
        accelerated.CatchUpEnabled = false;
        if (accelerated.IsCatchingUp) throw new Exception("Turning catch-up off did not clear its status immediately.");

        var staleQueue = New(1000, 1);
        staleQueue.CatchUpEnabled = true;
        staleQueue.Submit(new("old", "obsolete opening", true), 0);
        staleQueue.Submit(new("middle", "obsolete middle", true), .5);
        staleQueue.Submit(new("latest", "latest thought", true), 4);
        Equal("latest thought", Present(staleQueue, 7).Lines);
        if (staleQueue.SkippedPhrases != 2 || staleQueue.PendingPhrases != 0)
            throw new Exception("Stale queued captions were not trimmed with explicit accounting.");
        staleQueue.Submit(new("old", "late stale revision", true), 7.1);
        if (staleQueue.PendingPhrases != 0) throw new Exception("A skipped phrase was replayed by a late revision.");

        var crowded = New(1000, 1);
        crowded.CatchUpEnabled = true;
        for (var index = 0; index < 32; index++) crowded.Submit(new($"old-{index}", "old", true), 0);
        if (!crowded.Submit(new("fresh", "current thought", true), 7) || crowded.SkippedPhrases != 31)
            throw new Exception("Catch-up did not free stale display capacity before accepting live speech.");
        Equal("current thought", Present(crowded, 7).Lines);
        if (crowded.SkippedPhrases != 32)
            throw new Exception("Stale backlog was not fully accounted for at presentation.");

        var sole = New(1000, 1);
        sole.CatchUpEnabled = true;
        sole.Submit(new("only", "retain the only thought", true), 0);
        Equal("retain the only thought", Present(sole, 8).Lines);
        if (sole.SkippedPhrases != 0) throw new Exception("The sole queued phrase was discarded as stale.");

        var resumedSuffix = New(1000, 1);
        resumedSuffix.CatchUpEnabled = true;
        resumedSuffix.Submit(new("partial", "first", false), 0); Present(resumedSuffix, 0);
        resumedSuffix.Submit(new("partial", "first second", false), 10);
        if (resumedSuffix.DisplayDelaySeconds(10) != 0)
            throw new Exception("A newly arrived partial suffix inherited stale queue age.");

        var disabled = New(1000, 1);
        disabled.Submit(new("long", string.Join(" ", Enumerable.Repeat("word", 60)), true), 0);
        Present(disabled, 0);
        disabled.Submit(new("next", "next", true), 0);
        Blocked(disabled, 10);
        if (disabled.SkippedPhrases != 0 || disabled.IsCatchingUp)
            throw new Exception("Disabled catch-up changed the old reading schedule.");

        Console.WriteLine("Caption presentation checks passed: full height, phrase openings, paced chunks, cumulative deadlines, minimum scroll, resize replay, cancellation, Unicode, partial budgets, retained overflow, per-language reading, and bounded catch-up.");
    }

    private static CaptionPresentation New(double width, int rows, Func<string, double>? measure = null)
    {
        var presentation = new CaptionPresentation { CatchUpEnabled = false };
        presentation.Configure(width, rows, measure ?? (text => text.Length), "initial");
        return presentation;
    }
    private static CaptionPresentationFrame Present(CaptionPresentation presentation, double time)
    {
        if (!presentation.TryPresent(time, out var frame)) throw new Exception($"Expected caption at {time}.");
        presentation.Presented(time); return frame!;
    }
    private static void Blocked(CaptionPresentation presentation, double time)
    {
        if (presentation.TryPresent(time, out _)) throw new Exception($"Caption advanced before reading deadline at {time}.");
    }
    private static void Equal(string expected, IReadOnlyList<string> lines)
    {
        if (string.Join("|", lines) != expected) throw new Exception($"Expected '{expected}', got '{string.Join("|", lines)}'.");
    }
}
