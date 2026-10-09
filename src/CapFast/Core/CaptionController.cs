using CapFast.Speech;

namespace CapFast.Core;

// All public operations and dispatched callbacks run on the UI thread.
// Serializing transitions prevents an old start/stop from owning a newer session.
public sealed class CaptionController
{
    private readonly Func<CaptionSettings, ICaptionSession> _factory;
    private readonly Action<Action> _dispatch;
    private readonly CaptionBuffer _buffer = new();
    private readonly SemaphoreSlim _transition = new(1);
    private ICaptionSession? _session;
    private CancellationTokenSource? _starting;
    private CancellationTokenSource? _demo;
    private bool _wantLive;
    private bool _shutdown;
    private int _generation;

    public CaptionController(CaptionSettings settings, Func<CaptionSettings, ICaptionSession> factory, Action<Action> dispatch)
    { Settings = settings; _factory = factory; _dispatch = dispatch; if (settings.ConfigurationError is { } error) Errors.Add(error); }
    public CaptionSettings Settings { get; }
    public CaptionState State { get; private set; } = CaptionState.Paused;
    public bool IsPaused => State == CaptionState.Paused;
    public string Text => _buffer.Text;
    public string? CaptionUtteranceId { get; private set; }
    public string Status { get; private set; } = "Paused — press Space to start";
    public ErrorLog Errors { get; } = new();
    public string? MissingConfiguration => Settings.Provider == SpeechProvider.Google
        ? string.IsNullOrWhiteSpace(Settings.GeminiKey) ? "Add your Google API key in Settings to start captions." : null
        : string.IsNullOrWhiteSpace(Settings.AzureKey) || string.IsNullOrWhiteSpace(Settings.AzureRegion) ? "Add your Azure key and region in Settings to start captions." : null;
    public event Action? Changed;
    public void SettingsChanged() => Changed?.Invoke();
    public void ClearCaptions() { _buffer.Clear(); CaptionUtteranceId = null; Changed?.Invoke(); }
    public Task ToggleAsync()
    {
        if (_shutdown) return Task.CompletedTask;
        _wantLive = !_wantLive;
        if (!_wantLive) { ++_generation; _starting?.Cancel(); _demo?.Cancel(); State = CaptionState.Pausing; Changed?.Invoke(); }
        return ReconcileAsync();
    }
    public Task PauseAsync()
    {
        _wantLive = false; ++_generation; _starting?.Cancel(); _demo?.Cancel();
        if (!IsPaused) { State = CaptionState.Pausing; Changed?.Invoke(); }
        return ReconcileAsync();
    }
    public async Task ShutdownAsync() { _shutdown = true; await PauseAsync(); }
    private async Task ReconcileAsync()
    {
        await _transition.WaitAsync();
        try
        {
            if (!_wantLive) { await StopAsync(); return; }
            if ((_session != null || _demo != null) && State == CaptionState.Live) return;
            if (_session != null || _demo != null) await StopAsync();
            if (MissingConfiguration is { } missing)
            { _wantLive = false; State = CaptionState.Paused; Status = missing; Changed?.Invoke(); return; }
            State = CaptionState.Starting; Status = "Connecting…"; Changed?.Invoke();
            var generation = ++_generation;
            using var starting = new CancellationTokenSource();
            _starting = starting;
            try
            {
                var session = _factory(Settings.Copy());
                _session = session;
                session.Caption += update => _dispatch(() =>
                {
                    if (generation == _generation && _wantLive && _buffer.Apply(update))
                    { CaptionUtteranceId = update.UtteranceId; Changed?.Invoke(); }
                });
                session.Status += message => _dispatch(() =>
                { if (generation == _generation && _wantLive) { Status = message; Changed?.Invoke(); } });
                session.Ended += message => _dispatch(() =>
                { if (generation == _generation && _wantLive) { _ = PauseWithErrorAsync(message); } });
                await session.StartAsync(starting.Token);
                if (!_wantLive || generation != _generation) { await StopAsync(); return; }
                State = CaptionState.Live; Status = "Live — press Space to pause"; Changed?.Invoke();
            }
            catch (OperationCanceledException) when (starting.IsCancellationRequested)
            {
                await StopAsync();
            }
            catch (Exception error)
            {
                var message = error is CaptionSessionException ? error.Message
                    : "Could not start transcription. Open Errors for details; check the configured service and connection.";
                // A session-ended callback may already have recorded its precise error.
                if (_wantLive) Errors.Add(message);
                _wantLive = false; await StopAsync();
                Status = message; Changed?.Invoke();
            }
            finally { if (ReferenceEquals(_starting, starting)) _starting = null; }
        }
        finally { _transition.Release(); }
    }
    private async Task PauseWithErrorAsync(string message)
    {
        Errors.Add(message);
        await PauseAsync();
        Status = message; Changed?.Invoke();
    }
    private async Task StopAsync()
    {
        ++_generation;
        _demo?.Cancel(); _demo?.Dispose(); _demo = null;
        var session = _session; _session = null;
        if (session != null)
        {
            State = CaptionState.Pausing; Changed?.Invoke();
            try { await session.DisposeAsync(); }
            catch { Errors.Add("Could not cleanly stop transcription."); }
        }
        State = CaptionState.Paused; Status = "Paused — press Space to start"; Changed?.Invoke();
    }
    public async Task RunDemoAsync()
    {
        await PauseAsync();
        if (_shutdown) return;
        CancellationToken token;
        int generation;
        await _transition.WaitAsync();
        try
        {
            if (_shutdown || !IsPaused || _session != null || _demo != null) return;
            _demo = new CancellationTokenSource(); token = _demo.Token;
            generation = ++_generation;
            _wantLive = true; State = CaptionState.Live; Status = "Caption demo — press Space to pause"; Changed?.Invoke();
        }
        finally { _transition.Release(); }
        try
        {
            var frames = new[] { "Welcome", "Welcome to today's", "Welcome to today's seminar.", "We are sharing ideas", "We are sharing ideas together." };
            for (var i = 0; i < frames.Length; i++)
            {
                await Task.Delay(900, token);
                if (generation != _generation) return;
                var update = new CaptionUpdate($"demo-{generation}-{(i < 3 ? 0 : 1)}", frames[i], i == 2 || i == 4);
                if (_buffer.Apply(update))
                { CaptionUtteranceId = update.UtteranceId; Changed?.Invoke(); }
            }
        }
        catch (OperationCanceledException) { }
        finally { if (generation == _generation) await PauseAsync(); }
    }
}
