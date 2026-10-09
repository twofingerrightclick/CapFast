using System.Text.Json;
using System.Text;
using System.Net.WebSockets;
using CapFast.Core;
using CapFast.Speech;

public static class TwoStageChecks
{
    public static async Task RunAsync()
    {
        StreamingSourceChecks.Run();
        var continuousSpeech = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var liveQueue = new SourceTranslationPipeline((source, _) =>
        { continuousSpeech.TrySetResult(source); return Task.FromResult("We meet today."); }, coalesceWindow: TimeSpan.Zero))
        {
            liveQueue.TryAcceptTranscript(new("continuous", "Tunakutana leo. Tuna", false), 0);
            liveQueue.TryAcceptTranscript(new("continuous", "Tunakutana leo. Tunazungumza", false), 1);
            liveQueue.TryAcceptTranscript(new("continuous", "Tunakutana leo. Tunazungumza pamoja", false), 3);
            Check(await Task.WhenAny(continuousSpeech.Task, Task.Delay(100)) == continuousSpeech.Task,
                "Continuous speech produced no English caption despite a stable complete source sentence before server finalization.");
        }
        var parser = new GeminiInputTranscript();
        IReadOnlyList<CaptionUpdate> Apply(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return parser.Apply(doc.RootElement);
        }
        var partial = Apply("""{"serverContent":{"interimInputTranscription":{"text":"Tutaonana Jumatatu"}}}""").Single();
        var revised = Apply("""{"serverContent":{"interimInputTranscription":{"text":"Tutaonana Jumanne"}}}""").Single();
        Check(revised.Text == "Tutaonana Jumanne" && revised.UtteranceId == partial.UtteranceId && !revised.IsFinal,
            "Interim hypotheses were appended rather than replaced.");
        var final = Apply("""{"serverContent":{"inputTranscription":{"text":"Tutaonana Jumanne.","startOffset":"0s","endOffset":"2s"}}}""").Single();
        Check(final.IsFinal && final.Text == "Tutaonana Jumanne." && final.UtteranceId == partial.UtteranceId,
            "Authoritative final transcript required a completion marker or retained a stale hypothesis.");
        Check(Apply("""{"serverContent":{"inputTranscription":{"text":"Tutaonana Jumanne.","startOffset":"0s","endOffset":"2s"}}}""").Count == 0,
            "A duplicate timestamped final was emitted twice.");
        var repeated = Apply("""{"serverContent":{"inputTranscription":{"text":"Tutaonana Jumanne.","startOffset":"3s","endOffset":"5s"}}}""").Single();
        Check(repeated.UtteranceId != final.UtteranceId, "A genuinely repeated utterance was discarded.");
        Check(Apply("""{"serverContent":{"turnComplete":true}}""").Count == 0, "A terminal marker created extra source text.");
        var bytes = Encoding.UTF8.GetBytes("""{"setupComplete":{}}""");
        using var binary = GoogleLiveProtocol.ParseMessage(bytes, WebSocketMessageType.Binary);
        using var textMessage = GoogleLiveProtocol.ParseMessage(bytes, WebSocketMessageType.Text);
        Check(binary.RootElement.TryGetProperty("setupComplete", out _) && textMessage.RootElement.TryGetProperty("setupComplete", out _),
            "Binary JSON transport frames were rejected.");
        try { using var invalid = GoogleLiveProtocol.ParseMessage(new byte[2 * 1024 * 1024 + 1], WebSocketMessageType.Binary); throw new Exception("Oversized frame accepted."); }
        catch (InvalidDataException) { }

        var setup = JsonSerializer.SerializeToElement(GoogleLiveProtocol.CreateSetup(GoogleCaptionMode.TwoStage)).GetProperty("setup");
        Check(setup.GetProperty("generationConfig").GetProperty("responseModalities")[0].GetString() == "TEXT"
            && setup.GetProperty("inputAudioTranscription").GetProperty("languageCodes")[0].GetString() == "sw-KE"
            && !setup.TryGetProperty("outputAudioTranscription", out _), "Two-stage setup uses the wrong transcription schema.");
        var request = JsonSerializer.SerializeToElement(FlashTextTranslator.CreateRequest("source-only-sentinel"));
        Check(request.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32() == 4096
            && request.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString() == "LOW",
            "Translation request diverged from the tested Flash configuration.");
        using var response = JsonDocument.Parse("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"thought":true,"text":"private reasoning"},{"text":"English output"}]}}]}""");
        Check(FlashTextTranslator.ParseResponse(response.RootElement) == "English output", "Reasoning leaked into captions.");
        using var truncated = JsonDocument.Parse("""{"candidates":[{"finishReason":"MAX_TOKENS","content":{"parts":[{"text":"partial"}]}}]}""");
        try { FlashTextTranslator.ParseResponse(truncated.RootElement); throw new Exception("Truncated translation was accepted."); }
        catch (CaptionSessionException) { }

        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var captions = new List<CaptionUpdate>();
        await using (var queue = new SourceTranslationPipeline(async (source, token) =>
        {
            calls.Add(source);
            if (source == "one") { firstEntered.TrySetResult(); return await releaseFirst.Task.WaitAsync(token); }
            return "English two";
        }, coalesceWindow: TimeSpan.Zero))
        {
            queue.Caption += update => { captions.Add(update); if (captions.Count == 2) allReceived.TrySetResult(); };
            queue.TryEnqueue(new("1", "interim", false));
            queue.TryEnqueue(new("1", "one", true));
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            queue.TryEnqueue(new("1", "duplicate", true));
            queue.TryEnqueue(new("2", "two", true));
            Check(calls.Count == 1, "Translation requests ran concurrently or provisional text was translated.");
            releaseFirst.TrySetResult("English one");
            await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(calls.SequenceEqual(new[] { "one", "two" }) && captions.Select(c => c.Text).SequenceEqual(new[] { "English one", "English two" }),
                "Translations were duplicated or displayed out of source order.");
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputAfterPause = 0;
        var paused = new SourceTranslationPipeline((_, _) => { entered.TrySetResult(); return late.Task; }, capacity: 1, coalesceWindow: TimeSpan.Zero);
        paused.Caption += _ => Interlocked.Increment(ref outputAfterPause);
        paused.TryEnqueue(new("active", "active", true));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(paused.TryEnqueue(new("queued", "queued", true)) && !paused.TryEnqueue(new("overflow", "overflow", true)),
            "Translation backlog was not bounded.");
        await paused.DisposeAsync();
        late.TrySetResult("Must never appear after pause");
        await paused.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Check(outputAfterPause == 0 && !paused.TryEnqueue(new("after", "after", true)), "Paused translation emitted stale captions or accepted new work.");

        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var queue = new SourceTranslationPipeline((_, _) => throw new IOException("key-secret-sentinel"), coalesceWindow: TimeSpan.Zero))
        {
            queue.Failed += text => failed.TrySetResult(text);
            queue.TryEnqueue(new("failure", "source", true));
            Check(!(await failed.Task.WaitAsync(TimeSpan.FromSeconds(2))).Contains("key-secret-sentinel"), "An arbitrary translation exception exposed sensitive details.");
        }
        var merged = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mergedCaption = new TaskCompletionSource<CaptionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<CaptionUpdate>? batch = null;
        await using (var queue = new SourceTranslationPipeline((source, _) =>
        { merged.TrySetResult(source); return Task.FromResult("without paying duty."); }))
        {
            queue.BatchStarted += sources => batch = sources;
            queue.Caption += caption => mergedCaption.TrySetResult(caption);
            queue.TryEnqueue(new("prefix", "ziliingia nchini bila", true));
            queue.TryEnqueue(new("suffix", "kulipiwa ushuru.", true));
            Check(await merged.Task.WaitAsync(TimeSpan.FromSeconds(2)) == "ziliingia nchini bila kulipiwa ushuru.",
                "A split source sentence was translated as unrelated fragments.");
            Check((await mergedCaption.Task.WaitAsync(TimeSpan.FromSeconds(2))).UtteranceId == "prefix" && batch?.Count == 2,
                "Coalescing lost source IDs or used the wrong caption ID.");
        }
        var immediate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var queue = new SourceTranslationPipeline((source, _) => { immediate.TrySetResult(source); return Task.FromResult("done"); }))
        {
            queue.TryEnqueue(new("complete", "Sentensi imekamilika.”", true));
            Check(await immediate.Task.WaitAsync(TimeSpan.FromSeconds(1)) == "Sentensi imekamilika.”", "A punctuated sentence waited for the three-second timer.");
        }
        var timed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var following = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var queue = new SourceTranslationPipeline((source, _) =>
        { if (source == "unfinished") timed.TrySetResult(source); else following.TrySetResult(source); return Task.FromResult("English"); }, coalesceWindow: TimeSpan.FromMilliseconds(50)))
        {
            queue.TryEnqueue(new("timed", "unfinished", true));
            await timed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            queue.TryEnqueue(new("following", "Next sentence.", true));
            Check(await following.Task.WaitAsync(TimeSpan.FromSeconds(2)) == "Next sentence.", "An abandoned timed read consumed the next segment.");
        }
        var canceledCalls = 0;
        var canceledBuffer = new SourceTranslationPipeline((_, _) => { Interlocked.Increment(ref canceledCalls); return Task.FromResult("stale"); });
        canceledBuffer.TryEnqueue(new("canceled", "Unfinished source", true));
        await canceledBuffer.DisposeAsync();
        Check(canceledCalls == 0, "Canceling the sentence buffer still launched a translation.");
        var boundedTexts = new List<string>();
        var boundedDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var queue = new SourceTranslationPipeline((source, _) =>
        { boundedTexts.Add(source); if (boundedTexts.Count == 2) boundedDone.TrySetResult(); return Task.FromResult("English"); }))
        {
            queue.TryEnqueue(new("limit-first", new string('a', 700), true));
            queue.TryEnqueue(new("limit-second", new string('b', 400) + ".", true));
            await boundedDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(boundedTexts.Count == 2 && boundedTexts[0].Length == 700 && boundedTexts[1].Length == 401,
                "The sentence character limit lost or over-merged a queued segment.");
        }
        Console.WriteLine("Two-stage checks passed: authoritative segments, interim replacement, timestamp deduplication, protocol, ordered bounded translations, pause cancellation, and safe errors.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
