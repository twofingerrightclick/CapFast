using CapFast.Core;
using CapFast.Speech;

internal static class ControllerChecks
{
    public static async Task RunAsync()
    {
        var log = new ErrorLog(3);
        for (var i = 0; i < 5; i++) log.Add(i.ToString());
        Require(log.Entries.Count == 3 && log.Entries[0].Message == "2", "Log did not evict oldest entries");
        log.Clear(); Require(log.Entries.Count == 0, "Log clear failed");
        var sessions = new List<FakeSession>();
        var settings = new CaptionSettings { GeminiKey = "test", AudioDeviceId = "device-one" };
        var controller = new CaptionController(settings, snapshot =>
        {
            Require(snapshot.AudioDeviceId == "device-one", "Selected audio source lost");
            var session = new FakeSession(); sessions.Add(session); return session;
        }, action => action());
        Require(controller.IsPaused && sessions.Count == 0, "Startup must be paused");
        var start = controller.ToggleAsync();
        var pause = controller.ToggleAsync();
        sessions[0].Emit("stale");
        sessions[0].Started.SetResult();
        await Task.WhenAll(start, pause);
        Require(controller.IsPaused && sessions[0].Disposed && controller.Text == "", "Pause during start failed");
        Require(controller.CaptionUtteranceId is null, "Stale caption changed display identity");
        start = controller.ToggleAsync(); sessions[1].Started.SetResult(); await start;
        sessions[1].Emit("Keep these captions");
        await controller.PauseAsync();
        sessions[1].Emit("stale");
        Require(controller.IsPaused && controller.Text == "Keep these captions", "Pause changed captions or accepted stale callback");
        controller.SettingsChanged();
        Require(controller.CaptionUtteranceId == "id", "Pause or settings lost the retained caption identity");
        controller.ClearCaptions();
        Require(controller.Text == "" && controller.CaptionUtteranceId is null, "Clear retained stale caption identity");
        start = controller.ToggleAsync();
        pause = controller.ToggleAsync();
        var resume = controller.ToggleAsync();
        sessions[2].Started.SetResult();
        await Task.Yield();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (sessions.Count < 4 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Require(sessions.Count >= 4, "Rapid resume did not start a new session");
        sessions[3].Started.SetResult();
        await Task.WhenAll(start, pause, resume);
        Require(controller.State == CaptionState.Live && sessions[2].Disposed, "Rapid resume failed");
        await controller.ShutdownAsync();
        Require(controller.IsPaused && sessions[3].Disposed, "Shutdown did not stop audio");
        var missing = new CaptionController(new CaptionSettings { GeminiKey = "" }, _ => throw new Exception("Factory must not be called"), a => a());
        await missing.ToggleAsync(); Require(missing.IsPaused && missing.MissingConfiguration != null && missing.Errors.Entries.Count == 0, "Missing configuration started session");
        var cancellableSession = new CancellableSession();
        var cancellable = new CaptionController(settings, _ => cancellableSession, a => a());
        var pending = cancellable.ToggleAsync();
        await cancellable.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await pending;
        Require(cancellable.IsPaused && cancellableSession.Disposed && cancellable.Errors.Entries.Count == 0, "Cancellation did not stop pending start silently");
        var safeFailure = new CaptionController(settings, _ => throw new CaptionSessionException("Google rejected authentication (401)."), a => a());
        await safeFailure.ToggleAsync();
        Require(safeFailure.Status.Contains("401") && safeFailure.Errors.Entries.Single().Message.Contains("401"), "Safe diagnostic was lost");
        var unsafeFailure = new CaptionController(settings, _ => throw new Exception("secret-value"), a => a());
        await unsafeFailure.ToggleAsync();
        Require(!unsafeFailure.Status.Contains("secret-value") && !unsafeFailure.Errors.Entries.Single().Message.Contains("secret-value"), "Raw exception leaked");
        var readingSession = new FakeSession();
        readingSession.Started.SetResult();
        var reading = new CaptionController(settings, _ => readingSession, a => a());
        var received = new List<CaptionUpdate>();
        var cleared = 0;
        reading.CaptionReceived += received.Add;
        reading.CaptionsCleared += () => cleared++;
        await reading.ToggleAsync();
        readingSession.EmitActivity(AudioActivity.Speech);
        Require(reading.HasAudioActivity && reading.AudioActivity == AudioActivity.Speech, "Speech activity did not reach the controller");
        var longPhrase = string.Join(" ", Enumerable.Range(1, 100).Select(i => $"word{i:000}"));
        readingSession.Emit(new CaptionUpdate("long-one", longPhrase, true));
        Require(received.Count == 1 && received[0].Text == longPhrase, "Display event lost the opening of a long translation");
        Require(reading.Text != longPhrase, "Long-phrase fixture did not exercise bounded history truncation");
        readingSession.Emit(new CaptionUpdate("long-two", longPhrase, true));
        readingSession.Emit(new CaptionUpdate("long-two", longPhrase, true));
        Require(received.Count == 2 && received[1].UtteranceId == "long-two", "Repeated new phrase lost or duplicate final delivered twice");
        await reading.PauseForDisplayBacklogAsync();
        Require(reading.IsPaused && readingSession.Disposed && reading.Errors.Entries.Single().Message.Contains("reading fell behind"),
            "Display backlog did not pause capture with a visible diagnostic");
        readingSession.Emit(new CaptionUpdate("late", "Stale after pause", true));
        readingSession.EmitActivity(AudioActivity.Speech);
        Require(!reading.HasAudioActivity && reading.AudioActivity == AudioActivity.Idle, "Stale activity survived pause");
        Require(received.Count == 2, "Paused session delivered stale raw captions");
        reading.ClearCaptions();
        Require(cleared == 1 && reading.Text == "", "Clear did not notify queued presentation");
        await reading.ShutdownAsync();
        var gatedStarts = 0;
        var gated = new CaptionController(settings, _ => { gatedStarts++; throw new Exception("Capture must wait for reading backlog"); }, a => a())
        { CanAcceptCaptions = () => false };
        await gated.ToggleAsync();
        Require(gated.IsPaused && gatedStarts == 0 && gated.Status.Contains("reading fell behind"), "Early resume started capture with a full display backlog");
        await gated.RunDemoAsync();
        Require(gated.IsPaused && gated.CaptionUtteranceId is null, "Demo bypassed the reading backlog guard");
        var clock = new ManualClock();
        var musicSession = new FakeSession(); musicSession.Started.SetResult();
        var musicController = new CaptionController(settings, _ => musicSession, a => a(), clock);
        var musicClears = 0;
        musicController.CaptionsCleared += () => musicClears++;
        await musicController.ToggleAsync();
        musicSession.Emit("Retained captions");
        musicSession.EmitActivity(AudioActivity.Music);
        clock.Advance(9);
        Require(musicClears == 0 && musicController.Text.Length > 0, "Music cleared captions before ten seconds");
        musicSession.EmitActivity(AudioActivity.Music);
        clock.Advance(1);
        Require(musicClears == 1 && musicController.Text == "", "Continuous music did not clear visible and queued captions once");
        musicSession.Emit("Late translation"); clock.Advance(20);
        Require(musicClears == 1 && musicController.Text == "", "Late translation refilled captions during music");
        musicSession.EmitActivity(AudioActivity.Speech); musicSession.Emit("Speech resumed");
        Require(musicController.Text == "Speech resumed", "Speech after music did not restore captions");
        musicSession.EmitActivity(AudioActivity.Music); clock.Advance(5);
        musicSession.EmitActivity(AudioActivity.Idle); clock.Advance(20);
        Require(musicClears == 1 && musicController.Text.Length > 0, "Interrupted music cleared captions");
        musicSession.EmitActivity(AudioActivity.Music); clock.Advance(9);
        await musicController.PauseAsync(); clock.Advance(20);
        Require(musicClears == 1 && musicController.Text.Length > 0, "Music timer cleared retained captions after pause");
        await musicController.ShutdownAsync();
        var deferred = new Queue<Action>();
        var raceClock = new ManualClock();
        var raceSession = new FakeSession(); raceSession.Started.SetResult();
        var race = new CaptionController(settings, _ => raceSession, deferred.Enqueue, raceClock);
        var raceClears = 0; race.CaptionsCleared += () => raceClears++;
        void Drain() { while (deferred.TryDequeue(out var action)) action(); }
        await race.ToggleAsync();
        raceSession.Emit("Keep on screen"); raceSession.EmitActivity(AudioActivity.Music); Drain();
        raceClock.Advance(10); var oldTimeout = deferred.Dequeue();
        raceSession.EmitActivity(AudioActivity.Idle); raceSession.EmitActivity(AudioActivity.Music); Drain();
        oldTimeout();
        Require(raceClears == 0 && race.Text.Length > 0, "Queued old music timeout cleared a newer music interval");
        raceClock.Advance(10); var pausedTimeout = deferred.Dequeue();
        await race.PauseAsync(); pausedTimeout();
        Require(raceClears == 0 && race.Text.Length > 0, "Queued music timeout cleared captions after pause");
        await race.ShutdownAsync();
        Console.WriteLine("Controller checks passed: paused startup, selected source, pause during connect, rapid resume, stale callbacks, retained captions, shutdown, missing configuration, rolling log.");
    }
    private static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = new();
        private double _seconds;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, _seconds + dueTime.TotalSeconds);
            _timers.Add(timer); return timer;
        }
        public void Advance(double seconds)
        {
            _seconds += seconds;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue(_seconds);
        }
        private sealed class ManualTimer(TimerCallback callback, object? state, double due) : ITimer
        {
            private bool _disposed;
            public void FireIfDue(double now)
            {
                if (_disposed || now < due) return;
                _disposed = true; callback(state);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class CancellableSession : ICaptionSession
    {
        public bool Disposed { get; private set; }
        public event Action<CaptionUpdate>? Caption { add { } remove { } }
        public event Action<string>? Status { add { } remove { } }
        public event Action<string>? Ended { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.Delay(Timeout.Infinite, cancellationToken);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class FakeSession : ICaptionSession, IAudioActivitySession
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public event Action<CaptionUpdate>? Caption;
        public event Action<AudioActivity>? AudioActivityChanged;
        public void EmitActivity(AudioActivity activity) => AudioActivityChanged?.Invoke(activity);
        public event Action<string>? Status { add { } remove { } }
        public event Action<string>? Ended { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken = default) => Started.Task;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public void Emit(string text) => Caption?.Invoke(new("id", text, false));
        public void Emit(CaptionUpdate update) => Caption?.Invoke(update);
    }
}
