using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using CapFast.Core;
using CapFast.Speech;

// Real Google sockets and the production audio pump; only the local session
// clock advances after seven seconds of accepted audio. No microphone or UI access.
internal static class Program
{
    private sealed class ProbeClock : ILiveClock
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _offset;
        public double Now => _clock.Elapsed.TotalSeconds + Volatile.Read(ref _offset);
        public void AgeConnection() => Interlocked.Exchange(ref _offset, 481);
        public Task Delay(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
    }
    private sealed class CountingTransport(ILiveTransport inner, Action uploaded) : ILiveTransport
    {
        public int HttpStatus => inner.HttpStatus;
        public Task ConnectAsync(CancellationToken token) => inner.ConnectAsync(token);
        public async Task SendAsync(byte[] data, CancellationToken token)
        {
            await inner.SendAsync(data, token);
            using var json = JsonDocument.Parse(data);
            if (json.RootElement.TryGetProperty("realtimeInput", out var realtime) && realtime.TryGetProperty("audio", out _)) uploaded();
        }
        public Task<LiveMessage> ReceiveAsync(CancellationToken token) => inner.ReceiveAsync(token);
        public void Abort() => inner.Abort();
        public void Dispose() => inner.Dispose();
    }
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var run = args.Contains("--run");
            var input = "artifacts/speech-gate/talk.frames";
            string? output = null;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--input") input = args[++i];
                else if (args[i] == "--output") output = args[++i];
                else if (args[i] != "--run") throw new CaptionSessionException("Use --run, --input FRAMED_AUDIO, --output NEW_REPORT.");
            }
            if (output is null || File.Exists(output)) throw new CaptionSessionException("Choose a new output report.");
            var bytes = File.ReadAllBytes(input);
            if (bytes.Length == 0 || bytes.Length % 3201 != 0 || bytes.Length > 300 * 3201)
                throw new CaptionSessionException("Use at most thirty seconds of classified audio frames.");
            for (var offset = 0; offset < bytes.Length; offset += 3201)
                if (bytes[offset] > 2) throw new CaptionSessionException("Invalid classified audio frame.");
            if (!run)
            {
                Console.WriteLine($"Validated {bytes.Length / 3201 / 10d:F1}s; --run opts into real Google audio and translation requests.");
                return 0;
            }
            var key = new LocalConfiguration().GoogleKey;
            if (string.IsNullOrWhiteSpace(key)) throw new CaptionSessionException("No local Google API key is configured.");
            var clock = new ProbeClock();
            var sockets = 0; var uploads = 0; var captionCount = 0; var captionCharacters = 0;
            var notices = new List<string>();
            var captionSessions = new HashSet<string>();
            string? failure = null;
            await using var session = new GeminiCaptionSession(key, null, () =>
            {
                Interlocked.Increment(ref sockets);
                return new CountingTransport(new WebSocketLiveTransport(key), () =>
                {
                    if (Interlocked.Increment(ref uploads) == 70) clock.AgeConnection();
                });
            }, clock);
            session.Caption += caption =>
            {
                Interlocked.Increment(ref captionCount); Interlocked.Add(ref captionCharacters, caption.Text.Length);
                var fields = caption.UtteranceId.Split(':');
                if (fields.Length > 1) lock (captionSessions) captionSessions.Add(fields[1]);
            };
            session.Diagnostic += message => { lock (notices) notices.Add(message); };
            session.Ended += message => failure = message;
            var frames = Channel.CreateBounded<ClassifiedAudioFrame>(200);
            var timer = Stopwatch.StartNew();
            using var stopFeeder = new CancellationTokenSource(TimeSpan.FromSeconds(70));
            var pump = session.RunClassifiedAudioAsync(frames.Reader);
            var feeder = Task.Run(async () =>
            {
                var paced = Stopwatch.StartNew();
                for (var index = 0; index < bytes.Length / 3201; index++)
                {
                    var remaining = TimeSpan.FromMilliseconds(index * 100) - paced.Elapsed;
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, stopFeeder.Token);
                    var offset = index * 3201;
                    await frames.Writer.WriteAsync(new(bytes.AsSpan(offset + 1, 3200).ToArray(), (AudioFrameKind)bytes[offset]), stopFeeder.Token);
                }
                // Local quiet frames request one end marker and allow pending English
                // to finish. The production gate never uploads this synthetic silence.
                for (var index = 0; index < 250; index++)
                {
                    await frames.Writer.WriteAsync(new(new byte[3200], AudioFrameKind.Quiet), stopFeeder.Token);
                    await Task.Delay(100, stopFeeder.Token);
                }
                frames.Writer.Complete();
            });
            _ = pump.ContinueWith(_ => { try { stopFeeder.Cancel(); } catch (ObjectDisposedException) { } },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try { await Task.WhenAll(pump, feeder).WaitAsync(stopFeeder.Token); }
            catch (CaptionSessionException safe) { failure = safe.Message; }
            catch { failure ??= "Rollover probe stopped or exceeded its deadline; no raw service response was recorded."; }
            stopFeeder.Cancel();
            frames.Writer.TryComplete();
            await session.DisposeAsync();
            var passed = failure is null && sockets == 2 && uploads > 70 && captionSessions.Count == 2;
            var report = new
            {
                passed, elapsedSeconds = timer.Elapsed.TotalSeconds, sourceAudioSeconds = bytes.Length / 3201 / 10d,
                connections = sockets, uploadedAudioSeconds = uploads / 10d, captionCount, captionCharacters, captionSourceSessions = captionSessions.Count, notices, failure,
                methodology = "Real Google sockets and production two-stage pump; on the 70th accepted audio frame the injected local session clock advances by481seconds, forcing proactive rollover. Frames come from the production local classifier fixture and are paced100ms. Subsequent quiet frames stay local. No microphone, transcript text, credentials, or server payloads are saved. The same clock also drives source-stability deadlines, so this is not a latency or segmentation-timing measurement. It does not wait for actual server expiry."
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            Console.WriteLine($"Rollover probe {(passed ? "passed" : "did not pass")}: {sockets} connections, {uploads / 10d:F1}s audio uploaded, {captionCount} captions. See the safe report.");
            return passed ? 0 : 1;
        }
        catch (CaptionSessionException safe) { Console.WriteLine(safe.Message); return 1; }
        catch { Console.WriteLine("Rollover probe failed before completion. No raw exception or credentials were logged."); return 1; }
    }
}
