using CapFast.Core;

namespace CapFast.Speech;

// Interim chunks and drained final suffixes share ordered source context within
// their original assignment. A newer block has a separate pair and can proceed.
public sealed class BlockTextTranslator : IDisposable
{
    private sealed class Context(FlashTextTranslator primary, FlashTextTranslator secondary) : IDisposable
    {
        public readonly FlashTextTranslator Primary = primary, Secondary = secondary;
        public readonly SemaphoreSlim Order = new(1);
        public int Users;
        public void Dispose() { Primary.Dispose(); Secondary.Dispose(); }
    }
    private readonly object _gate = new();
    private readonly Dictionary<(LanguageAssignment Assignment, long Run), Context> _contexts = new();
    private readonly Dictionary<long, (string Input, long Run)> _epochRuns = new();
    private long _nextRun;
    private readonly Func<LanguageAssignment, string, FlashTextTranslator> _factory;
    private bool _disposed;

    public BlockTextTranslator(string key) : this((assignment, target) => new FlashTextTranslator(key,
        sourceLanguage: SpeechLanguageAssignment.Name(assignment.Input), targetLanguage: SpeechLanguageAssignment.Name(target))) { }
    public BlockTextTranslator(Func<LanguageAssignment, string, FlashTextTranslator> factory) => _factory = factory;

    public async Task<(string Primary, string Secondary)> TranslateAsync(string text, LanguageAssignment assignment, CancellationToken token)
    {
        Context context;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A speaker returning to a language within the same audio block
            // starts a fresh translation context. Earlier source text may be
            // from a different speaker and must not influence this fragment.
            if (!_epochRuns.TryGetValue(assignment.Epoch, out var run) || run.Input != assignment.Input)
            {
                run = (assignment.Input, ++_nextRun);
                _epochRuns[assignment.Epoch] = run;
                if (_epochRuns.Count > 64) _epochRuns.Remove(_epochRuns.Keys.Min());
            }
            var key = (assignment, run.Run);
            if (!_contexts.TryGetValue(key, out context!))
            {
                if (_contexts.Count >= 32)
                {
                    var idle = _contexts.FirstOrDefault(pair => pair.Value.Users == 0);
                    if (idle.Value is null) throw new CaptionSessionException("Translation fell behind the audio. Restart the session to continue.");
                    _contexts.Remove(idle.Key); idle.Value.Dispose();
                }
                context = new(_factory(assignment, assignment.Primary), _factory(assignment, assignment.Secondary));
                _contexts.Add(key, context);
            }
            ++context.Users;
        }
        var entered = false;
        try
        {
            await context.Order.WaitAsync(token); entered = true;
            var primary = context.Primary.TranslateAsync(text, token);
            var secondary = context.Secondary.TranslateAsync(text, token);
            await Task.WhenAll(primary, secondary);
            return (primary.Result, secondary.Result);
        }
        finally
        {
            if (entered) context.Order.Release();
            lock (_gate) --context.Users;
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var context in _contexts.Values) context.Dispose();
            _contexts.Clear();
            _epochRuns.Clear();
        }
    }
}
