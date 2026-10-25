using CapFast.Core;
using CapFast.Speech;
public static class LanguageAssignmentRegressionChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task RunAsync()
    {
        await CheckContinuousLanguageSwitchAsync();
        await CheckReceivedFinalDrainAsync();
        await CheckRetiredFailuresAsync();
        await CheckFinalOrdersLaterSourcesAsync();
        await CheckDrainOrderAsync();
        await CheckNaturalRetirementKeepsPrefixAsync();
        var accepted = new List<CaptionUpdate>();
        var oldEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldWork = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fresh = new TaskCompletionSource<CaptionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipeline = new SourceTranslationPipeline((text, assignment, token) =>
        {
            if (text == "Old.") { oldEntered.TrySetResult(); return oldWork.Task; }
            return Task.FromResult((text, text));
        });
        pipeline.SourceAccepted += accepted.Add;
        pipeline.Caption += update => fresh.TrySetResult(update);
        pipeline.ObserveAudio(AudioActivity.Speech, 0);
        var originalAudioGeneration = pipeline.AudioGeneration;
        pipeline.TryAcceptTranscript(new("old", "Old.", true, SourceLanguage: "en"), 0);
        await oldEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        pipeline.ObserveAudio(AudioActivity.Idle, 5);
        Check(pipeline.Assignment is null, "Silence must expire the language assignment.");
        pipeline.TryAcceptTranscript(new("late-unseen", "Late.", true, SourceLanguage: "en"), 6);
        Check(pipeline.Assignment is null, "Late final resurrected an expired audio block.");
        pipeline.ObserveAudio(AudioActivity.Speech, 7);
        pipeline.TryAcceptTranscript(new("old", "Old late.", true, SourceLanguage: "en"), 7);
        Check(pipeline.Assignment is null, "Known obsolete utterance resurrected after new speech.");
        pipeline.TryAcceptTranscript(new("unseen-old-final", "Wrong source.", true, SourceLanguage: "en"), 7, originalAudioGeneration);
        Check(pipeline.Assignment is null, "Unseen old final seized the new block after speech resumed.");
        pipeline.TryAcceptTranscript(new("new", "Fresh.", true, SourceLanguage: "sw"), 7, pipeline.AudioGeneration);
        var caption = await fresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(caption.SourceLanguage == "sw", "Old work blocked fresh assignment.");
        oldWork.SetException(new IOException("obsolete failure"));
        Check(!pipeline.Completion.IsCompleted, "Obsolete failure terminated pipeline.");

        var sources = new List<CaptionUpdate>();
        await using var alignment = new SourceTranslationPipeline((text, assignment, token) => Task.FromResult((text, text)));
        alignment.SourceAccepted += sources.Add;
        alignment.ObserveAudio(AudioActivity.Speech, 0);
        alignment.TryAcceptTranscript(new("continuous", "First. More", false, SourceLanguage: "en"), 0);
        alignment.TryAcceptTranscript(new("continuous", "First. More words", false), 1);
        Check(sources.Count == 1 && sources[0].Text == "First.", "Known interim without language lost stable text.");
        var originalId = sources[0].UtteranceId;
        alignment.ResetAssignment();
        alignment.TryAcceptTranscript(new("continuous", "First. More words.", true), 2);
        Check(sources.Count == 2 && sources[1].Text == "More words." && sources[1].UtteranceId != originalId,
            "Reset replayed consumed words or collided with source ledger.");
        alignment.TryAcceptTranscript(new("translator", "Bonjour", false, SourceLanguage: "fr"), 3);
        alignment.TryAcceptTranscript(new("translator", "Bonjour tout le monde.", true), 4);
        Check(sources.Count == 3 && sources[^1].SourceLanguage == "fr" && sources[^1].PrimaryLanguage == "en" && sources[^1].SecondaryLanguage == "sw",
            "French continuation was not translated into English and Swahili.");
        alignment.ResetForNextUtterance();
        Check(alignment.Assignment is null, "Reset command failed to release assignment immediately.");
        alignment.ObserveAudio(AudioActivity.Speech, 4);
        alignment.TryAcceptTranscript(new("continuous", "Old reset continuation.", true, SourceLanguage: "fr"), 4);
        Check(alignment.Assignment is null, "Reset command accepted old utterance rather than waiting for fresh detection.");
        alignment.TryAcceptTranscript(new("after-reset", "Nouveau", false, SourceLanguage: "fr"), 4, alignment.AudioGeneration);
        Check(alignment.Assignment?.Input == "fr", "Reset command did not accept fresh utterance.");
        alignment.ObserveAudio(AudioActivity.Idle, 9);
        Check(alignment.Assignment is null, "Delayed final extended audio block by receipt time.");
    }
    private static async Task CheckContinuousLanguageSwitchAsync()
    {
        var captions = new List<CaptionUpdate>();
        var translated = new List<LanguageAssignment>();
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipeline = new SourceTranslationPipeline((text, assignment, _) =>
        {
            lock (translated) translated.Add(assignment);
            return Task.FromResult(($"{assignment.Primary}:{text}", $"{assignment.Secondary}:{text}"));
        });
        pipeline.Caption += caption => { lock (captions) { captions.Add(caption); if (captions.Count == 3) enough.TrySetResult(); } };
        pipeline.ObserveAudio(AudioActivity.Speech, 0);
        pipeline.TryAcceptTranscript(new("sw", "Habari.", true, SourceLanguage: "sw"), 0);
        pipeline.ObserveAudio(AudioActivity.Speech, 1);
        pipeline.TryAcceptTranscript(new("en", "Hello.", true, SourceLanguage: "en"), 1);
        await pipeline.CompleteReceivedFinalAsync(new("fr", "Bonjour.", true, SourceLanguage: "fr"),
            pipeline.CaptureReceivedBlock(pipeline.AudioGeneration), 2);
        await enough.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(translated.Select(a => a.Input).SequenceEqual(new[] { "sw", "en", "fr" }),
            "A continuous language switch was discarded or translated from the wrong language.");
        Check(captions.Select(c => (c.PrimaryLanguage!, c.SecondaryLanguage!)).SequenceEqual(new[]
            { ("en", "fr"), ("sw", "fr"), ("en", "sw") }), "Switch did not target the other two languages.");
        var oldBlock = pipeline.CaptureReceivedBlock(pipeline.AudioGeneration);
        pipeline.ObserveAudio(AudioActivity.Idle, 7);
        pipeline.ObserveAudio(AudioActivity.Speech, 8);
        pipeline.TryAcceptTranscript(new("fresh", "Hello again.", true, SourceLanguage: "en"), 8);
        Check(pipeline.FallbackInput(oldBlock) == "fr" &&
            pipeline.FallbackInput(pipeline.CaptureReceivedBlock(pipeline.AudioGeneration)) == "en",
            "A retired block inherited the next block's fallback language.");
    }
    private static async Task CheckReceivedFinalDrainAsync()
    {
        var captions = new List<CaptionUpdate>();
        await using var pipeline = new SourceTranslationPipeline((text, assignment, token) => Task.FromResult(($"{assignment.Input}:{text}", text)));
        pipeline.Caption += captions.Add;
        pipeline.ObserveAudio(AudioActivity.Speech, 0);
        var received = pipeline.CaptureReceivedBlock(pipeline.AudioGeneration);
        var delayed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new TranscriptLanguageResolver();
        var identification = resolver.ResolveAsync(new("short-final", "Thank you.", true), (_, _) => delayed.Task, default);
        pipeline.ObserveAudio(AudioActivity.Idle, 5);
        Check(pipeline.Assignment is null, "Pending classifier prevented candidate reset.");
        Check(pipeline.CaptureReceivedBlock(received.Generation).ManualRevision < 0,
            "Transcript received after generation retirement acquired a new manual revision.");
        pipeline.ObserveAudio(AudioActivity.Speech, 6);
        pipeline.TryAcceptTranscript(new("new-block", "Habari", false, SourceLanguage: "sw"), 6, pipeline.AudioGeneration);
        delayed.SetResult("en");
        await pipeline.CompleteReceivedFinalAsync((await identification)!, received, 0);
        Check(captions.Count == 1 && captions[0].IsSpeechBlockDrain && captions[0].PrimaryLanguage == "sw" && captions[0].SecondaryLanguage == "fr",
            "Received short final disappeared during idle classification.");
        Check(pipeline.Assignment?.Input == "sw", "Old classified final seized next block.");
        await pipeline.CompleteReceivedFinalAsync(new("old-translator", "Bonjour.", true, SourceLanguage: "fr"), received, 0);
        Check(captions.Count == 2 && captions[^1].SourceLanguage == "fr" && captions[^1].PrimaryLanguage == "en" && captions[^1].SecondaryLanguage == "sw",
            "Old French final was not translated into English and Swahili.");
        var beforeNewFinal = captions.Count;
        await pipeline.CompleteReceivedFinalAsync(new("new-block", "Habari mpya.", true, SourceLanguage: "sw"),
            pipeline.CaptureReceivedBlock(pipeline.AudioGeneration), 6);
        Check(captions.Count == beforeNewFinal + 1 && captions[^1].SourceLanguage == "sw" && captions[^1].Text.Contains("Habari mpya."),
            "Old final closed shared segmenter and consumed new block's final.");
        Check(pipeline.CanPresentCaption(captions[0]), "Naturally drained final was not eligible for retained-language routing.");
        pipeline.HintInputLanguage("fr");
        Check(!pipeline.CanPresentCaption(captions[0]), "Hint retained obsolete queued drain.");
        await pipeline.CompleteReceivedFinalAsync(new("obsolete", "Old.", true, SourceLanguage: "en"), received, 0);
        Check(captions.Count == beforeNewFinal + 1, "Manual reset allowed obsolete received final.");
        pipeline.ObserveAudio(AudioActivity.Speech, 7);
        var hint = pipeline.CaptureHint("hinted");
        Check(hint == "fr" && pipeline.CaptureHint("translator") is null, "Hint escaped first utterance.");
        var hinted = await resolver.ResolveAsync(new("hinted", "texte", true, SourceLanguage: "en"), (_, _) => Task.FromResult<string?>(null), default, hint);
        await pipeline.CompleteReceivedFinalAsync(hinted!, pipeline.CaptureReceivedBlock(pipeline.AudioGeneration), 7);
        Check(pipeline.Assignment?.Input == "fr" && captions[^1].SourceLanguage == "fr", "Hint failed to override wrong automatic code.");
        var hintedUnknown = await new TranscriptLanguageResolver().ResolveAsync(new("unknown", "mot", true), (_, _) => Task.FromResult<string?>(null), default, "fr");
        Check(hintedUnknown?.SourceLanguage == "fr", "Unknown detection swallowed manual hint.");
        var count = captions.Count;
        await pipeline.CompleteReceivedFinalAsync(new("translator", "English translator.", true, SourceLanguage: "en"), pipeline.CaptureReceivedBlock(pipeline.AudioGeneration), 7);
        Check(captions.Count == count + 1 && captions[^1].SourceLanguage == "en",
            "A later English utterance was discarded after a French hint.");
    }

    private static async Task CheckDrainOrderAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        await using var pipeline = new SourceTranslationPipeline((text, _, _) => { calls.Add(text); entered.TrySetResult(); return release.Task; },
            drainTranslate: (text, _, _) => { calls.Add(text); return Task.FromResult((text, text)); });
        pipeline.ObserveAudio(AudioActivity.Speech, 0);
        pipeline.TryAcceptTranscript(new("ordered", "Prefix.", false, SourceLanguage: "en"), 0);
        pipeline.TryAcceptTranscript(new("ordered", "Prefix. Suffix", false, SourceLanguage: "en"), .5);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var final = pipeline.CompleteReceivedFinalAsync(new("ordered", "Prefix. Suffix.", true, SourceLanguage: "en"),
            pipeline.CaptureReceivedBlock(pipeline.AudioGeneration), 1);
        Check(calls.SequenceEqual(new[] { "Prefix." }) && !final.IsCompleted, "Final suffix overtook earlier queued source chunk.");
        release.SetResult(("Prefix.", "Prefix."));
        await final.WaitAsync(TimeSpan.FromSeconds(2));
        Check(calls.SequenceEqual(new[] { "Prefix.", "Suffix." }), "Drain replayed prior chunk or translated out of order.");
    }

    private static async Task CheckNaturalRetirementKeepsPrefixAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefix = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captions = new List<CaptionUpdate>();
        CancellationToken prefixToken = default;
        await using var pipeline = new SourceTranslationPipeline((text, _, token) =>
        {
            if (text == "Prefix.") { prefixToken = token; entered.TrySetResult(); return prefix.Task; }
            return Task.FromResult((text, text));
        }, drainTranslate: (text, _, _) => Task.FromResult((text, text)));
        pipeline.Caption += update => { captions.Add(update); if (update.Text == "Fresh.") newer.TrySetResult(); };
        pipeline.ObserveAudio(AudioActivity.Speech, 0);
        pipeline.TryAcceptTranscript(new("retired", "Prefix.", false, SourceLanguage: "en"), 0);
        pipeline.TryAcceptTranscript(new("retired", "Prefix. Suffix", false, SourceLanguage: "en"), 1);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var block = pipeline.CaptureReceivedBlock(pipeline.AudioGeneration);
        var final = pipeline.CompleteReceivedFinalAsync(new("retired", "Prefix. Suffix.", true, SourceLanguage: "en"), block, 1);
        pipeline.ObserveAudio(AudioActivity.Idle, 5);
        Check(pipeline.Assignment is null && !prefixToken.IsCancellationRequested && !final.IsCompleted,
            "Natural retirement canceled consumed prefix work.");
        pipeline.ObserveAudio(AudioActivity.Speech, 6);
        pipeline.TryAcceptTranscript(new("fresh", "Fresh.", true, SourceLanguage: "sw"), 6, pipeline.AudioGeneration);
        await newer.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!prefix.Task.IsCompleted, "New block waited for retired translation.");
        prefix.SetResult(("Prefix.", "Prefix."));
        await final.WaitAsync(TimeSpan.FromSeconds(2));
        Check(captions.Where(update => update.SourceLanguage == "en").Select(update => update.Text).SequenceEqual(new[] { "Prefix.", "Suffix." }),
            "Natural retirement lost consumed prefix or published suffix first.");
        Check(captions.All(pipeline.CanPresentCaption), "Natural retirement made finished chunks ineligible at UI dispatch.");
        pipeline.ResetForNextUtterance();
        Check(captions.All(update => !pipeline.CanPresentCaption(update)),
            "Manual reset failed to invalidate retained block captions.");
    }

    private static async Task CheckRetiredFailuresAsync()
    {
        foreach (var final in new[] { false, true })
        foreach (var reset in new[] { false, true })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var diagnostic = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fatal = false;
            await using var pipeline = new SourceTranslationPipeline((text, _, _) =>
            {
                if (text == "Old.") { entered.TrySetResult(); return failure.Task; }
                return Task.FromResult((text, text));
            });
            pipeline.Failed += _ => fatal = true;
            pipeline.Diagnostic += message => diagnostic.TrySetResult(message);
            pipeline.Caption += update => { if (update.Text == "Fresh.") fresh.TrySetResult(); };
            pipeline.ObserveAudio(AudioActivity.Speech, 0);
            var block = pipeline.CaptureReceivedBlock(pipeline.AudioGeneration);
            var work = final ? pipeline.CompleteReceivedFinalAsync(new("old", "Old.", true, SourceLanguage: "en"), block, 0) : Task.CompletedTask;
            if (!final) pipeline.TryAcceptTranscript(new("old", "Old.", true, SourceLanguage: "en"), 0);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            pipeline.ObserveAudio(AudioActivity.Idle, 5);
            if (reset) pipeline.ResetForNextUtterance();
            pipeline.ObserveAudio(AudioActivity.Speech, 6);
            pipeline.TryAcceptTranscript(new("fresh", "Fresh.", true, SourceLanguage: "sw"), 6);
            await fresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
            failure.SetException(new IOException("secret provider payload"));
            await work.WaitAsync(TimeSpan.FromSeconds(2));
            if (!reset)
            {
                var message = await diagnostic.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Check(!message.Contains("secret") && message.Contains("earlier speech block"), "Retired failure diagnostic exposed provider text or lacked context.");
            }
            // Disposal waits for scheduled work, making silence assertions deterministic.
            await pipeline.DisposeAsync();
            Check(!fatal && (!reset || !diagnostic.Task.IsCompleted), "Retired/reset failure stopped newer work or emitted reset diagnostic.");
        }
    }

    private static async Task CheckFinalOrdersLaterSourcesAsync()
    {
        var release = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var later = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        await using var pipeline = new SourceTranslationPipeline((text, _, _) =>
        {
            calls.Add(text);
            if (text == "First.") return release.Task;
            later.TrySetResult(); return Task.FromResult((text, text));
        });
        pipeline.ObserveAudio(AudioActivity.Speech, 0);
        var first = pipeline.CompleteReceivedFinalAsync(new("first", "First.", true, SourceLanguage: "en"), pipeline.CaptureReceivedBlock(pipeline.AudioGeneration), 0);
        pipeline.TryAcceptTranscript(new("second", "Second.", true, SourceLanguage: "en"), 1);
        Check(!later.Task.IsCompleted, "Later source overtook an asynchronously scheduled final.");
        release.SetResult(("First.", "First."));
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        await later.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(calls.SequenceEqual(new[] { "First.", "Second." }), "Final/source ordering changed.");
    }

}
