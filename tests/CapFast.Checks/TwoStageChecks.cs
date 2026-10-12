using System.Text.Json;
using System.Text;
using System.Net.WebSockets;
using CapFast.Core;
using CapFast.Speech;

public static class TwoStageChecks
{
    public static async Task RunAsync()
    {
        await TranscriptLanguageChecks.RunAsync();
        LanguageSurfaceChecks.Run();
        LanguageAssignmentChecks.Run();
        await BlockTranslationChecks.RunAsync();
        await LanguageAssignmentRegressionChecks.RunAsync();
        var routed = new TaskCompletionSource<CaptionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var switched = new TaskCompletionSource<CaptionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var assignedCalls = 0;
        await using (var assignedQueue = new SourceTranslationPipeline((text, assignment, _) =>
        {
            assignedCalls++;
            return Task.FromResult(($"{assignment.Primary}:{text}", $"{assignment.Secondary}:{text}"));
        }))
        {
            assignedQueue.Caption += update => { routed.TrySetResult(update); if (update.SourceLanguage == "sw") switched.TrySetResult(update); };
            assignedQueue.TryAcceptTranscript(new("first-en", "Hello.", true, SourceLanguage: "en-US"), 0);
            var translated = await routed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(translated.Text == "sw:Hello." && translated.FrenchText == "fr:Hello." && translated.PrimaryLanguage == "sw", "Detected English was not routed into Swahili and French.");
            assignedQueue.TryAcceptTranscript(new("translator-sw", "Habari.", true, SourceLanguage: "sw"), 1);
            var mixed = await switched.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(mixed.PrimaryLanguage == "en" && mixed.SecondaryLanguage == "fr" && assignedCalls == 2,
                "Mid-block Swahili was not translated into English and French.");
            Check(assignedQueue.Assignment?.Input == "en", "Language switch changed the speech block anchor.");
            assignedQueue.ObserveAudio(AudioActivity.Idle, 6);
            Check(assignedQueue.Assignment is null, "Five seconds without speech did not reset assignment.");
        }
        StreamingSourceChecks.Run();
        var continuousSpeech = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var liveQueue = new SourceTranslationPipeline((source, _, _) =>
        { continuousSpeech.TrySetResult(source); return Task.FromResult(("We meet today.", "Nous nous réunissons aujourd'hui.")); }))
        {
            liveQueue.TryAcceptTranscript(new("continuous", "Tunakutana leo. Tuna", false, SourceLanguage: "sw"), 0);
            liveQueue.TryAcceptTranscript(new("continuous", "Tunakutana leo. Tunazungumza", false, SourceLanguage: "sw"), 1);
            liveQueue.TryAcceptTranscript(new("continuous", "Tunakutana leo. Tunazungumza pamoja", false, SourceLanguage: "sw"), 3);
            Check(await continuousSpeech.Task.WaitAsync(TimeSpan.FromSeconds(2)) == "Tunakutana leo.",
                "Continuous speech lost a stable complete source sentence before server finalization.");
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
        var final = Apply("""{"serverContent":{"inputTranscription":{"text":"Tutaonana Jumanne.","languageCode":"sw-KE","startOffset":"0s","endOffset":"2s"}}}""").Single();
        Check(final.SourceLanguage == "sw" && final.IsFinal && final.Text == "Tutaonana Jumanne." && final.UtteranceId == partial.UtteranceId,
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

        var setup = JsonSerializer.SerializeToElement(GoogleLiveProtocol.CreateSetup()).GetProperty("setup");
        Check(setup.GetProperty("generationConfig").GetProperty("responseModalities")[0].GetString() == "TEXT"
            && setup.GetProperty("inputAudioTranscription").GetProperty("languageCodes")
                .EnumerateArray().Select(value => value.GetString()).SequenceEqual(new[] { "en-US", "sw-KE", "fr-FR" })
            && !setup.TryGetProperty("outputAudioTranscription", out _), "Two-stage setup uses the wrong transcription schema.");
        var request = JsonSerializer.SerializeToElement(FlashTextTranslator.CreateRequest("source-only-sentinel"));
        Check(request.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32() == 4096
            && request.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString() == "LOW",
            "Translation request diverged from the tested Flash configuration.");
        using var response = JsonDocument.Parse("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"thought":true,"text":"private reasoning"},{"text":"English output"}]}}]}""");
        Check(FlashTextTranslator.ParseResponse(response.RootElement) == "English output", "Reasoning leaked into captions.");
        using var technical = JsonDocument.Parse("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"JSON parsing error: {\"unexpected\": true}"}]}}]}""");
        try { FlashTextTranslator.ParseResponse(technical.RootElement); throw new Exception("Technical model output was accepted as a translation."); }
        catch (CaptionSessionException) { }
        using var truncated = JsonDocument.Parse("""{"candidates":[{"finishReason":"MAX_TOKENS","content":{"parts":[{"text":"partial"}]}}]}""");
        try { FlashTextTranslator.ParseResponse(truncated.RootElement); throw new Exception("Truncated translation was accepted."); }
        catch (CaptionSessionException) { }

        await CheckAssignedQueueLifecycleAsync();
        Console.WriteLine("Two-stage checks passed: authoritative segments, interim replacement, timestamp deduplication, protocol, ordered bounded translations, pause cancellation, and safe errors.");
    }

    private static async Task CheckAssignedQueueLifecycleAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var captions = new List<CaptionUpdate>();
        await using (var queue = new SourceTranslationPipeline((source, _, token) =>
        {
            calls.Add(source);
            if (source == "One.") { entered.TrySetResult(); return release.Task.WaitAsync(token); }
            return Task.FromResult(("Two translated.", "Deux traduit."));
        }))
        {
            queue.Caption += update => { captions.Add(update); if (captions.Count == 2) received.TrySetResult(); };
            queue.TryAcceptTranscript(new("one", "One.", true, SourceLanguage: "en"), 0);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            queue.TryAcceptTranscript(new("one", "One.", true, SourceLanguage: "en"), 0);
            queue.TryAcceptTranscript(new("two", "Two.", true, SourceLanguage: "en"), 1);
            Check(calls.Count == 1, "Later same-block translation overtook earlier work.");
            release.SetResult(("One translated.", "Un traduit."));
            await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(calls.SequenceEqual(new[] { "One.", "Two." }) && captions.All(update => update.FrenchText is not null),
                "Assigned translations duplicated source or lost complete output pairs.");
        }

        var pausedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputAfterPause = 0;
        var paused = new SourceTranslationPipeline((_, _, _) => { pausedEntered.TrySetResult(); return late.Task; }, capacity: 1);
        paused.Caption += _ => Interlocked.Increment(ref outputAfterPause);
        paused.TryAcceptTranscript(new("active", "Active.", true, SourceLanguage: "en"), 0);
        await pausedEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!paused.TryAcceptTranscript(new("overflow", "Overflow.", true, SourceLanguage: "en"), 1), "Assigned backlog was not bounded.");
        await paused.DisposeAsync();
        late.SetResult(("Stale.", "Périmé."));
        await paused.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Check(outputAfterPause == 0 && !paused.TryAcceptTranscript(new("after", "After.", true, SourceLanguage: "en"), 2),
            "Pause emitted stale captions or accepted new source.");

        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var failure = new SourceTranslationPipeline((_, _, _) => throw new IOException("key-secret-sentinel"));
        failure.Failed += message => failed.TrySetResult(message);
        failure.TryAcceptTranscript(new("failure", "Source.", true, SourceLanguage: "en"), 0);
        Check(!(await failed.Task.WaitAsync(TimeSpan.FromSeconds(2))).Contains("key-secret-sentinel"), "Provider exception exposed sensitive details.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
