using CapFast.Core;
using System.Globalization;

public static class CaptionLayoutChecks
{
    public static void Run()
    {
        var layout = new CaptionLineLayout();
        IReadOnlyList<string> Update(string text) => layout.Update(text, 7, value => value.Length);
        Equal("one", Update("one"));
        Equal("one two", Update("one two"));
        Equal("one two|three", Update("one two three"));
        Equal("one two|three|four", Update("one two three four"));
        Equal("three|four|five", Update("one two three four five"));
        Equal("four|five|six", Update("one two three four five six"));
        // Losing old controller head words must not change retained visual rows.
        Equal("five|six|seven", Update("three four five six seven"));
        Equal("five|six|eight", Update("three four five six eight"));
        Equal("five|six|", Update("three four five six"));

        var batch = new CaptionLineLayout();
        Equal("a b c d e|f g h i j|k l", batch.Update("a b c d e f g h i j k l", 10, value => value.Length));
        Equal("f g h i j|k l m n o|p q r s", batch.Update("a b c d e f g h i j k l m n o p q r s", 10, value => value.Length));

        var frames = batch.ScrollFrames;
        if (frames.Count < 2) throw new Exception("Batch did not retain intermediate scroll frames.");
        for (var index = 1; index < frames.Count; index++)
            if (!frames[index - 1].Skip(1).SequenceEqual(frames[index].Take(2)))
                throw new Exception("A scroll frame skipped or rewrote a completed row.");

        var initialBatch = new CaptionLineLayout();
        initialBatch.Update("a b c d e f g h", 1, value => value.Length);
        var introduced = initialBatch.ScrollFrames[0].Concat(initialBatch.ScrollFrames.Skip(1).Select(frame => frame[^1]));
        if (!introduced.SequenceEqual(new[] { "a", "b", "c", "d", "e", "f", "g", "h" }))
            throw new Exception("An atomic initial batch lost an intermediate caption row.");

        var sealedRows = new CaptionLineLayout();
        Equal("one two|three|four", sealedRows.Update("one two three four", 7, value => value.Length, utteranceId: "partial"));
        Equal("one two|three|five", sealedRows.Update("changed words before five", 7, value => value.Length, utteranceId: "partial"));

        var newSegment = new CaptionLineLayout();
        newSegment.Update("one two three four", 7, value => value.Length, utteranceId: "first");
        newSegment.Update("five six seven eight nine", 7, value => value.Length, utteranceId: "second");
        var newWords = newSegment.ScrollFrames[0].Concat(newSegment.ScrollFrames.Skip(1).Select(frame => frame[^1]));
        if (!newWords.Contains("four") || !newWords.Contains("five") || !newWords.Contains("six") || !newWords.Contains("nine"))
            throw new Exception("A new segment without snapshot overlap lost its opening words.");

        var repeat = new CaptionLineLayout();
        Equal("yes|yes|yes", repeat.Update("yes yes yes", 3, value => value.Length));
        Equal("yes|yes|yes", repeat.Update("yes yes yes yes", 3, value => value.Length));
        Equal("yes|yes|yes", repeat.Update("yes yes yes yes yes", 3, value => value.Length));
        Equal("yes|yes|", repeat.Update("yes yes yes yes", 3, value => value.Length));
        Equal("yes|yes|", repeat.Update("yes yes yes", 3, value => value.Length));

        var corrected = new CaptionLineLayout();
        Equal("red blue|green", corrected.Update("red blue green", 8, value => value.Length));
        Equal("red blue|longer|phrase", corrected.Update("red blue longer phrase", 8, value => value.Length));
        Equal("red blue|longer|", corrected.Update("red blue short", 8, value => value.Length));
        var resized = corrected.Update("red blue short", 4, value => value.Length);
        if (resized.Count > 3 || resized.Any(line => line.Length > 4)) throw new Exception("Resize exceeded the caption row/width limit.");
        Equal("", corrected.Update("", 4, value => value.Length));

        var unicode = new CaptionLineLayout();
        var word = string.Concat(Enumerable.Repeat("👋🏽", 7));
        var lines = unicode.Update(word, 2, value => StringInfo.ParseCombiningCharacters(value).Length);
        Equal("👋🏽👋🏽|👋🏽👋🏽|👋🏽", lines); // One row scrolls; Unicode fragments stay intact.
        if (lines.Any(line => StringInfo.ParseCombiningCharacters(line).Length > 2)) throw new Exception("Unicode wrapping split or overflowed a text element.");
        var oneRow = new CaptionLineLayout();
        Equal("three", oneRow.Update("one two three", 7, value => value.Length, rows: 1));
        Console.WriteLine("Caption layout checks passed: fixed rows, one-row scroll frames, sealed rows, bounded-tail overlap, revisions, repeated words, resize, Unicode, and narrow-height fallback.");
    }

    private static void Equal(string expected, IReadOnlyList<string> actual)
    {
        var joined = string.Join("|", actual);
        if (joined != expected) throw new Exception($"Caption page expected '{expected}', got '{joined}'.");
    }
}
