using System.Threading.Channels;
using System.Diagnostics;
using CapFast.Core;

namespace CapFast.Speech;

public sealed class SourceTranslationPipeline : IAsyncDisposable
{
    private sealed record QueuedSource(CaptionUpdate Update, long ReceivedAt);
    private readonly Channel<QueuedSource> _queue;
    private readonly TimeSpan _coalesceWindow;
    private readonly Func<string, CancellationToken, Task<string>> _translate;
    private readonly CancellationTokenSource _lifetime;
    private readonly object _gate = new();
    private readonly HashSet<string> _accepted = new();
    private readonly Queue<string> _recent = new();
    private bool _disposed;
    public event Action<CaptionUpdate>? Caption;
    public event Action<string>? Failed;
    public event Action<IReadOnlyList<CaptionUpdate>>? BatchStarted;
    public Task Completion { get; }

    public SourceTranslationPipeline(Func<string, CancellationToken, Task<string>> translate,
        CancellationToken cancellationToken = default, int capacity = 8, TimeSpan? coalesceWindow = null)
    {
        _translate = translate;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _coalesceWindow = coalesceWindow ?? TimeSpan.FromSeconds(3);
        if (_coalesceWindow < TimeSpan.Zero || _coalesceWindow > TimeSpan.FromSeconds(3))
            throw new ArgumentOutOfRangeException(nameof(coalesceWindow));
        _queue = Channel.CreateBounded<QueuedSource>(new BoundedChannelOptions(capacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        Completion = ProcessAsync();
    }

    public bool TryEnqueue(CaptionUpdate source)
    {
        lock (_gate)
        {
            if (_disposed || _lifetime.IsCancellationRequested) return false;
            if (!source.IsFinal || string.IsNullOrWhiteSpace(source.Text) || _accepted.Contains(source.UtteranceId)) return true;
            if (!_queue.Writer.TryWrite(new(source, Stopwatch.GetTimestamp()))) return false;
            _accepted.Add(source.UtteranceId);
            _recent.Enqueue(source.UtteranceId);
            if (_recent.Count > 256) _accepted.Remove(_recent.Dequeue());
            return true;
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_lifetime.Token))
            {
                if (!_queue.Reader.TryRead(out var first)) continue;
                var sources = new List<CaptionUpdate> { first.Update };
                var text = first.Update.Text.Trim();
                while (_coalesceWindow > TimeSpan.Zero && !EndsSentence(text) && text.Length < 1024)
                {
                    if (_queue.Reader.TryPeek(out var next))
                    {
                        if (Stopwatch.GetElapsedTime(first.ReceivedAt, next.ReceivedAt) > _coalesceWindow
                            || text.Length + next.Update.Text.Trim().Length + 1 > 1024) break;
                        if (!_queue.Reader.TryRead(out next)) continue;
                        sources.Add(next.Update);
                        text += " " + next.Update.Text.Trim();
                        continue;
                    }
                    var remaining = _coalesceWindow - Stopwatch.GetElapsedTime(first.ReceivedAt);
                    if (remaining <= TimeSpan.Zero) break;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    deadline.CancelAfter(remaining);
                    try
                    {
                        // This waits for availability, not a consuming ReadAsync. A canceled
                        // deadline cannot steal the next segment from the subsequent batch.
                        if (!await _queue.Reader.WaitToReadAsync(deadline.Token)) break;
                    }
                    catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested) { break; }
                }
                _lifetime.Token.ThrowIfCancellationRequested();
                BatchStarted?.Invoke(sources.AsReadOnly());
                var translated = await _translate(text, _lifetime.Token).WaitAsync(_lifetime.Token);
                lock (_gate)
                {
                    if (_disposed || _lifetime.IsCancellationRequested) return;
                    Caption?.Invoke(new(first.Update.UtteranceId, translated, true));
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (_disposed || _lifetime.IsCancellationRequested) return;
                Failed?.Invoke(error is CaptionSessionException safe ? safe.Message : "Google text translation failed. Restart the session to continue.");
                _lifetime.Cancel();
                _queue.Writer.TryComplete();
            }
        }
    }

    private static bool EndsSentence(string text)
    {
        var tail = text.TrimEnd().TrimEnd('"', '\'', '”', '’', ')', ']', '»').TrimEnd();
        return tail.Length > 0 && tail[^1] is '.' or '!' or '?';
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _queue.Writer.TryComplete(); }
        try { await Completion.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        _ = Completion.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
