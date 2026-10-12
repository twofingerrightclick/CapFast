using System.Net;
using CapFast.Speech;
using CapFast.Core;

internal static class TranslationRecoveryChecks
{
    private const string Success = """{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"English result"}]}}]}""";
    public static async Task RunAsync()
    {
        await CheckLanguageSwitchTimeoutAsync();
        await CheckQueuedAgeAsync();
        await CheckFinalSegmentAgeAsync();
        await CheckEarlyBoundedDetectionAsync();
        await CheckSafetyBlockContinuationAsync();
        await Task.WhenAll(CheckDeadlineAsync(true), CheckDeadlineAsync(false));
        var retry = new FakeHttp(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK, HttpStatusCode.OK);
        using (var translator = new FlashTextTranslator("test-key", retry))
        {
            Require(await translator.TranslateAsync("Tunakutana leo.", default) == "English result", "Transient translation did not recover");
            Require(retry.Bodies.Count == 2 && retry.Bodies[0] == retry.Bodies[1], "Retry changed the fragment or prematurely advanced context");
            await translator.TranslateAsync("Tunaendelea.", default);
            Require(retry.Bodies[2].Contains("Tunakutana leo."), "Successful translation context did not persist");
        }
        var transport = new FakeHttp(HttpStatusCode.OK) { ThrowFirstRequest = true };
        using (var translator = new FlashTextTranslator("test-key", transport))
        {
            Require(await translator.TranslateAsync("source", default) == "English result" && transport.Bodies.Count == 2,
                "Temporary HTTP transport interruption did not recover");
        }
        foreach (var code in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.BadRequest })
        {
            var rejected = new FakeHttp(code);
            using var translator = new FlashTextTranslator("test-key", rejected);
            try { await translator.TranslateAsync("source", default); throw new Exception("Rejected translation succeeded"); }
            catch (CaptionSessionException error) { Require(error.IsTerminalService && !error.IsTransientTranslation, "Terminal service error became skippable"); }
            Require(rejected.Bodies.Count == 1, "Authentication, quota, or configuration error was retried");
        }
        var failing = new FakeHttp(HttpStatusCode.BadGateway, HttpStatusCode.BadGateway, HttpStatusCode.BadGateway);
        using (var translator = new FlashTextTranslator("test-key", failing))
        {
            try { await translator.TranslateAsync("source", default); throw new Exception("Exhausted translation succeeded"); }
            catch (CaptionSessionException error) { Require(error.Message.Contains("502"), "Retry exhaustion lost its safe service code"); }
            Require(failing.Bodies.Count == 3, "Retry budget was not bounded to three attempts");
        }
        using (var capped = System.Text.Json.JsonDocument.Parse("""{"candidates":[{"finishReason":"MAX_TOKENS"}]}"""))
        {
            try { FlashTextTranslator.ParseResponse(capped.RootElement); throw new Exception("Capped translation succeeded"); }
            catch (CaptionSessionException error) { Require(error.IsTransientTranslation, "Output cap failure would stop capture"); }
        }
        var cancelled = new FakeHttp(HttpStatusCode.ServiceUnavailable);
        using (var translator = new FlashTextTranslator("test-key", cancelled))
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = translator.TranslateAsync("source", cancellation.Token);
            cancellation.Cancel();
            try { await pending; throw new Exception("Cancelled translation succeeded"); }
            catch (OperationCanceledException) { }
            Require(cancelled.Bodies.Count == 1, "Cancellation continued retrying translation");
        }
        Console.WriteLine("Translation recovery checks passed: language-switch timeout continuation, live deadlines, unchanged retries, persistent context, terminal errors, bounded exhaustion, and cancellation.");
    }
    private static async Task CheckDeadlineAsync(bool detection)
    {
        using var translator = new FlashTextTranslator("test-key", new StalledHttp(), languageDetection: detection);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await translator.TranslateAsync("Tunakutana leo.", default).WaitAsync(TimeSpan.FromSeconds(detection ? 6 : 7));
            throw new Exception("Stalled translation unexpectedly succeeded");
        }
        catch (CaptionSessionException error)
        {
            Require(error.IsTransientTranslation && error.Message.Contains("timed out"), "Deadline did not produce a recoverable timeout");
            Require(timer.Elapsed.TotalSeconds >= (detection ? 3.5 : 4.5), "Request was canceled before its latency budget");
        }
    }
    private sealed class StalledHttp : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new Exception("Unreachable");
        }
    }
    private static async Task CheckLanguageSwitchTimeoutAsync()
    {
        foreach (var finalDrain in new[] { false, true })
        {
            var handler = new FakeHttp(HttpStatusCode.OK) { TimeoutRequest = 2 };
            using var translator = new FlashTextTranslator("test-key", handler);
            await using var pipeline = new SourceTranslationPipeline(async (text, _, token) =>
                (await translator.TranslateAsync(text, token), "secondary"));
            var results = System.Threading.Channels.Channel.CreateUnbounded<CaptionUpdate>();
            var issue = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pipeline.Caption += update => results.Writer.TryWrite(update);
            pipeline.Failed += _ => issue.TrySetResult(true);
            pipeline.Diagnostic += _ => issue.TrySetResult(false);
            pipeline.ObserveAudio(AudioActivity.Speech, 0);
            async Task Accept(string id, string text, string language, double now)
            {
                var update = new CaptionUpdate(id, text, true, SourceLanguage: language);
                if (finalDrain) await pipeline.CompleteReceivedFinalAsync(update, pipeline.CaptureReceivedBlock(pipeline.AudioGeneration), now);
                else Require(pipeline.TryAcceptTranscript(update, now), "Translation pipeline stopped accepting speech");
            }
            await Accept("english", "Hello everyone.", "en", 0);
            await results.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            pipeline.ObserveAudio(AudioActivity.Idle, 5);
            pipeline.ObserveAudio(AudioActivity.Speech, 6);
            await Accept("swahili-timeout", "Tunakutana leo.", "sw", 6);
            Require(!await issue.Task.WaitAsync(TimeSpan.FromSeconds(2)),
                "Translation timeout after English/silence/Swahili restarted the caption session");
            await Accept("swahili-next", "Tunaendelea pamoja.", "sw", 7);
            var next = await results.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Require(next.SourceLanguage == "sw", "Swahili did not continue after translation timeout");
        }
    }
    private static async Task CheckQueuedAgeAsync()
    {
        var notices = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipeline = new SourceTranslationPipeline(async (_, _, token) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700), token);
            throw new CaptionSessionException("Transient translation failure.", isTransientTranslation: true);
        }, maxTranslationAge: TimeSpan.FromSeconds(1));
        pipeline.Diagnostic += _ => { if (Interlocked.Increment(ref notices) == 7) done.TrySetResult(); };
        pipeline.Failed += _ => done.TrySetException(new Exception("A delayed translation restarted capture."));
        pipeline.ObserveAudio(AudioActivity.Speech, 0);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 7; i++)
            Require(pipeline.TryAcceptTranscript(new($"queued-{i}", $"Sentence {i}.", true,
                SourceLanguage: i % 2 == 0 ? "en" : "sw"), 0), "Queue rejected an accepted segment early");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Require(timer.Elapsed < TimeSpan.FromSeconds(2), "Same-block translation retries accumulated beyond the live caption age limit");
    }
    private static async Task CheckFinalSegmentAgeAsync()
    {
        var captions = new List<CaptionUpdate>();
        var notices = 0;
        var starts = 0;
        await using var pipeline = new SourceTranslationPipeline(async (text, _, token) =>
        {
            Interlocked.Increment(ref starts);
            await Task.Delay(TimeSpan.FromMilliseconds(700), token);
            return (text, text);
        }, maxTranslationAge: TimeSpan.FromSeconds(1));
        pipeline.Caption += update => captions.Add(update);
        pipeline.Diagnostic += _ => Interlocked.Increment(ref notices);
        pipeline.ObserveAudio(AudioActivity.Speech, 0);
        await pipeline.CompleteReceivedFinalAsync(new("long-final", "One. Two. Three.", true, SourceLanguage: "en"),
            pipeline.CaptureReceivedBlock(pipeline.AudioGeneration), 0).WaitAsync(TimeSpan.FromSeconds(3));
        Require(starts == 2 && captions.Select(c => c.Text).SequenceEqual(new[] { "One." }) && notices == 1,
            "A long final restarted the age budget for each sentence or published an expired suffix");
        await pipeline.CompleteReceivedFinalAsync(new("fresh-final", "Fresh.", true, SourceLanguage: "sw"),
            pipeline.CaptureReceivedBlock(pipeline.AudioGeneration), 1).WaitAsync(TimeSpan.FromSeconds(3));
        Require(captions[^1].Text == "Fresh.", "Expired final prevented fresh speech from translating");
    }
    private static async Task CheckEarlyBoundedDetectionAsync()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDetected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothCaptioned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captions = new List<CaptionUpdate>();
        await using (var pipeline = new SourceTranslationPipeline((text, _, _) =>
        {
            if (text == "First.") { firstEntered.TrySetResult(); return releaseFirst.Task; }
            return Task.FromResult((text, text));
        }))
        {
            pipeline.Caption += update => { captions.Add(update); if (captions.Count == 2) bothCaptioned.TrySetResult(); };
            pipeline.ObserveAudio(AudioActivity.Speech, 0);
            Require(pipeline.TryAcceptTranscript(new("first", "First.", true, SourceLanguage: "en"), 0), "First source was rejected");
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Require(pipeline.TryAcceptTranscript(new("second", "Second.", true, SourceLanguage: "auto"), 0,
                detectSegment: (_, _) => { secondDetected.TrySetResult(); return Task.FromResult<string?>("sw"); }),
                "Second source was rejected");
            await secondDetected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Require(captions.Count == 0, "Early detection overtook ordered translation");
            releaseFirst.SetResult(("First.", "First."));
            await bothCaptioned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Require(captions.Select(c => c.SourceLanguage).SequenceEqual(new[] { "en", "sw" }),
            "Early detection changed source translation order or routing");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detectRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var active = 0;
        var maximum = 0;
        var finalCaptions = new List<CaptionUpdate>();
        await using var finals = new SourceTranslationPipeline((text, _, _) =>
        {
            if (text == "Hold.") { entered.TrySetResult(); return release.Task; }
            return Task.FromResult((text, text));
        });
        finals.Caption += finalCaptions.Add;
        finals.ObserveAudio(AudioActivity.Speech, 0);
        finals.TryAcceptTranscript(new("hold", "Hold.", true, SourceLanguage: "en"), 0);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var pending = finals.CompleteReceivedFinalAsync(new("mixed-final", "One. Two. Three. Four.", true,
            SourceLanguage: "auto"), finals.CaptureReceivedBlock(finals.AudioGeneration), 0,
            async (text, token) =>
            {
                var current = Interlocked.Increment(ref active);
                maximum = Math.Max(maximum, current);
                if (Interlocked.Increment(ref starts) == 2) twoStarted.TrySetResult();
                try { await detectRelease.Task.WaitAsync(token); }
                finally { Interlocked.Decrement(ref active); }
                return text == "Two." || text == "Four." ? "sw" : "en";
            });
        await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Require(starts == 2 && maximum == 2 && finalCaptions.Count == 0,
            "Final prefetch exceeded two detectors or translated ahead of earlier source");
        detectRelease.TrySetResult();
        release.TrySetResult(("Hold.", "Hold."));
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Require(starts == 4 && finalCaptions.Select(c => c.SourceLanguage).SequenceEqual(new[]
            { "en", "en", "sw", "en", "sw" }), "Final detector results were assigned out of source order");
    }

    private static async Task CheckSafetyBlockContinuationAsync()
    {
        const string blocked = """{"candidates":[{"finishReason":"PROHIBITED_CONTENT"}]}""";
        using (var feedback = System.Text.Json.JsonDocument.Parse("""{"promptFeedback":{"blockReason":"SAFETY"}}"""))
        {
            try { FlashTextTranslator.ParseResponse(feedback.RootElement); throw new Exception("Blocked response was accepted"); }
            catch (CaptionSessionException error) { Require(error.IsContentBlocked && error.IsTransientTranslation, "Blocked response would stop capture"); }
        }
        var handler = new ResponseHttp(Success, blocked, Success);
        using var translator = new FlashTextTranslator("test-key", handler);
        await using var pipeline = new SourceTranslationPipeline(async (text, _, token) =>
            (await translator.TranslateAsync(text, token), "secondary"));
        var captions = new List<CaptionUpdate>();
        var diagnostics = 0;
        var failed = false;
        pipeline.Caption += captions.Add;
        pipeline.Diagnostic += _ => diagnostics++;
        pipeline.Failed += _ => failed = true;
        async Task Accept(string id, string text) => await pipeline.CompleteReceivedFinalAsync(
            new(id, text, true, SourceLanguage: "sw"), pipeline.CaptureReceivedBlock(pipeline.AudioGeneration), 0);
        await Accept("before", "Habari.");
        await Accept("blocked", "Imebadilishwa.");
        await Accept("after", "Tunaendelea.");
        Require(handler.Calls == 3 && diagnostics == 1 && !failed && captions.Count == 2,
            "Safety refusal was retried, shown, or stopped later captions");
    }

    private sealed class ResponseHttp(params string[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = responses[Math.Min(Calls++, responses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
    private static void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
    private sealed class FakeHttp(params HttpStatusCode[] codes) : HttpMessageHandler
    {
        public int TimeoutRequest { get; init; }
        public bool ThrowFirstRequest { get; init; }
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            if (Bodies.Count == TimeoutRequest) throw new OperationCanceledException();
            if (ThrowFirstRequest && Bodies.Count == 1) throw new HttpRequestException("secret-sentinel");
            var code = codes[Math.Min(Bodies.Count - 1, codes.Length - 1)];
            return new HttpResponseMessage(code) { Content = new StringContent(code == HttpStatusCode.OK ? Success : "private provider body") };
        }
    }
}
