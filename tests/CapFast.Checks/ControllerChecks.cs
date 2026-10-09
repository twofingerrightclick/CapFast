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
        Console.WriteLine("Controller checks passed: paused startup, selected source, pause during connect, rapid resume, stale callbacks, retained captions, shutdown, missing configuration, rolling log.");
    }
    private static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
    private sealed class CancellableSession : ICaptionSession
    {
        public bool Disposed { get; private set; }
        public event Action<CaptionUpdate>? Caption { add { } remove { } }
        public event Action<string>? Status { add { } remove { } }
        public event Action<string>? Ended { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.Delay(Timeout.Infinite, cancellationToken);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class FakeSession : ICaptionSession
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public event Action<CaptionUpdate>? Caption;
        public event Action<string>? Status { add { } remove { } }
        public event Action<string>? Ended { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken = default) => Started.Task;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public void Emit(string text) => Caption?.Invoke(new("id", text, false));
    }
}
