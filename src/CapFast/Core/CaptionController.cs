using CapFast.Speech;

namespace CapFast.Core;

// All public operations and dispatched callbacks run on the UI thread.
// Serializing transitions prevents an old start/stop from owning a newer session.
public sealed class CaptionController
{
    private const int MaximumAutomaticRestarts = 3;
    private const string DisplayBacklogMessage = "Caption reading fell behind the speaker. Transcription paused to preserve queued text; let the captions finish, then use Start captions in Settings.";
    private readonly Func<CaptionSettings, ICaptionSession> _factory;
    private readonly Action<Action> _dispatch;
    private readonly CaptionBuffer _buffer = new();
    private readonly SemaphoreSlim _transition = new(1);
    private ICaptionSession? _session;
    private CancellationTokenSource? _starting;
    private CancellationTokenSource? _demo;
    private readonly TimeProvider _timeProvider;
    private ITimer? _inactivityTimer;
    private int _inactivityEpoch;
    private ITimer? _musicTimer;
    private int _musicEpoch;
    private ITimer? _recoveryTimer;
    private int _recoveryEpoch;
    private int _recoveryAttempts;
    private DateTimeOffset? _liveSince;
    private bool _acceptedCaptionSinceStart;
    private bool _musicExpired;
    private bool _wantLive;
    private bool _shutdown;
    private int _generation;
    private bool _hasCaptionText;

    public CaptionController(CaptionSettings settings, Func<CaptionSettings, ICaptionSession> factory, Action<Action> dispatch, TimeProvider? timeProvider = null)
    { _timeProvider = timeProvider ?? TimeProvider.System; Settings = settings; _factory = factory; _dispatch = dispatch; if (settings.ConfigurationError is { } error) Errors.Add(error); }
    public bool CanSetLanguageAssignment => State == CaptionState.Live && _session is ILanguageAssignmentSession;
    public LanguageAssignment? LanguageAssignment { get; private set; }
    public event Action<LanguageAssignment?>? LanguageAssignmentChanged;
    public void ResetLanguageDetection() => (_session as ILanguageAssignmentSession)?.ResetLanguageDetection();
    public void HintInputLanguage(string code) => (_session as ILanguageAssignmentSession)?.HintInputLanguage(code);
    public CaptionSettings Settings { get; }
    public CaptionState State { get; private set; } = CaptionState.Paused;
    public bool IsPaused => State == CaptionState.Paused;
    public AudioActivity AudioActivity { get; private set; } = AudioActivity.Idle;
    public bool HasAudioActivity { get; private set; }
    public string Text => _buffer.Text;
    public string? CaptionUtteranceId { get; private set; }
    public string Status { get; private set; } = "Paused — use Start captions in Settings";
    public ErrorLog Errors { get; } = new();
    public ErrorLog Events { get; } = new();
    public string? MissingConfiguration => Settings.IsLoadingConfiguration
        ? "Loading your local API configuration… You can open Settings while it loads."
        : string.IsNullOrWhiteSpace(Settings.GeminiKey) ? "Add your Google API key in Settings to start captions." : null;
    public event Action? Changed;
    public event Action<CaptionUpdate>? CaptionReceived;
    public event Action? CaptionsCleared;
    public event Action? CaptionsFading;
    public event Action? LocalConfigurationLoaded;
    public Func<bool>? CanAcceptCaptions { get; set; }
    public Func<bool>? HasUnreadCaptions { get; set; }
    public void SettingsChanged()
    {
        Changed?.Invoke();
    }
    public void NotifyLocalConfigurationLoaded() { LocalConfigurationLoaded?.Invoke(); Changed?.Invoke(); }
    public void ClearCaptions()
    { CancelInactivityCountdown(); _buffer.Clear(); _hasCaptionText = false; CaptionUtteranceId = null; CaptionsCleared?.Invoke(); Changed?.Invoke(); }
    public Task PauseForDisplayBacklogAsync() => _shutdown ? Task.CompletedTask : PauseWithErrorAsync(DisplayBacklogMessage);

    private bool DisplayReady()
    {
        if (CanAcceptCaptions?.Invoke() != false) return true;
        _wantLive = false; State = CaptionState.Paused; Status = DisplayBacklogMessage;
        Errors.Add(DisplayBacklogMessage); Changed?.Invoke();
        return false;
    }

    private void CancelInactivityCountdown()
    {
        ++_inactivityEpoch;
        _inactivityTimer?.Dispose(); _inactivityTimer = null;
    }

    private void RestartInactivityCountdown(TimeSpan? delay = null)
    {
        CancelInactivityCountdown();
        var epoch = _inactivityEpoch;
        var generation = _generation;
        _inactivityTimer = _timeProvider.CreateTimer(_ => _dispatch(() =>
        {
            if (generation != _generation || epoch != _inactivityEpoch || !_wantLive) return;
            // Receipt silence is not reading silence: a long final or a burst
            // can still have unread words while the next request is pending.
            if (HasUnreadCaptions?.Invoke() == true)
            {
                RestartInactivityCountdown(TimeSpan.FromSeconds(1));
                return;
            }
            FadeCaptions("No accepted caption for 15 seconds. Existing text faded; capture is still live.");
        }), null, delay ?? TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
    }

    private void FadeCaptions(string reason)
    {
        CancelInactivityCountdown();
        if (!_hasCaptionText) return;
        _hasCaptionText = false;
        _buffer.Clear(); CaptionUtteranceId = null;
        Events.Add(reason);
        CaptionsFading?.Invoke(); Changed?.Invoke();
    }

    private void CancelMusicCountdown()
    {
        ++_musicEpoch;
        _musicTimer?.Dispose(); _musicTimer = null;
        _musicExpired = false;
    }

    private void SetAudioActivity(AudioActivity activity, int generation)
    {
        CancelMusicCountdown();
        AudioActivity = activity;
        if (activity == AudioActivity.Music)
        {
            var epoch = _musicEpoch;
            _musicTimer = _timeProvider.CreateTimer(_ => _dispatch(() =>
            {
                if (generation != _generation || epoch != _musicEpoch || !_wantLive || AudioActivity != AudioActivity.Music) return;
                _musicExpired = true;
                _musicTimer?.Dispose(); _musicTimer = null;
                FadeCaptions("Music continued for 7 seconds. Existing text faded; capture is still live.");
            }), null, TimeSpan.FromSeconds(7), Timeout.InfiniteTimeSpan);
        }
        Changed?.Invoke();
    }

    private void ApplyCaption(CaptionUpdate update)
    {
        // Late results from already accepted speech must not refill the screen
        // after the music timeout. New non-music activity restores delivery.
        if (_musicExpired && AudioActivity == AudioActivity.Music) return;
        if (CaptionContentSafety.IsTechnicalOutput(update.Text) || CaptionContentSafety.IsTechnicalOutput(update.FrenchText))
        {
            Errors.Add("A provider returned technical text instead of a caption. The text was withheld from the audience.");
            Events.Add("Rejected provider output. Listening for the next qualified speech.");
            if (CaptionUtteranceId == update.UtteranceId) ClearCaptions();
            return;
        }
        if (!update.IsFinal && (CaptionContentSafety.HoldPartial(update.Text) || CaptionContentSafety.HoldPartial(update.FrenchText))) return;
        // Filter every provider and every interim revision before either the
        // history preview or animated presentation can receive caption text.
        update = update with {
            Text = update.PrimaryLanguage is null or "en" ? EnglishCaptionFilter.Default.Filter(update.Text) : update.Text,
            FrenchText = update.SecondaryLanguage == "en" && update.FrenchText is { } english ? EnglishCaptionFilter.Default.Filter(english) : update.FrenchText };
        _buffer.Apply(update);
        if (_buffer.LastAcceptedUpdate is not { } accepted) return;
        if (!string.IsNullOrWhiteSpace(accepted.Text) || !string.IsNullOrWhiteSpace(accepted.FrenchText))
        { _hasCaptionText = true; _acceptedCaptionSinceStart = true; RestartInactivityCountdown(); }
        CaptionUtteranceId = accepted.UtteranceId;
        CaptionReceived?.Invoke(accepted);
        Changed?.Invoke();
    }
    public Task ToggleAsync()
    {
        if (_shutdown) return Task.CompletedTask;
        if (!_wantLive) _recoveryAttempts = 0;
        _wantLive = !_wantLive;
        if (!_wantLive) { CancelRecovery(); CancelMusicCountdown(); CancelInactivityCountdown(); ++_generation; _starting?.Cancel(); _demo?.Cancel(); State = CaptionState.Pausing; Changed?.Invoke(); }
        return ReconcileAsync();
    }
    public Task PauseAsync()
    {
        _wantLive = false; CancelRecovery(); _recoveryAttempts = 0; CancelMusicCountdown(); CancelInactivityCountdown(); ++_generation; _starting?.Cancel(); _demo?.Cancel();
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
            if (!DisplayReady()) return;
            if (MissingConfiguration is { } missing)
            { _wantLive = false; State = CaptionState.Paused; Status = missing; Changed?.Invoke(); return; }
            State = CaptionState.Starting; Status = "Connecting…";
            _liveSince = null; _acceptedCaptionSinceStart = false;
            Events.Add("Connecting to the caption service."); Changed?.Invoke();
            var generation = ++_generation;
            using var starting = new CancellationTokenSource();
            _starting = starting;
            try
            {
                var session = _factory(Settings.Copy());
                _session = session;
                LanguageAssignment = null; LanguageAssignmentChanged?.Invoke(null);
                if (session is ILanguageAssignmentSession languages)
                    languages.LanguageAssignmentChanged += assignment => _dispatch(() =>
                    {
                        if (generation != _generation || !_wantLive || languages.LanguageAssignment?.Epoch != assignment?.Epoch) return;
                        LanguageAssignment = assignment; LanguageAssignmentChanged?.Invoke(assignment); Changed?.Invoke();
                    });
                AudioActivity = AudioActivity.Idle;
                HasAudioActivity = session is IAudioActivitySession;
                if (session is IAudioActivitySession audioActivity)
                    audioActivity.AudioActivityChanged += activity => _dispatch(() =>
                    {
                        if (generation != _generation || !_wantLive || AudioActivity == activity) return;
                        SetAudioActivity(activity, generation);
                    });
                if (session is ICaptionDiagnosticsSession diagnostics)
                    diagnostics.Diagnostic += message => _dispatch(() =>
                    {
                        if (generation != _generation || !_wantLive) return;
                        Errors.Add(message); Changed?.Invoke();
                    });
                session.Caption += update => _dispatch(() =>
                {
                    if (generation == _generation && _wantLive &&
                        (session is not ILanguageAssignmentSession assignmentSession || assignmentSession.CanPresentCaption(update))) ApplyCaption(update);
                });
                session.Status += message => _dispatch(() =>
                {
                    if (generation != _generation || !_wantLive) return;
                    Status = message;
                    if (message == "Google connection interrupted — reconnecting…")
                        Events.Add("Caption service connection is recovering.");
                    else if (message == "Speech detected — connecting to Google…")
                        Events.Add("Qualified speech detected; connecting to the caption service.");
                    else if (message == "Listening locally — Google reconnects when speech resumes.")
                        Events.Add("Google connection closed while idle; listening locally for speech.");
                    else if (message is "Google live — translating qualified speech." or "Gemini connected — starting the selected Mac audio input.")
                        Events.Add("Caption service connected.");
                    else if (message == "No speech recognized. Check the audio input.")
                        Events.Add("No speech recognized. Check the selected audio input.");
                    Changed?.Invoke();
                });
                void SessionFailed(string message, bool operatorAction) => _dispatch(() =>
                {
                    if (generation != _generation || !_wantLive) return;
                    Events.Add("Caption session stopped because of an issue.");
                    _ = RecoverFromEndedAsync(generation, message, operatorAction);
                });
                if (session is ICaptionFailureSession classified)
                    classified.Failure += failure => SessionFailed(failure.SafeMessage, failure.RequiresOperatorAction);
                else session.Ended += message => SessionFailed(message, CaptionFailurePolicy.RequiresOperatorAction(message));
                await session.StartAsync(starting.Token);
                if (!_wantLive || generation != _generation) { await StopAsync(); return; }
                State = CaptionState.Live; Status = "Live — use Pause captions in Settings";
                _liveSince = _timeProvider.GetUtcNow();
                Events.Add("Caption session is live.");
                if (_hasCaptionText) RestartInactivityCountdown();
                Changed?.Invoke();
            }
            catch (OperationCanceledException) when (starting.IsCancellationRequested)
            {
                await StopAsync();
            }
            catch (Exception error)
            {
                var message = error is CaptionSessionException ? error.Message
                    : "Could not start transcription. Check Settings diagnostics, service configuration, and connection.";
                // A session-ended callback may already have recorded its precise error.
                if (_wantLive) Errors.Add(message);
                if (_wantLive) Events.Add("Caption session could not start.");
                ResetRecoveryBudgetAfterHealthyRun();
                var recover = _wantLive && !CaptionFailurePolicy.RequiresOperatorAction(message) &&
                    _recoveryAttempts < MaximumAutomaticRestarts;
                await StopAsync();
                if (recover) ScheduleRecovery(message);
                else
                {
                    _wantLive = false;
                    Status = _recoveryAttempts >= MaximumAutomaticRestarts && !CaptionFailurePolicy.RequiresOperatorAction(message)
                        ? $"Automatic recovery stopped after {MaximumAutomaticRestarts} attempts. {message} Check the connection, then use Start captions in Settings."
                        : message;
                    if (_recoveryAttempts >= MaximumAutomaticRestarts) Events.Add("Automatic recovery exhausted; operator action is needed.");
                    Changed?.Invoke();
                }
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
    private void CancelRecovery()
    {
        ++_recoveryEpoch;
        _recoveryTimer?.Dispose(); _recoveryTimer = null;
    }
    private void ResetRecoveryBudgetAfterHealthyRun()
    {
        // One accepted caption proves the restarted path worked; a sparse talk can
        // then stay live through a long silence without consuming a lifetime retry budget.
        if (_recoveryAttempts == 0 || !_acceptedCaptionSinceStart || _liveSince is not { } since ||
            _timeProvider.GetUtcNow() - since < TimeSpan.FromMinutes(2)) return;
        _recoveryAttempts = 0;
        Events.Add("Previous recovery attempts cleared after two minutes live with an accepted caption.");
    }
    private void ScheduleRecovery(string message)
    {
        if (!_wantLive || _shutdown) return;
        CancelRecovery();
        var attempt = ++_recoveryAttempts;
        var delay = TimeSpan.FromSeconds(1 << (attempt - 1));
        var epoch = _recoveryEpoch;
        State = CaptionState.Starting;
        Status = $"Automatic recovery {attempt}/{MaximumAutomaticRestarts} in {delay.TotalSeconds:0} seconds…";
        Events.Add($"Caption session restart scheduled (attempt {attempt}/{MaximumAutomaticRestarts}).");
        Changed?.Invoke();
        _recoveryTimer = _timeProvider.CreateTimer(_ => _dispatch(() =>
        {
            if (epoch != _recoveryEpoch || !_wantLive || _shutdown) return;
            _recoveryTimer?.Dispose(); _recoveryTimer = null;
            Events.Add($"Restarting caption session (attempt {attempt}/{MaximumAutomaticRestarts}).");
            _ = ReconcileAsync();
        }), null, delay, Timeout.InfiniteTimeSpan);
    }
    private async Task RecoverFromEndedAsync(int generation, string message, bool operatorAction)
    {
        await _transition.WaitAsync();
        try
        {
            if (generation != _generation || !_wantLive || _shutdown) return;
            Errors.Add(message);
            ResetRecoveryBudgetAfterHealthyRun();
            var recover = !operatorAction &&
                _recoveryAttempts < MaximumAutomaticRestarts;
            await StopAsync();
            if (recover) ScheduleRecovery(message);
            else
            {
                _wantLive = false;
                Status = _recoveryAttempts >= MaximumAutomaticRestarts && !operatorAction
                    ? $"Automatic recovery stopped after {MaximumAutomaticRestarts} attempts. {message} Check the connection, then use Start captions in Settings."
                    : message;
                Events.Add(_recoveryAttempts >= MaximumAutomaticRestarts && !operatorAction
                    ? "Automatic recovery exhausted; operator action is needed."
                    : "Caption session stopped; operator action is needed.");
                Changed?.Invoke();
            }
        }
        finally { _transition.Release(); }
    }
    private async Task StopAsync()
    {
        ++_generation;
        CancelMusicCountdown(); CancelInactivityCountdown();
        AudioActivity = AudioActivity.Idle; HasAudioActivity = false;
        _demo?.Cancel(); _demo?.Dispose(); _demo = null;
        var session = _session; _session = null;
        if (session != null)
        {
            State = CaptionState.Pausing; Changed?.Invoke();
            try { await session.DisposeAsync(); }
            catch { Errors.Add("Could not cleanly stop transcription."); }
        }
        State = CaptionState.Paused; Status = "Paused — use Start captions in Settings"; Changed?.Invoke();
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
            if (!DisplayReady()) return;
            _demo = new CancellationTokenSource(); token = _demo.Token;
            generation = ++_generation;
            { LanguageAssignment = new("sw", "en", "fr", -generation); LanguageAssignmentChanged?.Invoke(LanguageAssignment); }
            _wantLive = true; State = CaptionState.Live; Status = "Caption demo — use Pause captions in Settings"; Changed?.Invoke();
        }
        finally { _transition.Release(); }
        try
        {
            var frames = new (string Text, int Phrase, bool Final)[]
            {
                ("Welcome", 0, false),
                ("Welcome to today's", 0, false),
                ("Welcome to today's seminar.", 0, true),
                ("We are sharing ideas", 1, false),
                ("We are sharing ideas together.", 1, true),
                ("Our community brings together people with different experiences, and everyone has something valuable to contribute.", 2, true),
                ("Today we will listen carefully, ask questions, and consider how these ideas can help our families.", 3, true),
                ("A small act of kindness can make a lasting difference to someone who is facing a difficult day.", 4, true),
                ("When we work together with patience and understanding, we discover possibilities that we could not see alone.", 5, true),
                ("Thank you for joining us and for taking the time to learn from one another.", 6, true)
            };
            var frenchFrames = new[]
            {
                "Bienvenue",
                "Bienvenue au séminaire",
                "Bienvenue au séminaire d'aujourd'hui.",
                "Nous partageons des idées",
                "Nous partageons des idées ensemble.",
                "Notre communauté rassemble des personnes aux expériences différentes, et chacun a quelque chose de précieux à apporter.",
                "Aujourd'hui, nous allons écouter attentivement, poser des questions et réfléchir à la façon dont ces idées peuvent aider nos familles.",
                "Un petit geste de bonté peut faire une différence durable pour une personne qui traverse une journée difficile.",
                "Lorsque nous travaillons ensemble avec patience et compréhension, nous découvrons des possibilités que nous ne pouvions pas voir seuls.",
                "Merci de vous joindre à nous et de prendre le temps d'apprendre les uns des autres."
            };
            for (var i = 0; i < frames.Length; i++)
            {
                await Task.Delay(900, token);
                if (generation != _generation) return;
                var update = new CaptionUpdate($"demo-{generation}-{frames[i].Phrase}", frames[i].Text, frames[i].Final,
                    frenchFrames[i],
                    SourceLanguage: "sw",
                    PrimaryLanguage: "en",
                    SecondaryLanguage: "fr");
                ApplyCaption(update);
            }
        }
        catch (OperationCanceledException) { }
        finally { if (generation == _generation) await PauseAsync(); }
    }
}
