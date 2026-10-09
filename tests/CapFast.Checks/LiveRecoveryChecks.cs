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
                    Emit("{\"serverContent\":{\"outputTranscription\":{\"text\":\"Finished.\"},\"turnComplete\":true}}");
                }
            }
            await Task.Yield();
        }
        public Task<LiveMessage> ReceiveAsync(CancellationToken token) => _messages.Reader.ReadAsync(token).AsTask();
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
        new("fake-test-key", null, GoogleCaptionMode.LiveTranslate, factory, clock);
    public static async Task RunAsync()
    {
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
            sockets[1].Emit("{\"serverContent\":{\"outputTranscription\":{\"text\":\"Finished.\"},\"turnComplete\":true}}");
            var freshCaption = await captions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            sockets[0].Emit("{\"serverContent\":{\"outputTranscription\":{\"text\":\"Stale socket text.\"},\"turnComplete\":true}}");
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
