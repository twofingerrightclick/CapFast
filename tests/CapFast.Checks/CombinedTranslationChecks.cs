using System.Net;
using System.Text.Json;
using CapFast.Core;
using CapFast.Speech;

internal static class CombinedTranslationChecks
{
    private const string Success = """{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"{\"sourceLanguage\":\"sw\",\"english\":\"Hello\",\"swahili\":\"Habari\",\"french\":\"Bonjour\"}"}]}}]}""";
    private const string Blocked = """{"candidates":[{"finishReason":"PROHIBITED_CONTENT"}]}""";

    public static async Task RunAsync()
    {
        CheckSchemaAndGuard();
        await CheckProviderFailurePolicyAsync();
        await CheckBoundedHedgeAsync();
        await CheckOrderedBoundedWorkAsync();
        await CheckMixedFragmentRoutingAsync();
        await CheckDominantAndRoutingAsync();
        await CheckLongFinalKeepsEverySegmentAsync();
        await CheckKnownBlockedContextAsync();
        Console.WriteLine("Combined translation checks passed: structured response safety, explicit exhaustion, bounded hedge, ordered two-slot work, dominant routing, durable final, and blocked context.");
    }

    private static void CheckSchemaAndGuard()
    {
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(FlashMultilingualTranslator.CreateRequest(
            "Habari.", "Karibu.", "sw")));
        var config = request.RootElement.GetProperty("generationConfig");
        Require(config.GetProperty("responseMimeType").GetString() == "application/json" &&
            config.GetProperty("responseJsonSchema").GetProperty("required").GetArrayLength() == 4,
            "Combined request did not use the live-verified JSON schema fields");
        Require(request.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text")
            .GetString()!.Contains("Karibu."), "Prior accepted source was omitted from request context");
        var parsed = FlashMultilingualTranslator.ParseStructuredResult(
            """{"sourceLanguage":"sw","english":"Hello","swahili":"Habari","french":"Bonjour"}""");
        Require(parsed.For(SpeechLanguageAssignment.ForSource("sw", 1)) == ("Hello", "Bonjour"),
            "Structured result selected wrong Swahili outputs");
        foreach (var invalid in new[]
        {
            """{"sourceLanguage":"sw","english":"sourceLanguage is sw","swahili":"Habari","french":"Bonjour"}""",
            """{"sourceLanguage":"sw","english":"Hello","swahili":"Habari","french":"previousSource says Bonjour"}""",
            """{"sourceLanguage":"sw","english":"Hello","swahili":"Habari"}"""
        })
        {
            try { FlashMultilingualTranslator.ParseStructuredResult(invalid); throw new Exception("Unsafe result was accepted"); }
            catch (CaptionSessionException error) { Require(error.IsTransientTranslation, "Unsafe structured result became audience text or terminal error"); }
        }
    }

    private static async Task CheckProviderFailurePolicyAsync()
    {
        var handler = new ScriptedHttp(Success, Blocked, Success);
        using var translator = new FlashMultilingualTranslator("test-key", handler);
        Require((await translator.TranslateAsync("Habari.", "", null, default)).SourceLanguage == "sw",
            "Combined response did not parse");
        try { await translator.TranslateAsync("blocked", "", null, default); throw new Exception("Blocked response was accepted"); }
        catch (CaptionSessionException error) { Require(error.IsContentBlocked, "Provider refusal lost its blocked marker"); }
        Require((await translator.TranslateAsync("Tunaendelea.", "", null, default)).English == "Hello" && handler.Calls == 3,
            "Blocked response was retried or stopped the next caption");

        var unavailable = new ScriptedHttp(HttpStatusCode.ServiceUnavailable);
        using var exhausted = new FlashMultilingualTranslator("test-key", unavailable);
        try { await exhausted.TranslateAsync("source", "", null, default); throw new Exception("Exhausted service succeeded"); }
        catch (CaptionSessionException error)
        {
            Require(!error.IsTransientTranslation && CaptionFailurePolicy.RequiresOperatorAction(error.Message),
                "Exhausted accepted speech would be silently skipped or automatically restart");
        }
        Require(unavailable.Calls == 3, "Combined service retries were not bounded");
    }

    private static async Task CheckBoundedHedgeAsync()
    {
        static HttpResponseMessage Ok(string body = Success) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
        var slowOriginal = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterOriginal = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var winner = new ControlledHttp((index, _) => index switch
        {
            0 => slowOriginal.Task,
            1 => Task.FromResult(Ok()),
            2 => laterOriginal.Task,
            _ => Task.FromResult(Ok())
        });
        using (var translator = new FlashMultilingualTranslator("test-key", winner, TimeSpan.FromMilliseconds(20)))
        {
            Require((await translator.TranslateAsync("first", "", null, default).WaitAsync(TimeSpan.FromSeconds(2))).English == "Hello" &&
                winner.Calls == 2, "A slow primary did not yield to one successful reserve request");
            var next = translator.TranslateAsync("next", "first", null, default);
            await winner.WaitForCallsAsync(3);
            await Task.Delay(50);
            Require(winner.Calls == 3, "Reserve was released before the canceled original HTTP call exited");
            laterOriginal.SetResult(Ok());
            await next.WaitAsync(TimeSpan.FromSeconds(2));
            slowOriginal.SetResult(Ok());
        }

        var blockedPrimary = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedPeer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new ControlledHttp((index, _) => index == 0 ? blockedPrimary.Task : blockedPeer.Task);
        using (var translator = new FlashMultilingualTranslator("test-key", blocked, TimeSpan.FromMilliseconds(20)))
        {
            var pending = translator.TranslateAsync("blocked", "", null, default);
            await blocked.WaitForCallsAsync(2);
            blockedPrimary.SetResult(Ok(Blocked));
            try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("Known blocked content was captioned"); }
            catch (CaptionSessionException error) { Require(error.IsContentBlocked && blocked.Calls == 2,
                "Known refusal was retried or hidden by a pending hedge"); }
            blockedPeer.SetResult(Ok());
        }

        var transientPrimary = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transientPeer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transient = new ControlledHttp((index, _) => index == 0 ? transientPrimary.Task : transientPeer.Task);
        using (var translator = new FlashMultilingualTranslator("test-key", transient, TimeSpan.FromMilliseconds(20)))
        {
            var pending = translator.TranslateAsync("source", "", null, default);
            await transient.WaitForCallsAsync(2);
            transientPrimary.SetResult(new(HttpStatusCode.ServiceUnavailable));
            transientPeer.SetResult(Ok());
            Require((await pending.WaitAsync(TimeSpan.FromSeconds(2))).English == "Hello" && transient.Calls == 2,
                "Transient first completion prevented a valid peer result");
        }
    }

    private static async Task CheckOrderedBoundedWorkAsync()
    {
        var first = new TaskCompletionSource<MultilingualCaptionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<MultilingualCaptionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allCaptioned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<(string Text, string Previous)>();
        var captions = new List<CaptionUpdate>();
        await using var pipeline = CombinedPipeline(async (text, previous, _, token) =>
        {
            lock (calls)
            {
                calls.Add((text, previous));
                if (calls.Count == 2) twoStarted.TrySetResult();
                if (text == "Three.") thirdStarted.TrySetResult();
            }
            if (text == "One.") return await first.Task.WaitAsync(token);
            if (text == "Two.") return await second.Task.WaitAsync(token);
            return Result("en", text);
        }, age: TimeSpan.FromMilliseconds(20));
        pipeline.Caption += caption => { captions.Add(caption); if (captions.Count == 3) allCaptioned.TrySetResult(); };
        pipeline.ObserveAudio(AudioActivity.Speech, 0);
        foreach (var (id, text) in new[] { ("one", "One."), ("two", "Two."), ("three", "Three.") })
            Require(pipeline.TryAcceptTranscript(new(id, text, true, SourceLanguage: "auto"), 0),
                "Combined source was rejected during normal acceptance");
        await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(!thirdStarted.Task.IsCompleted && calls[1].Previous == "One.",
            "Combined requests exceeded two slots or lost acceptance-order context");
        second.SetResult(Result("sw", "Two."));
        await thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(captions.Count == 0, "Later completed source overtook earlier caption");
        first.SetResult(Result("en", "One."));
        await allCaptioned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(captions.Select(c => c.UtteranceId).SequenceEqual(new[]
            { "1:one:stream-chunk:0", "1:two:stream-chunk:0", "1:three:stream-chunk:0" }),
            "Combined captions did not preserve source order");
        Require(captions[0].Text == "sw-One." && captions[1].Text == "en-Two.",
            "Combined captions ignored each fragment's language while preserving order");
    }

    private static async Task CheckDominantAndRoutingAsync()
    {
        var captions = new List<CaptionUpdate>();
        await using var pipeline = CombinedPipeline((text, _, _, _) => Task.FromResult(Result(
            text.StartsWith("Welcome") || text == "Amen." ? "en" : "sw", text)));
        pipeline.Caption += captions.Add;
        pipeline.ObserveAudio(AudioActivity.Speech, 0);
        async Task Accept(string id, string text) => await pipeline.CompleteReceivedFinalAsync(
            new(id, text, true, SourceLanguage: "auto"), pipeline.CaptureReceivedBlock(pipeline.AudioGeneration), 0);
        await Accept("intro", "Welcome friends to this meeting.");
        Require(pipeline.Assignment?.Input == "en", "Brief genuine English opening was not indicated");
        await Accept("sw1", "Tunamshukuru Bwana wetu kwa baraka.");
        await Accept("borrowed", "Amen.");
        Require(pipeline.Assignment?.Input == "en", "One borrowed English word flipped or canceled dominant evidence");
        await Accept("sw2", "Tunaendelea pamoja katika imani yetu.");
        Require(pipeline.Assignment?.Input == "sw", "Sustained Swahili did not become dominant");
        Require(captions.Select(c => c.PrimaryLanguage).SequenceEqual(new[] { "sw", "en", "sw", "en" }) &&
            captions.All(c => c.SecondaryLanguage == "fr"),
            "Dominant indicator replaced per-fragment English/Swahili routing");
    }

    private static async Task CheckMixedFragmentRoutingAsync()
    {
        var captions = new List<CaptionUpdate>();
        await using var pipeline = CombinedPipeline((text, _, _, _) => Task.FromResult(Result(
            text.StartsWith("In Elder", StringComparison.Ordinal) ? "en" : "sw", text)));
        pipeline.Caption += captions.Add;
        pipeline.ObserveAudio(AudioActivity.Speech, 0);
        async Task Accept(string id, string text) => await pipeline.CompleteReceivedFinalAsync(
            new(id, text, true, SourceLanguage: "auto"), pipeline.CaptureReceivedBlock(pipeline.AudioGeneration), 0);
        await Accept("english-majority", "In Elder Patrick Kearon's remarks, kama vile");
        await Accept("swahili-majority", "Kama vile Christine na mimi tulivyohisi uzito");
        Require(captions.Select(c => (c.SourceLanguage!, c.PrimaryLanguage!, c.SecondaryLanguage!))
            .SequenceEqual(new[] { ("en", "sw", "fr"), ("sw", "en", "fr") }),
            "Mixed supported-language fragments did not route by each fragment's predominant source");
    }

    private static async Task CheckLongFinalKeepsEverySegmentAsync()
    {
        var captions = new List<CaptionUpdate>();
        await using var pipeline = CombinedPipeline(async (text, _, _, token) =>
        {
            await Task.Delay(80, token);
            return Result("sw", text);
        }, age: TimeSpan.FromMilliseconds(20));
        pipeline.Caption += captions.Add;
        pipeline.ObserveAudio(AudioActivity.Speech, 0);
        await pipeline.CompleteReceivedFinalAsync(new("long", "One. Two. Three. Four. Five. Six. Seven. Eight. Nine. Ten.",
            true, SourceLanguage: "auto"), pipeline.CaptureReceivedBlock(pipeline.AudioGeneration), 0)
            .WaitAsync(TimeSpan.FromSeconds(3));
        Require(captions.Count == 10 && captions.Select(c => c.Text).SequenceEqual(new[]
        { "en-One.", "en-Two.", "en-Three.", "en-Four.", "en-Five.", "en-Six.", "en-Seven.", "en-Eight.", "en-Nine.", "en-Ten." }),
            "Long final dropped a suffix because an accepted source aged in the queue");
    }

    private static async Task CheckKnownBlockedContextAsync()
    {
        var releaseFirst = new TaskCompletionSource<MultilingualCaptionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocked = new TaskCompletionSource<MultilingualCaptionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoCaptioned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captions = new List<CaptionUpdate>();
        await using var pipeline = CombinedPipeline(async (text, previous, _, token) =>
        {
            if (text == "First.") return await releaseFirst.Task.WaitAsync(token);
            if (text == "Blocked.") return await releaseBlocked.Task.WaitAsync(token);
            nextStarted.TrySetResult(previous);
            return Result("sw", text);
        });
        pipeline.Caption += caption => { captions.Add(caption); if (captions.Count == 2) twoCaptioned.TrySetResult(); };
        pipeline.ObserveAudio(AudioActivity.Speech, 0);
        foreach (var (id, text) in new[] { ("first", "First."), ("blocked", "Blocked."), ("next", "Next.") })
            Require(pipeline.TryAcceptTranscript(new(id, text, true, SourceLanguage: "auto"), 0),
                "Blocked-context source was rejected");
        releaseBlocked.SetException(new CaptionSessionException("blocked", isTransientTranslation: true, isContentBlocked: true));
        Require(await nextStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)) == "",
            "Known blocked source was resent as previousSource in waiting request");
        releaseFirst.SetResult(Result("sw", "First."));
        await twoCaptioned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(captions.Select(c => c.Text).SequenceEqual(new[] { "en-First.", "en-Next." }),
            "Blocked source entered captions or stopped later source");
    }

    private static SourceTranslationPipeline CombinedPipeline(
        Func<string, string, string?, CancellationToken, Task<MultilingualCaptionResult>> translate,
        TimeSpan? age = null) => new((_, _, _) => throw new Exception("Legacy path invoked"),
            maxTranslationAge: age, capacity: 32, translateMultilingual: translate);

    private static MultilingualCaptionResult Result(string language, string text) =>
        new(language, "en-" + text, "sw-" + text, "fr-" + text);

    private static void Require(bool value, string reason) { if (!value) throw new Exception(reason); }

    private sealed class ScriptedHttp : HttpMessageHandler
    {
        private readonly string[] _responses;
        private readonly HttpStatusCode? _status;
        public int Calls { get; private set; }
        public ScriptedHttp(params string[] responses) => _responses = responses;
        public ScriptedHttp(HttpStatusCode status) { _responses = []; _status = status; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Calls++;
            return Task.FromResult(new HttpResponseMessage(_status ?? HttpStatusCode.OK)
            { Content = new StringContent(_status is null ? _responses[Math.Min(index, _responses.Length - 1)] : "private body") });
        }
    }

    private sealed class ControlledHttp(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(Interlocked.Increment(ref _calls) - 1, cancellationToken);
        public async Task WaitForCallsAsync(int expected)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (Calls < expected && DateTime.UtcNow < deadline) await Task.Delay(5);
            Require(Calls >= expected, $"Timed out waiting for {expected} HTTP calls");
        }
    }
}
