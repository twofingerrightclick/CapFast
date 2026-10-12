using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using CapFast.Core;
using CapFast.Speech;

public static class SourceReaderChecks
{
    private sealed class Clock : ILiveClock
    {
        public double Now => 0;
        public Task Delay(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
    }
    private sealed class Transport : ILiveTransport
    {
        private readonly Channel<LiveMessage> _messages = Channel.CreateUnbounded<LiveMessage>();
        public bool Aborted;
        public readonly TaskCompletionSource AbortObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Uploaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<LiveMessage>? MessageReceived;
        public int HttpStatus => 0;
        public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task SendAsync(byte[] data, CancellationToken token)
        {
            if (Encoding.UTF8.GetString(data).Contains("\"setup\"")) Emit("{\"setupComplete\":{}}");
            else Uploaded.TrySetResult();
            return Task.CompletedTask;
        }
        public async Task<LiveMessage> ReceiveAsync(CancellationToken token)
        {
            var message = await _messages.Reader.ReadAsync(token);
            MessageReceived?.Invoke(message);
            return message;
        }
        public void Emit(string json) => _messages.Writer.TryWrite(new(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text));
        public void Abort() { Aborted = true; AbortObserved.TrySetResult(); }
        public void Dispose() { }
    }
    public static async Task RunAsync()
    {
        await CheckFinalCapacityFailureAsync();
        var socket = new Transport();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interimRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var caption = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.MessageReceived = message =>
        {
            if (Encoding.UTF8.GetString(message.Data).Contains("interimInputTranscription", StringComparison.Ordinal))
                interimRead.TrySetResult();
        };
        await using var session = new GeminiCaptionSession("fake", null, () => socket, new Clock(),
            detectLanguage: (_, _) => Task.FromResult<string?>("en"),
            drainTranslate: (_, _, _) => { entered.TrySetResult(); return release.Task; });
        session.Caption += _ => caption.TrySetResult();
        var frames = Channel.CreateUnbounded<ClassifiedAudioFrame>();
        for (var i = 0; i < 20; i++) frames.Writer.TryWrite(new(new byte[3200], AudioFrameKind.Speech));
        var pump = session.RunClassifiedAudioAsync(frames.Reader);
        await socket.Uploaded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        socket.Emit("{\"serverContent\":{\"inputTranscription\":{\"text\":\"First.\",\"languageCode\":\"en\"}}}");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // More than the source channel's capacity arrive during slow translation.
        for (var i = 0; i < 40; i++)
        {
            socket.Emit("{\"serverContent\":{\"interimInputTranscription\":{\"text\":\"Next words still in progress\"}}}");
            await Task.Delay(5);
        }
        await interimRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (socket.Aborted || release.Task.IsCompleted) throw new Exception("Slow final blocked recognition or overflowed its bounded channel.");
        release.SetResult(("First.", "First."));
        await caption.Task.WaitAsync(TimeSpan.FromSeconds(2));
        frames.Writer.Complete();
        await pump.WaitAsync(TimeSpan.FromSeconds(2));
    }
    private static async Task CheckFinalCapacityFailureAsync()
    {
        var socket = new Transport();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registered = Enumerable.Range(0, 33).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var registrations = 0;
        await using var session = new GeminiCaptionSession("fake", null, () => socket, new Clock(),
            detectLanguage: (_, _) => Task.FromResult<string?>("en"),
            drainTranslate: (_, _, _) => { entered.TrySetResult(); return release.Task; });
        session.FinalSourceRegistered += () => registered[registrations++].TrySetResult();
        var frames = Channel.CreateUnbounded<ClassifiedAudioFrame>();
        for (var i = 0; i < 20; i++) frames.Writer.TryWrite(new(new byte[3200], AudioFrameKind.Speech));
        var pump = session.RunClassifiedAudioAsync(frames.Reader);
        await socket.Uploaded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 33; i++)
        {
            socket.Emit("{\"serverContent\":{\"inputTranscription\":{\"text\":\"Accepted final.\"}}}");
            // Pace reception by source registration so this reaches the
            // final-work bound, independently of the recognition-channel bound.
            await registered[i].Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (i == 0) await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        await socket.AbortObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await pump.WaitAsync(TimeSpan.FromSeconds(2));
            throw new Exception("Final-work capacity failure did not terminate capture visibly.");
        }
        catch (CaptionSessionException error)
        {
            if (error.Message != "Translation fell behind the audio. Restart the session to continue.")
                throw new Exception("Final-work overflow used the wrong failure: " + error.Message);
        }
        if (release.Task.IsCompleted) throw new Exception("Capacity regression released blocked translation unexpectedly.");
    }

}
