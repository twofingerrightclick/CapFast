using CapFast.Core;

namespace CapFast.Speech;

public interface ICaptionSession : IAsyncDisposable
{
    event Action<CaptionUpdate>? Caption;
    event Action<string>? Status;
    event Action<string>? Ended;
    Task StartAsync(CancellationToken cancellationToken = default);
}
