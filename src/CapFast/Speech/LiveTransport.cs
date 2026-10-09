using System.Net.WebSockets;

namespace CapFast.Speech;

internal sealed record LiveMessage(byte[] Data, WebSocketMessageType Type, int? CloseCode = null, string? CloseReason = null);
internal interface ILiveTransport : IDisposable
{
    int HttpStatus { get; }
    Task ConnectAsync(CancellationToken token);
    Task SendAsync(byte[] data, CancellationToken token);
    Task<LiveMessage> ReceiveAsync(CancellationToken token);
    void Abort();
}
internal sealed class WebSocketLiveTransport : ILiveTransport
{
    private readonly ClientWebSocket _socket = new();
    public int HttpStatus => (int)_socket.HttpStatusCode;
    public WebSocketLiveTransport(string key)
    {
        _socket.Options.SetRequestHeader("x-goog-api-key", key);
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _socket.Options.CollectHttpResponseDetails = true;
    }
    public Task ConnectAsync(CancellationToken token) => _socket.ConnectAsync(new Uri("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent"), token);
    public Task SendAsync(byte[] data, CancellationToken token) => _socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, token);
    public async Task<LiveMessage> ReceiveAsync(CancellationToken token)
    {
        var buffer = new byte[16384];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close)
                return new(Array.Empty<byte>(), result.MessageType, (int?)result.CloseStatus ?? 0, result.CloseStatusDescription);
            if (result.MessageType is not (WebSocketMessageType.Text or WebSocketMessageType.Binary) || message.Length + result.Count > 2 * 1024 * 1024)
                throw new InvalidDataException("Invalid service frame.");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return new(message.ToArray(), result.MessageType);
    }
    public void Abort() => _socket.Abort();
    public void Dispose() => _socket.Dispose();
}
internal interface ILiveClock
{
    double Now { get; }
    Task Delay(TimeSpan delay, CancellationToken token);
}
internal sealed class SystemLiveClock : ILiveClock
{
    public double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    public Task Delay(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
}
internal sealed record ClassifiedAudioFrame(byte[] Pcm, CapFast.Core.AudioFrameKind Kind);
internal sealed class TransientLiveException : Exception { }
internal sealed class LiveRetryBudget
{
    private int _attempts;
    private int _healthyFrames;
    public TimeSpan NextDelay()
    {
        _healthyFrames = 0;
        if (_attempts >= 3) throw new CaptionSessionException("Google connection recovery exhausted three attempts. Capture paused; check the network and start again.");
        return TimeSpan.FromSeconds(0.5 * Math.Pow(2, _attempts++));
    }
    public void AudioSucceeded()
    {
        if (++_healthyFrames >= 100) Reset();
    }
    public void Reset() { _attempts = 0; _healthyFrames = 0; }
}
