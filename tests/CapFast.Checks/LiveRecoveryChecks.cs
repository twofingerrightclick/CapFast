using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CapFast.Core;
using CapFast.Speech;

public static class LiveRecoveryChecks
{
    private sealed class Clock : ILiveClock
    {
        public double Now { get; set; }
        public List<double> Backoffs { get; } = new();
        public async Task Delay(TimeSpan delay, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (delay.TotalSeconds >= .5) Backoffs.Add(delay.TotalSeconds);
            Now += delay.TotalSeconds;
            await Task.Yield();
            token.ThrowIfCancellationRequested();
        }
    }
    private sealed class Transport : ILiveTransport
    {
        private readonly Channel<LiveMessage> _messages = Channel.CreateUnbounded<LiveMessage>();
        public int HttpStatus { get; set; }
        public bool Aborted;
        public bool Disposed;
        public int Setups;
        public int EndMarkers;
        public List<byte[]> Audio = new();
        public Func<Transport, Task>? Connected;
        public Func<Transport, Task>? AudioSent;
        public Action<LiveMessage>? MessageReceived;
        public Task ConnectAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Connected?.Invoke(this) ?? Task.CompletedTask; }
        public async Task SendAsync(byte[] data, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Aborted) throw new WebSocketException();
            using var json = JsonDocument.Parse(data);
            if (json.RootElement.TryGetProperty("setup", out _)) { Setups++; Emit("{\"setupComplete\":{}}"); }
            else if (json.RootElement.TryGetProperty("realtimeInput", out var realtime))
            {
                if (realtime.TryGetProperty("audio", out var audio))
                {
                    Audio.Add(Convert.FromBase64String(audio.GetProperty("data").GetString()!));
                    if (AudioSent is not null) await AudioSent(this);
                }
                if (realtime.TryGetProperty("audioStreamEnd", out _))
                {
                    EndMarkers++;
                    Emit("{\"serverContent\":{\"inputTranscription\":{\"text\":\"Finished.\"},\"turnComplete\":true}}");
                }
            }
            await Task.Yield();
        }
        public async Task<LiveMessage> ReceiveAsync(CancellationToken token)
        {
            var message = await _messages.Reader.ReadAsync(token);
            MessageReceived?.Invoke(message);
            return message;
        }
        public void Emit(string json) => _messages.Writer.TryWrite(new(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text));
        public void Close(int code, string? reason = null) => _messages.Writer.TryWrite(new(Array.Empty<byte>(), WebSocketMessageType.Close, code, reason));
        public void Abort() => Aborted = true;
        public void Dispose() { Disposed = true; }
    }
    private static ClassifiedAudioFrame Frame(int id, AudioFrameKind kind = AudioFrameKind.Speech)
    { var bytes = new byte[3200]; BitConverter.GetBytes(id).CopyTo(bytes, 0); return new(bytes, kind); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static Channel<ClassifiedAudioFrame> Frames(int count)
    {
        var channel = Channel.CreateUnbounded<ClassifiedAudioFrame>();
        for (var i = 0; i < count; i++) channel.Writer.TryWrite(Frame(i));
        channel.Writer.Complete(); return channel;
    }
    private static GeminiCaptionSession Session(Func<ILiveTransport> factory, Clock clock) =>
        new("fake-test-key", null, factory, clock,
            detectLanguage: (_, _) => Task.FromResult<string?>("sw"),
            drainTranslate: (source, _, _) => Task.FromResult(("English " + source, "French " + source)));
    private static async Task CheckLanguageFailureContinuesSpeechAsync(bool unknown = false)
    {
        var socket = new Transport();
        socket.AudioSent = transport =>
        {
            if (transport.Audio.Count == 20)
            {
                transport.Emit("{\"serverContent\":{\"inputTranscription\":{\"text\":\"First words of continuous speech.\"}}}");
                transport.Emit("{\"serverContent\":{\"inputTranscription\":{\"text\":\"Second words continue the same speech.\"}}}");
                transport.Emit("{\"serverContent\":{\"inputTranscription\":{\"text\":\"Third words continue the same speech.\"}}}");
            }
            return Task.CompletedTask;
        };
        var detectionCalls = 0;
        var thirdCaption = new TaskCompletionSource<CaptionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = false;
        var diagnostics = new List<string>();
        await using var session = new GeminiCaptionSession("fake-test-key", null, () => socket, new Clock(),
            detectLanguage: (_, _) =>
            {
                if (++detectionCalls == 3)
                {
                    if (unknown) return Task.FromResult<string?>(null);
                    throw new CaptionSessionException("Google did not complete the language code translation (finishReason: MAX_TOKENS).");
                }
                return Task.FromResult<string?>(detectionCalls == 1 ? "en" : "sw");
            },
            drainTranslate: (source, _, _) => Task.FromResult((source, source)));
        session.Caption += update =>
        {
            if (update.Text.Contains("Third words", StringComparison.Ordinal)) thirdCaption.TrySetResult(update);
        };
        session.Failure += _ => failed = true;
        session.Diagnostic += diagnostics.Add;
        await session.RunClassifiedAudioAsync(Frames(20).Reader).WaitAsync(TimeSpan.FromSeconds(3));
        var last = await thirdCaption.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(detectionCalls == 3 && last.SourceLanguage == "auto" &&
            last.PrimaryLanguage == "en" && last.SecondaryLanguage == "fr" &&
            !failed && !socket.Aborted &&
            (unknown || diagnostics.Any(message => message.Contains("automatic source translation", StringComparison.OrdinalIgnoreCase))),
            unknown ? "An unknown language result discarded continuous speech." :
                "A failed language check stopped translation or audio capture during continuous speech.");
    }
    private static async Task CheckRepeatedInitialLanguageFailuresAsync()
    {
        var socket = new Transport();
        socket.AudioSent = transport =>
        {
            if (transport.Audio.Count == 20)
                for (var i = 0; i < 3; i++)
                    transport.Emit($"{{\"serverContent\":{{\"inputTranscription\":{{\"text\":\"Habari za leo {i}.\"}}}}}}");
            return Task.CompletedTask;
        };
        var captions = new List<CaptionUpdate>();
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = false;
        await using var session = new GeminiCaptionSession("fake-test-key", null, () => socket, new Clock(),
            detectLanguage: (_, _) => throw new CaptionSessionException("Google did not complete the language code translation (finishReason: MAX_TOKENS)."),
            drainTranslate: (source, assignment, _) => Task.FromResult(($"{assignment.Primary}:{source}", $"{assignment.Secondary}:{source}")));
        session.Caption += update => { captions.Add(update); if (captions.Count == 3) third.TrySetResult(); };
        session.Failure += _ => failed = true;
        await session.RunClassifiedAudioAsync(Frames(20).Reader).WaitAsync(TimeSpan.FromSeconds(3));
        await third.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!failed && captions.All(update => update.SourceLanguage == "auto" && update.PrimaryLanguage == "en" && update.SecondaryLanguage == "fr"),
            "Repeated initial language-check failures starved translation instead of using automatic source translation.");
    }
    private static async Task CheckShortInterimWaitsForLanguageAsync()
    {
        var socket = new Transport();
        socket.AudioSent = transport =>
        {
            if (transport.Audio.Count == 20)
            {
                transport.Emit("{\"serverContent\":{\"inputTranscription\":{\"text\":\"Hello everyone.\"}}}");
                transport.Emit("{\"serverContent\":{\"interimInputTranscription\":{\"text\":\"Habari za leo.\"}}}");
                transport.Emit("{\"serverContent\":{\"interimInputTranscription\":{\"text\":\"Habari za leo.\"}}}");
                transport.Emit("{\"serverContent\":{\"inputTranscription\":{\"text\":\"Habari za leo.\"}}}");
            }
            return Task.CompletedTask;
        };
        var captions = new List<CaptionUpdate>();
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new GeminiCaptionSession("fake-test-key", null, () => socket, new Clock(),
            detectLanguage: (source, _) => Task.FromResult<string?>(source.StartsWith("Habari", StringComparison.Ordinal) ? "sw" : "en"),
            drainTranslate: (source, assignment, _) => Task.FromResult(($"{assignment.Primary}:{source}", $"{assignment.Secondary}:{source}")));
        session.Caption += update => { captions.Add(update); if (captions.Count >= 2) enough.TrySetResult(); };
        await session.RunClassifiedAudioAsync(Frames(20).Reader).WaitAsync(TimeSpan.FromSeconds(3));
        await enough.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(captions.Count(update => update.Text.Contains("Habari", StringComparison.Ordinal)) == 1 &&
            captions.Any(update => update.SourceLanguage == "sw" && update.PrimaryLanguage == "en" && update.Text.Contains("Habari", StringComparison.Ordinal)),
            "A short Swahili interim was translated as the prior English utterance before final classification.");
    }
    private static async Task CheckMetadataOverridesFailedLanguageCheckAsync()
    {
        var socket = new Transport();
        socket.AudioSent = transport =>
        {
            if (transport.Audio.Count == 20)
            {
                transport.Emit("""{"serverContent":{"inputTranscription":{"text":"Initial words."}}}""");
                transport.Emit("""{"serverContent":{"inputTranscription":{"text":"Hello everyone.","languageCode":"en"}}}""");
                transport.Emit("""{"serverContent":{"inputTranscription":{"text":"Habari za leo.","languageCode":"sw"}}}""");
            }
            return Task.CompletedTask;
        };
        var calls = 0;
        var captions = new List<CaptionUpdate>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new GeminiCaptionSession("fake-test-key", null, () => socket, new Clock(),
            detectLanguage: (_, _) => { ++calls; throw new CaptionSessionException("Language check unavailable."); },
            drainTranslate: (source, _, _) => Task.FromResult((source, source)));
        session.Caption += update => { captions.Add(update); if (captions.Count == 3) done.TrySetResult(); };
        await session.RunClassifiedAudioAsync(Frames(20).Reader).WaitAsync(TimeSpan.FromSeconds(3));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(calls == 1 && captions.Select(update => update.SourceLanguage).SequenceEqual(new[] { "auto", "en", "sw" }) &&
            captions[1].PrimaryLanguage == "sw" && captions[2].PrimaryLanguage == "en",
            "A failed language check suppressed later valid language metadata or its switched output pair.");
    }

    private static async Task CheckRetiredLatchedSourcesAsync()
    {
        var clock = new Clock();
        var sockets = new List<Transport>();
        var oldAssignment = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newAssignment = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var final = new TaskCompletionSource<CaptionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new GeminiCaptionSession("fake-test-key", null, () =>
        {
            var socket = new Transport(); sockets.Add(socket);
            if (sockets.Count == 1)
            {
                socket.MessageReceived = message =>
                {
                    if (Encoding.UTF8.GetString(message.Data).Contains("queuedMarker", StringComparison.Ordinal)) queued.TrySetResult();
                };
                socket.AudioSent = async transport =>
                {
                    if (transport.Audio.Count != 20) return;
                    transport.Emit("""{"serverContent":{"interimInputTranscription":{"text":"Habari","languageCode":"sw"}}}""");
                    await oldAssignment.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    // Close the provider utterance carrying metadata; fail the next check.
                    transport.Emit("""{"serverContent":{"inputTranscription":{"text":"Habari.","languageCode":"sw"}}}""");
                    transport.Emit("""{"serverContent":{"inputTranscription":{"text":"Failed check."}}}""");
                    transport.Emit("""{"serverContent":{"inputTranscription":{"text":"Queued old final."}}}""");
                    transport.Emit("""{"queuedMarker":true}""");
                    await failedCheck.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    await queued.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    clock.Now = 5;
                };
            }
            else socket.AudioSent = transport =>
            {
                if (transport.Audio.Count == 1)
                    transport.Emit("""{"serverContent":{"interimInputTranscription":{"text":"Hello","languageCode":"en"}}}""");
                return Task.CompletedTask;
            };
            return socket;
        }, clock, detectLanguage: (_, _) => throw new CaptionSessionException("Language check unavailable."),
            drainTranslate: (source, _, _) => Task.FromResult((source, source)));
        session.LanguageAssignmentChanged += assignment =>
        {
            if (assignment?.Input == "sw") oldAssignment.TrySetResult();
            if (assignment?.Input == "en") newAssignment.TrySetResult();
        };
        session.Diagnostic += message =>
        {
            if (!message.Contains("Google language check failed", StringComparison.Ordinal)) return;
            failedCheck.TrySetResult();
            // Hold this reader after its latch is set while the audio pump starts a new block.
            newAssignment.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        };
        session.Caption += update => { if (update.Text == "Queued old final.") final.TrySetResult(update); };
        var frames = Channel.CreateUnbounded<ClassifiedAudioFrame>();
        for (var i = 0; i < 20; i++) frames.Writer.TryWrite(Frame(i));
        frames.Writer.TryWrite(Frame(20, AudioFrameKind.Quiet));
        for (var i = 21; i < 41; i++) frames.Writer.TryWrite(Frame(i));
        frames.Writer.Complete();
        await session.RunClassifiedAudioAsync(frames.Reader).WaitAsync(TimeSpan.FromSeconds(4));
        var completed = await final.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(completed.SourceLanguage == "sw" && completed.PrimaryLanguage == "en" && session.LanguageAssignment?.Input == "en",
            "A retired latched final borrowed the next speech block's source language.");
    }

    private static async Task CheckPendingFinalAcrossIdleAsync()
    {
        var clock = new Clock();
        var sockets = new List<Transport>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var language = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caption = new TaskCompletionSource<CaptionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newAssignment = new TaskCompletionSource<LanguageAssignment>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new GeminiCaptionSession("fake-test-key", null, () =>
        {
            var socket = new Transport(); sockets.Add(socket);
            if (sockets.Count == 1) socket.AudioSent = async t =>
            {
                if (t.Audio.Count != 20) return;
                t.Emit("{\"serverContent\":{\"inputTranscription\":{\"text\":\"Thank you.\"}}}");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                clock.Now = 5;
            };
            else socket.AudioSent = t =>
            {
                if (t.Audio.Count == 1) t.Emit("{\"serverContent\":{\"interimInputTranscription\":{\"text\":\"Habari\",\"languageCode\":\"sw\"}}}");
                return Task.CompletedTask;
            };
            return socket;
        }, clock, detectLanguage: (_, _) => { entered.TrySetResult(); return language.Task; },
            drainTranslate: (text, assignment, _) => Task.FromResult(($"{assignment.Primary}:{text}", $"{assignment.Secondary}:{text}")));
        session.Caption += update => caption.TrySetResult(update);
        session.LanguageAssignmentChanged += assignment => { if (assignment?.Input == "sw") newAssignment.TrySetResult(assignment); };
        var frames = Channel.CreateUnbounded<ClassifiedAudioFrame>();
        for (var i = 0; i < 20; i++) frames.Writer.TryWrite(Frame(i));
        frames.Writer.TryWrite(Frame(20, AudioFrameKind.Quiet));
        for (var i = 21; i < 41; i++) frames.Writer.TryWrite(Frame(i));
        frames.Writer.Complete();
        await session.RunClassifiedAudioAsync(frames.Reader).WaitAsync(TimeSpan.FromSeconds(3));
        await newAssignment.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(sockets.Count == 2 && sockets[0].Disposed, "Pending classifier blocked next-block stream creation.");
        language.SetResult("en");
        var completed = await caption.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(completed.IsSpeechBlockDrain && completed.PrimaryLanguage == "sw" && completed.SecondaryLanguage == "fr",
            "Closing idle socket lost already received text-only final.");
        Check(session.LanguageAssignment?.Input == "sw", "Delayed final reclassified new block.");
        session.ResetLanguageDetection();
        Check(!session.CanPresentCaption(completed), "Reset allowed queued drained result after dispatch.");
    }
    public static async Task RunAsync()
    {
        await CheckConfiguredGateSurvivesStreamResetAsync();
        await CheckMixedUtteranceSegmentsAsync();
        await CheckHintPersistsThroughFinalAsync();
        await CheckBlockedDetectionSkipsSegmentAsync();
        await CheckLanguageCheckRetriesAsync();
        await CheckLanguageFailureContinuesSpeechAsync();
        await CheckLanguageFailureContinuesSpeechAsync(unknown: true);
        await CheckRepeatedInitialLanguageFailuresAsync();
        await CheckShortInterimWaitsForLanguageAsync();
        await CheckMetadataOverridesFailedLanguageCheckAsync();
        await CheckRetiredLatchedSourcesAsync();
        await CheckPendingFinalAcrossIdleAsync();
        // A five-second speech gap retires the recognition stream before uploading
        // the next block, even if its first old transcript has never been seen.
        var blockClock = new Clock();
        var blockSockets = new List<Transport>();
        await using (var blockSession = new GeminiCaptionSession("fake-test-key", null, () =>
        {
            var socket = new Transport(); blockSockets.Add(socket);
            if (blockSockets.Count == 1)
                socket.AudioSent = t => { if (t.Audio.Count == 20) blockClock.Now = 5; return Task.CompletedTask; };
            else socket.AudioSent = t =>
            {
                if (t.Audio.Count == 1)
                {
                    blockSockets[0].Emit("{\"serverContent\":{\"inputTranscription\":{\"text\":\"Old English.\",\"languageCode\":\"en\"}}}");
                    t.Emit("{\"serverContent\":{\"interimInputTranscription\":{\"text\":\"Habari\",\"languageCode\":\"sw\"}}}");
                }
                return Task.CompletedTask;
            };
            return socket;
        }, blockClock))
        {
            var blockActivities = new List<AudioActivity>();
            blockSession.AudioActivityChanged += blockActivities.Add;
            var detected = new TaskCompletionSource<LanguageAssignment>(TaskCreationOptions.RunContinuationsAsynchronously);
            blockSession.LanguageAssignmentChanged += assignment => { if (assignment is not null) detected.TrySetResult(assignment); };
            var blockFrames = Channel.CreateUnbounded<ClassifiedAudioFrame>();
            for (var i = 0; i < 20; i++) blockFrames.Writer.TryWrite(Frame(i));
            blockFrames.Writer.TryWrite(Frame(20, AudioFrameKind.Quiet));
            for (var i = 21; i < 41; i++) blockFrames.Writer.TryWrite(Frame(i));
            blockFrames.Writer.Complete();
            await blockSession.RunClassifiedAudioAsync(blockFrames.Reader);
            var assignment = await detected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(assignment.Input == "sw" && blockSockets.Count == 2 && blockSockets[0].Disposed,
                "Late unseen old transcript crossed five-second stream boundary.");
            Check(blockActivities.SequenceEqual(new[] { AudioActivity.Idle, AudioActivity.Speech, AudioActivity.Idle, AudioActivity.Speech }), "Awaiting speech block left activity indicator stale.");
            Check(blockSockets.SelectMany(t => t.Audio).Count() == 40, "Speech-block reconnection lost qualified speech audio.");
        }
        // Proactive rollover during continuous speech preserves gate qualification and
        // ordered unsent audio rather than requiring another two seconds of speech.
        var clock = new Clock();
        var sockets = new List<Transport>();
        await using (var session = Session(() =>
        {
            var socket = new Transport(); sockets.Add(socket);
            if (sockets.Count == 1) socket.AudioSent = t => { if (t.Audio.Count == 20) clock.Now = 481; return Task.CompletedTask; };
            return socket;
        }, clock))
        {
            var captions = Channel.CreateUnbounded<CaptionUpdate>();
            session.Caption += update => { if (update.IsFinal) captions.Writer.TryWrite(update); };
            await session.RunClassifiedAudioAsync(Frames(30).Reader);
            var oldCaption = await captions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            sockets[1].Emit("{\"serverContent\":{\"inputTranscription\":{\"text\":\"Finished.\"},\"turnComplete\":true}}");
            var freshCaption = await captions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            sockets[0].Emit("{\"serverContent\":{\"inputTranscription\":{\"text\":\"Stale socket text.\"},\"turnComplete\":true}}");
            await Task.Yield();
            Check(oldCaption.UtteranceId != freshCaption.UtteranceId && !captions.Reader.TryRead(out _), "Fresh IDs collided or a retired socket delivered stale captions.");
            var ids = sockets.SelectMany(s => s.Audio).Select(a => BitConverter.ToInt32(a)).ToArray();
            Check(sockets.Count == 2 && sockets[0].EndMarkers == 1 && sockets[0].Disposed, "Proactive rollover did not drain and dispose the old connection.");
            Check(ids.SequenceEqual(Enumerable.Range(0, 30)), "Rollover duplicated, reordered, or lost queued audio.");
            Check(sockets.All(s => s.Setups == 1), "Rollover repeated setup within a socket.");
        }
        // goAway is a rollover request, not a terminal caption failure.
        clock = new(); sockets = new();
        await using (var session = Session(() =>
        {
            var socket = new Transport(); sockets.Add(socket);
            if (sockets.Count == 1) socket.AudioSent = async t =>
            { if (t.Audio.Count == 20) { t.Emit("{\"goAway\":{\"timeLeft\":\"30s\"}}"); await Task.Delay(10); } };
            return socket;
        }, clock))
        {
            await session.RunClassifiedAudioAsync(Frames(30).Reader);
            Check(sockets.Count == 2 && sockets.Sum(s => s.Audio.Count) == 30, "goAway stopped capture or lost accepted audio.");
        }
        // A failed send may have arrived at the provider. Never replay the uncertain
        // frame blindly; later queued frames still go exactly once and emit a notice.
        clock = new(); sockets = new(); var notices = new List<string>();
        await using (var session = Session(() =>
        {
            var socket = new Transport(); sockets.Add(socket);
            if (sockets.Count == 1) socket.AudioSent = t => t.Audio.Count == 5 ? Task.FromException(new WebSocketException()) : Task.CompletedTask;
            return socket;
        }, clock))
        {
            session.Diagnostic += notices.Add;
            await session.RunClassifiedAudioAsync(Frames(30).Reader);
            Check(sockets.Count == 2 && sockets.SelectMany(s => s.Audio).Select(a => BitConverter.ToInt32(a)).SequenceEqual(Enumerable.Range(0, 30)), "Ambiguous send was replayed or later audio was lost.");
            Check(notices.Count == 1 && clock.Backoffs.SequenceEqual(new[] { .5 }), "Transport recovery did not warn once and back off.");
        }
        // Network receiver interruption recovers; policy and authentication closes do not.
        foreach (var (code, reason, retry) in new[] { (1006, "", true), (1008, "policy", false), (1011, "invalid api key", false), (1011, "internal error RESOURCE_EXHAUSTED", false), (1011, "internal error rate limit", false), (1006, "invalid api key", false) })
        {
            clock = new(); sockets = new();
            await using var session = Session(() =>
            {
                var socket = new Transport(); sockets.Add(socket);
                if (sockets.Count == 1) socket.AudioSent = async t => { if (t.Audio.Count == 20) { t.Close(code, reason); await Task.Delay(10); } };
                return socket;
            }, clock);
            try
            {
                await session.RunClassifiedAudioAsync(Frames(30).Reader);
                Check(retry && sockets.Count == 2, "A terminal server close was retried.");
            }
            catch (CaptionSessionException)
            { Check(!retry && sockets.Count == 1, "A transient receive interruption failed instead of recovering."); }
        }
        clock = new(); sockets = new();
        await using (var session = Session(() =>
        {
            var socket = new Transport(); sockets.Add(socket);
            socket.AudioSent = async t =>
            {
                if (t.Audio.Count == 20)
                {
                    t.Emit("{\"goAway\":{}}");
                    t.Close(1000, "RESOURCE_EXHAUSTED");
                    await Task.Delay(10);
                }
            };
            return socket;
        }, clock))
        {
            try { await session.RunClassifiedAudioAsync(Frames(30).Reader); throw new Exception("goAway bypassed a terminal quota reason."); }
            catch (CaptionSessionException) { Check(sockets.Count == 1, "goAway retried a terminal quota close."); }
        }
        // Connection failures consume one finite budget, including successful setup
        // followed by repeated immediate interruptions; no unbounded retry loop.
        clock = new(); var attempts = 0;
        await using (var session = Session(() => { attempts++; return new Transport { Connected = _ => Task.FromException(new WebSocketException()) }; }, clock))
        {
            try { await session.RunClassifiedAudioAsync(Frames(30).Reader); throw new Exception("Retry exhaustion did not fail."); }
            catch (CaptionSessionException error)
            { Check(error.Message.Contains("three attempts") && attempts == 4 && clock.Backoffs.SequenceEqual(new[] { .5, 1d, 2d }), "Recovery attempts/backoff were not bounded."); }
        }
        // Pause cancels a reconnect in progress and prevents subsequent audio/upload.
        clock = new(); sockets = new(); var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellable = new CancellableTransport(connecting, hold);
        var first = new Transport { AudioSent = _ => Task.FromException(new WebSocketException()) };
        var factoryCalls = 0;
        var pausedSession = Session(() => ++factoryCalls == 1 ? first : cancellable, clock);
        var pumping = pausedSession.RunClassifiedAudioAsync(Frames(30).Reader);
        await connecting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await pausedSession.DisposeAsync();
        try { await pumping.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { }
        Check(factoryCalls == 2 && first.Audio.Count == 1 && cancellable.Sends == 0, "Pause allowed reconnect or queued audio to continue.");
        Console.WriteLine("Live recovery checks passed: rollover, goAway, ordered audio, uncertain-send notice, terminal errors, bounded backoff, and pause cancellation.");
    }
    private static async Task CheckConfiguredGateSurvivesStreamResetAsync()
    {
        var clock = new Clock();
        var sockets = new List<Transport>();
        await using var session = new GeminiCaptionSession("fake-test-key", null, () =>
        {
            var socket = new Transport(); sockets.Add(socket);
            if (sockets.Count == 1)
                socket.AudioSent = t => { if (t.Audio.Count == 15) clock.Now = 5; return Task.CompletedTask; };
            return socket;
        }, clock, speechQualificationSeconds: 1.5, silenceResetSeconds: .4);
        var frames = Channel.CreateUnbounded<ClassifiedAudioFrame>();
        for (var i = 0; i < 15; i++) frames.Writer.TryWrite(Frame(i));
        frames.Writer.TryWrite(Frame(99, AudioFrameKind.Quiet));
        for (var i = 15; i < 30; i++) frames.Writer.TryWrite(Frame(i));
        for (var i = 0; i < 4; i++) frames.Writer.TryWrite(Frame(99, AudioFrameKind.Quiet));
        frames.Writer.Complete();
        await session.RunClassifiedAudioAsync(frames.Reader);
        Check(sockets.Count == 2 && sockets.All(socket => socket.Audio.Count == 15)
            && sockets.SelectMany(socket => socket.Audio).Select(bytes => BitConverter.ToInt32(bytes))
                .SequenceEqual(Enumerable.Range(0, 30)),
            "Configured speech qualification was lost at startup or after a five-second stream reset.");
        Check(sockets[1].EndMarkers == 1, "Configured silence reset was lost when the speech gate was recreated.");
    }

    private static async Task CheckMixedUtteranceSegmentsAsync()
    {
        var socket = new Transport();
        var captions = new List<CaptionUpdate>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var session = new GeminiCaptionSession("fake-test-key", null, () => socket, new Clock(),
            detectLanguage: (text, _) =>
            {
                ++calls;
                return Task.FromResult<string?>(text.StartsWith("Karibuni", StringComparison.Ordinal) ||
                    text.StartsWith("Tunakutana", StringComparison.Ordinal) ? "sw" : "en");
            },
            drainTranslate: (text, assignment, _) => Task.FromResult(($"{assignment.Input}->{assignment.Primary}: {text}",
                $"{assignment.Input}->{assignment.Secondary}: {text}")));
        session.Caption += update => { lock (captions) { captions.Add(update); if (captions.Count == 4) done.TrySetResult(); } };
        await session.RunClassifiedAudioAsync(Frames(20).Reader).WaitAsync(TimeSpan.FromSeconds(3));
        var interimEnglish = "Welcome everyone to our meeting.";
        var interimMixed = interimEnglish + " Karibuni nyote kwenye mkutano wa leo.";
        socket.Emit(JsonSerializer.Serialize(new { serverContent = new { interimInputTranscription = new { text = interimEnglish } } }));
        socket.Emit(JsonSerializer.Serialize(new { serverContent = new { interimInputTranscription = new { text = interimEnglish } } }));
        socket.Emit(JsonSerializer.Serialize(new { serverContent = new { interimInputTranscription = new { text = interimMixed } } }));
        socket.Emit(JsonSerializer.Serialize(new { serverContent = new { interimInputTranscription = new { text = interimMixed } } }));
        socket.Emit(JsonSerializer.Serialize(new { serverContent = new { inputTranscription = new { text = interimMixed } } }));
        var finalMixed = "Good evening to the room. Tunakutana pamoja leo.";
        socket.Emit(JsonSerializer.Serialize(new { serverContent = new { inputTranscription = new { text = finalMixed } } }));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
        lock (captions)
        {
            Check(captions.Select(c => c.SourceLanguage).SequenceEqual(new[] { "en", "sw", "en", "sw" }) &&
                captions.Select(c => c.PrimaryLanguage).SequenceEqual(new[] { "sw", "en", "sw", "en" }) &&
                captions.Select(c => c.SecondaryLanguage).All(code => code == "fr") && calls == 4,
                "Stable interim or cumulative final reused the first speaker's language for the next sentence: " +
                string.Join(", ", captions.Select(c => $"{c.SourceLanguage}->{c.PrimaryLanguage}/{c.SecondaryLanguage}:{c.Text}")) +
                $"; checks={calls}");
        }
    }

    private static async Task CheckHintPersistsThroughFinalAsync()
    {
        var socket = new Transport();
        var captions = new List<CaptionUpdate>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var session = new GeminiCaptionSession("fake-test-key", null, () => socket, new Clock(),
            detectLanguage: (_, _) => { ++calls; return Task.FromResult<string?>("sw"); },
            drainTranslate: (text, _, _) => Task.FromResult((text, text)));
        session.HintInputLanguage("en");
        session.Caption += update => { lock (captions) { captions.Add(update); if (captions.Count == 3) done.TrySetResult(); } };
        await session.RunClassifiedAudioAsync(Frames(20).Reader).WaitAsync(TimeSpan.FromSeconds(3));
        var hintedInterim = "Habari za leo.";
        var hintedFinal = hintedInterim + " Tunaendelea sasa.";
        socket.Emit(JsonSerializer.Serialize(new { serverContent = new { interimInputTranscription = new { text = hintedInterim } } }));
        socket.Emit(JsonSerializer.Serialize(new { serverContent = new { interimInputTranscription = new { text = hintedInterim } } }));
        socket.Emit(JsonSerializer.Serialize(new { serverContent = new { inputTranscription = new { text = hintedFinal } } }));
        socket.Emit("""{"serverContent":{"inputTranscription":{"text":"Karibuni nyote."}}}""");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
        lock (captions)
            Check(captions.Take(2).All(c => c.SourceLanguage == "en" && c.PrimaryLanguage == "sw") &&
                captions[2].SourceLanguage == "sw" && calls == 1,
                "An operator hint stopped applying within its utterance or escaped to the next one.");
    }
    private static async Task CheckBlockedDetectionSkipsSegmentAsync()
    {
        var socket = new Transport();
        var translated = new List<string>();
        var captioned = new TaskCompletionSource<CaptionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detections = 0;
        await using var session = new GeminiCaptionSession("fake-test-key", null, () => socket, new Clock(),
            detectLanguage: (_, _) => ++detections == 1
                ? throw new CaptionSessionException("Google blocked this language check (SAFETY).",
                    isTransientTranslation: true, isContentBlocked: true)
                : Task.FromResult<string?>("sw"),
            drainTranslate: (text, _, _) => { translated.Add(text); return Task.FromResult((text, text)); });
        session.Caption += update => captioned.TrySetResult(update);
        await session.RunClassifiedAudioAsync(Frames(20).Reader).WaitAsync(TimeSpan.FromSeconds(3));
        socket.Emit("""{"serverContent":{"inputTranscription":{"text":"Blocked speech."}}}""");
        socket.Emit("""{"serverContent":{"inputTranscription":{"text":"Karibuni nyote."}}}""");
        var next = await captioned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(detections == 2 && translated.SequenceEqual(new[] { "Karibuni nyote." }) &&
            next.SourceLanguage == "sw" && !socket.Aborted,
            "A blocked language check was retried through translation or stopped later speech.");
    }
    private static async Task CheckLanguageCheckRetriesAsync()
    {
        var clock = new Clock();
        var socket = new Transport();
        var firstCaption = new TaskCompletionSource<CaptionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCaption = new TaskCompletionSource<CaptionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var failed = false;
        await using var session = new GeminiCaptionSession("fake-test-key", null, () => socket, clock,
            detectLanguage: (_, _) => ++calls == 1
                ? throw new CaptionSessionException("Google did not complete the language code translation (finishReason: MAX_TOKENS).")
                : Task.FromResult<string?>("sw"),
            drainTranslate: (text, _, _) => Task.FromResult((text, text)));
        session.Caption += update =>
        {
            if (update.Text == "Hello everyone.") firstCaption.TrySetResult(update);
            if (update.Text == "Karibuni nyote.") secondCaption.TrySetResult(update);
        };
        session.Failure += _ => failed = true;
        await session.RunClassifiedAudioAsync(Frames(20).Reader).WaitAsync(TimeSpan.FromSeconds(3));
        socket.Emit("""{"serverContent":{"inputTranscription":{"text":"Hello everyone."}}}""");
        await firstCaption.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Now = 3;
        socket.Emit("""{"serverContent":{"inputTranscription":{"text":"Karibuni nyote."}}}""");
        var recovered = await secondCaption.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(calls == 2 && recovered.SourceLanguage == "sw" && recovered.PrimaryLanguage == "en" && !failed,
            "A capped language check poisoned later speakers on the same connection.");
    }
    private sealed class CancellableTransport(TaskCompletionSource started, TaskCompletionSource hold) : ILiveTransport
    {
        public int HttpStatus => 0;
        public int Sends;
        public async Task ConnectAsync(CancellationToken token) { started.TrySetResult(); await hold.Task.WaitAsync(token); }
        public Task SendAsync(byte[] data, CancellationToken token) { Sends++; return Task.CompletedTask; }
        public Task<LiveMessage> ReceiveAsync(CancellationToken token) => throw new NotImplementedException();
        public void Abort() { }
        public void Dispose() { }
    }
}
