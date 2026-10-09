using System.Text.Json;
using CapFast.Core;

public static class GeminiChecks
{
    public static void Run()
    {
        var transcript = new GeminiTranscript();
        IReadOnlyList<CaptionUpdate> Apply(string json)
        {
            using var message = JsonDocument.Parse(json);
            return transcript.Apply(message.RootElement);
        }
        var first = Apply("""{"serverContent":{"outputTranscription":{"text":"Welcome"}}}""").Single();
        var second = Apply("""{"serverContent":{"outputTranscription":{"text":" to the seminar."}}}""").Single();
        Check(first.UtteranceId == second.UtteranceId && second.Text == "Welcome to the seminar." && !second.IsFinal,
            "Transcript deltas were not accumulated under one utterance.");
        var final = Apply("""{"serverContent":{"generationComplete":true}}""").Single();
        Check(final.IsFinal && final.Text == second.Text && final.UtteranceId == first.UtteranceId,
            "Generation completion did not commit the whole transcript.");
        Check(Apply("""{"serverContent":{"turnComplete":true}}""").Count == 0,
            "A repeated terminal marker duplicated a final.");
        var next = Apply("""{"serverContent":{"outputTranscription":{"text":"Very "}}}""").Single();
        var repeated = Apply("""{"serverContent":{"outputTranscription":{"text":"very "}}}""").Single();
        Check(next.UtteranceId != first.UtteranceId && repeated.Text == "Very very ",
            "Repeated meaningful deltas were discarded or utterance IDs reused.");
        var finished = Apply("""{"serverContent":{"outputTranscription":{"text":"good.","finished":true},"generationComplete":true}}""");
        Check(finished.Count == 2 && !finished[0].IsFinal && finished[1].IsFinal && finished[1].Text == "Very very good.",
            "Transcript finished marker did not finalize exactly once.");
        Check(Apply("""{"serverContent":{"turnComplete":true}}""").Count == 0,
            "Completion after finished duplicated a final.");
        Check(Apply("""{"serverContent":{"inputTranscription":{"text":"source must not be shown"},"modelTurn":{"parts":[{"inlineData":{"data":"audio"}}]}}}""").Count == 0,
            "Source transcription or output audio leaked into English captions.");
        Apply("""{"serverContent":{"outputTranscription":{"text":"Partial translation"}}}""");
        Check(Apply("""{"serverContent":{"interrupted":true}}""").Single().IsFinal,
            "Interrupted generation left an active provisional phrase.");
        Apply("""{"serverContent":{"outputTranscription":{"text":"New turn"}}}""");
        Check(Apply("""{"serverContent":{"turnComplete":true}}""").Single().Text == "New turn",
            "Turn completion fallback failed.");
        var huge = JsonSerializer.Serialize(new { serverContent = new { outputTranscription = new { text = new string('x', 50000) } } });
        Check(Apply(huge).Single().Text.Length <= 16384, "Unterminated transcript memory was not bounded.");
        Console.WriteLine("Gemini checks passed: delta accumulation, meaningful repetition, completion deduplication, new turns, interruption, transcript filtering, and memory bounds.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
