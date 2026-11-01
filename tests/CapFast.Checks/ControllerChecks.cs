using CapFast.Core;
using CapFast.Speech;

internal static class ControllerChecks
{
    public static async Task RunAsync()
    {
        var recordingSession = new FinalizingRecordingSession();
        var recordingController = new CaptionController(new CaptionSettings { GeminiKey = "test" }, _ => recordingSession, action => action());
        await recordingController.ToggleAsync();
        var recordingPause = recordingController.PauseAsync();
        Require(recordingController.State == CaptionState.Pausing && !recordingPause.IsCompleted,
            "Pause completed before recording finalization.");
        recordingSession.Saved.SetResult();
        await recordingPause;
        Require(recordingController.IsPaused && recordingController.Errors.Entries.Single().Message == "Recording finalization failed.",
            "Recording finalization error disappeared during pause or stale caption diagnostics leaked.");
        await recordingController.ShutdownAsync();
        await CheckUnreadInactivityAsync(burst: false);
        await CheckUnreadInactivityAsync(burst: true);
        var demo = new CaptionController(new CaptionSettings(), _ => throw new Exception("Demo opened a capture session"), a => a());
        var demoTask = demo.RunDemoAsync();
        Require(demo.State == CaptionState.Live && !demo.HasActiveAudioSession,
            "Sample captions enabled automatic microphone monitoring");
        await demo.PauseAsync();
        await demoTask;
        await demo.ShutdownAsync();
        var dispatch = new Queue<Action>();
        var assignmentSession = new AssignmentSession();
        var assignmentController = new CaptionController(new CaptionSettings { GeminiKey = "test" }, _ => assignmentSession, dispatch.Enqueue);
        await assignmentController.ToggleAsync();
        while (dispatch.TryDequeue(out var action)) action();
        assignmentSession.Emit(new("stale-epoch", "Obsolete caption", true, AssignmentEpoch: 1));
        assignmentSession.HintInputLanguage("fr");
        while (dispatch.TryDequeue(out var action)) action();
        Require(assignmentController.Text == "", "Queued old assignment caption survived hint.");
        assignmentSession.Emit(new("current-epoch", "Current caption", true, AssignmentEpoch: 2));
        while (dispatch.TryDequeue(out var action)) action();
        Require(assignmentController.Text == "Current caption", "Current assignment caption was rejected.");
        assignmentSession.Emit(new("before-reset", "Old after reset", true, AssignmentEpoch: 2));
        assignmentSession.ResetLanguageDetection();
        while (dispatch.TryDequeue(out var action)) action();
        Require(assignmentController.Text == "Current caption", "Reset replaced retained text with queued obsolete caption.");
        await assignmentController.ShutdownAsync();
        var log = new ErrorLog(3);
        Require(CommandCenterStatus.Describe(CaptionState.Live, AudioActivity.Idle,
                "Listening locally — Google reconnects when speech resumes.").Contains("listening locally") &&
                !CommandCenterStatus.Describe(CaptionState.Live, AudioActivity.Idle,
                    "Listening locally — Google reconnects when speech resumes.").Contains("recovering"),
            "Stable local listening was mislabeled as an active recovery");
        Require(CommandCenterStatus.Describe(CaptionState.Live, AudioActivity.Idle,
                "Google connection interrupted — reconnecting…").Contains("recovering"),
            "Interrupted connection was not labeled as recovering");
        Require(CommandCenterStatus.Describe(CaptionState.Starting, AudioActivity.Idle,
                "Automatic recovery 1/3 in 1 seconds…").Contains("recovering"),
            "Scheduled restart was not labeled as recovering");
        Require(!CommandCenterStatus.Describe(CaptionState.Live, AudioActivity.Speech, "Live")
                .Contains("Waiting for transcription") &&
                !CommandCenterStatus.Describe(CaptionState.Live, AudioActivity.Idle, "Live")
                .Contains("No qualified speech"),
            "Command center inferred translation or qualification from audio activity alone");
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
        Require(!controller.HasActiveAudioSession, "Connecting enabled automatic monitoring before capture started");
        var pause = controller.ToggleAsync();
        sessions[0].Emit("stale");
        sessions[0].Started.SetResult();
        await Task.WhenAll(start, pause);
        Require(controller.IsPaused && sessions[0].Disposed && controller.Text == "", "Pause during start failed");
        Require(controller.CaptionUtteranceId is null, "Stale caption changed display identity");
        start = controller.ToggleAsync(); sessions[1].Started.SetResult(); await start;
        Require(controller.HasActiveAudioSession, "Live capture did not enable automatic monitoring");
        sessions[1].Emit("Keep these captions");
        await controller.PauseAsync();
        Require(!controller.HasActiveAudioSession, "Paused capture retained automatic monitoring");
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
        var recoveryClock = new ManualClock();
        var recoverySessions = new List<FakeSession>();
        var recovering = new CaptionController(settings, _ =>
        {
            var session = new FakeSession(); session.Started.SetResult(); recoverySessions.Add(session); return session;
        }, a => a(), recoveryClock);
        await recovering.ToggleAsync();
        recoverySessions[0].EmitEnded("Google audio streaming failed. Check the network and restart the session.");
        await UntilAsync(() => recoverySessions[0].Disposed && recovering.Status.Contains("recovery", StringComparison.OrdinalIgnoreCase));
        Require(recovering.State == CaptionState.Starting && recoverySessions.Count == 1,
            "Recoverable session failure stopped instead of scheduling restart");
        recoveryClock.Advance(1);
        await UntilAsync(() => recoverySessions.Count == 2 && recovering.State == CaptionState.Live);
        recoverySessions[0].Emit("Stale speech");
        recoverySessions[1].Emit("Recovered speech");
        Require(recovering.Text == "Recovered speech", "Restart did not accept new speech or accepted stale callback");
        Require(recovering.Events.Entries.Any(entry => entry.Message.Contains("restart", StringComparison.OrdinalIgnoreCase)),
            "Command center log omitted automatic restart");
        recoverySessions[1].EmitEnded("Google audio streaming failed. Check the network and restart the session.");
        await UntilAsync(() => recoverySessions[1].Disposed);
        await recovering.PauseAsync();
        recoveryClock.Advance(30);
        Require(recoverySessions.Count == 2 && recovering.IsPaused, "Manual pause did not cancel pending recovery");
        await recovering.ShutdownAsync();
        var terminalSessions = new List<FakeSession>();
        var terminal = new CaptionController(settings, _ =>
        {
            var session = new FakeSession(); session.Started.SetResult(); terminalSessions.Add(session); return session;
        }, a => a(), recoveryClock);
        await terminal.ToggleAsync();
        terminalSessions[0].EmitEnded("Google rejected authentication (401). Check the API key in Settings.");
        await UntilAsync(() => terminal.IsPaused);
        recoveryClock.Advance(30);
        Require(terminalSessions.Count == 1 && terminal.Status.Contains("401"), "Terminal authentication failure was retried");
        await terminal.ShutdownAsync();
        var classifiedSession = new ClassifiedSession();
        var classified = new CaptionController(settings, _ => classifiedSession, a => a(), recoveryClock);
        await classified.ToggleAsync();
        classifiedSession.Fail(new("An operator must inspect this safe provider failure.", true));
        await UntilAsync(() => classified.IsPaused);
        recoveryClock.Advance(30);
        Require(classifiedSession.Disposed && classified.Status.Contains("operator") && classified.Events.Entries.Last().Message.Contains("operator action"),
            "Typed terminal failure did not reach the controller");
        await classified.ShutdownAsync();
        Require(CaptionFailurePolicy.RequiresOperatorAction("Gemini quota is exhausted (service 8).") &&
                CaptionFailurePolicy.RequiresOperatorAction("Gemini denied access (service 7). Check project billing.") &&
                CaptionFailurePolicy.RequiresOperatorAction("Gemini rejected the session setup (WebSocket 1008).") &&
                CaptionFailurePolicy.RequiresOperatorAction("The selected Mac audio input stopped producing audio.") &&
                CaptionFailurePolicy.RequiresOperatorAction("The selected audio input is unavailable. Choose another input in Settings and start again.") &&
                CaptionFailurePolicy.RequiresOperatorAction("The selected audio input has an unsupported audio format. Choose another input in Settings and start again.") &&
                CaptionFailurePolicy.RequiresOperatorAction("Mac audio capture could not start. Check the selected input and close other apps holding the device.") &&
                CaptionFailurePolicy.RequiresOperatorAction("Mac speech classification is unavailable. Capture stopped without uploading unclassified audio. Relaunch the packaged app.") &&
                CaptionFailurePolicy.RequiresOperatorAction("Mac speech classification returned an invalid frame. Capture stopped; restart the packaged app.") &&
                !CaptionFailurePolicy.RequiresOperatorAction("Gemini connected, but the startup deadline expired before audio capture began. Check the connection and service availability.") &&
                !CaptionFailurePolicy.RequiresOperatorAction("Gemini closed the session with an internal server error (WebSocket 1011)."),
            "Failure policy misclassified quota, setup timeout, device, or ambiguous service error");
        var startupClock = new ManualClock();
        var startupAttempts = 0;
        var startup = new CaptionController(settings, _ =>
        {
            if (++startupAttempts == 1)
                throw new CaptionSessionException("Gemini connected, but the startup deadline expired before audio capture began. Check the connection and service availability.");
            var session = new FakeSession(); session.Started.SetResult(); return session;
        }, a => a(), startupClock);
        await startup.ToggleAsync();
        Require(startup.State == CaptionState.Starting && startupAttempts == 1 &&
                startup.Status.Contains("Automatic recovery"),
            "Transient Gemini startup deadline did not schedule recovery");
        startupClock.Advance(1);
        await UntilAsync(() => startup.State == CaptionState.Live && startupAttempts == 2);
        await startup.ShutdownAsync();
        var exhaustClock = new ManualClock();
        var exhaustSessions = new List<FakeSession>();
        var exhausting = new CaptionController(settings, _ =>
        {
            var session = new FakeSession(); session.Started.SetResult(); exhaustSessions.Add(session); return session;
        }, a => a(), exhaustClock);
        await exhausting.ToggleAsync();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            exhaustSessions[attempt].EmitEnded("Gemini service is unavailable (service 503).");
            await UntilAsync(() => exhaustSessions[attempt].Disposed);
            exhaustClock.Advance(1 << attempt);
            var next = attempt + 2;
            await UntilAsync(() => exhaustSessions.Count == next && exhausting.State == CaptionState.Live);
        }
        exhaustSessions[3].EmitEnded("Gemini service is unavailable (service 503).");
        await UntilAsync(() => exhausting.IsPaused);
        exhaustClock.Advance(60);
        Require(exhaustSessions.Count == 4 && exhausting.Status.Contains("after 3 attempts") &&
                exhausting.Events.Entries.Last().Message.Contains("exhausted"),
            "Automatic restart budget was not bounded or reported");
        await exhausting.ShutdownAsync();
        var stableClock = new ManualClock();
        var stableSessions = new List<FakeSession>();
        var stable = new CaptionController(settings, _ =>
        {
            var session = new FakeSession(); session.Started.SetResult(); stableSessions.Add(session); return session;
        }, a => a(), stableClock);
        await stable.ToggleAsync();
        for (var failure = 0; failure < 4; failure++)
        {
            stableSessions[failure].Emit(new CaptionUpdate($"healthy-{failure}", "Caption delivered.", true));
            stableClock.Advance(121);
            stableSessions[failure].EmitEnded("Gemini service is unavailable (service 503).");
            await UntilAsync(() => stableSessions[failure].Disposed);
            Require(stable.Status.Contains("1/3") && (failure == 0 || stable.Events.Entries.Any(entry =>
                    entry.Message.Contains("two minutes live with an accepted caption"))),
                "Two-minute live session with an accepted caption did not reset recovery budget");
            stableClock.Advance(1);
            var expected = failure + 2;
            await UntilAsync(() => stableSessions.Count == expected && stable.State == CaptionState.Live);
        }
        await stable.ShutdownAsync();
        var technicalSession = new FakeSession();
        technicalSession.Started.SetResult();
        var technicalController = new CaptionController(settings, _ => technicalSession, a => a());
        await technicalController.ToggleAsync();
        technicalSession.Emit(new CaptionUpdate("technical", "JSON parsing ", false));
        Require(technicalController.Text == "", "Partial diagnostic prefix reached direct audience captions");
        technicalSession.Emit(new CaptionUpdate("technical", "JSON parsing error: {\"unexpected\": true}", true));
        Require(technicalController.Text == "" && technicalController.Errors.Entries.Last().Message.Contains("withheld"),
            "Technical text reached direct audience captions or was not diagnosed");
        Require(technicalController.Events.Entries.Last().Message.Contains("Rejected provider output") &&
                !technicalController.Events.Entries.Any(entry => entry.Message.Contains("unexpected")),
            "Command center event did not explain rejected output safely");
        technicalSession.Emit(new CaptionUpdate("bracketed", "[The speaker resumes] Welcome.", true));
        Require(technicalController.Text.Contains("[The speaker resumes] Welcome."), "Legitimate bracketed caption was rejected");
        technicalController.ClearCaptions();
        technicalSession.Emit(new CaptionUpdate("revised", "Ordinary draft", false));
        technicalSession.Emit(new CaptionUpdate("revised", "Error: {\"secret\": true}", true));
        Require(technicalController.Text == "", "Revised technical utterance left its earlier partial on screen");
        technicalSession.Emit(new CaptionUpdate("speech", "Welcome everyone.", true));
        Require(technicalController.Text == "Welcome everyone.", "A rejected provider result blocked later speech");
        await technicalController.ShutdownAsync();
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
        var activityClock = new ManualClock();
        var activitySession = new FakeSession(); activitySession.Started.SetResult();
        var activityController = new CaptionController(settings, _ => activitySession, a => a(), activityClock);
        var activityFades = 0;
        activityController.CaptionsFading += () => activityFades++;
        await activityController.ToggleAsync();
        activitySession.Emit("Initial caption");
        activitySession.EmitActivity(AudioActivity.Speech);
        activitySession.EmitActivity(AudioActivity.Idle);
        activityClock.Advance(8);
        Require(activityFades == 0 && activityController.Text.Length > 0,
            "Audio activity faded captions before the caption inactivity timeout");
        activitySession.Emit("Next caption");
        Require(activityController.Text.Contains("Next caption"), "Audio activity suppressed incoming captions");
        activityClock.Advance(8);
        Require(activityFades == 0, "Fresh captions failed to extend visibility beyond fifteen seconds total");
        await activityController.ShutdownAsync();
        var idleClock = new ManualClock();
        var idleSession = new FakeSession(); idleSession.Started.SetResult();
        var idle = new CaptionController(settings, _ => idleSession, a => a(), idleClock);
        var idleFades = 0; var immediateClears = 0;
        idle.CaptionsFading += () => idleFades++;
        idle.CaptionsCleared += () => immediateClears++;
        await idle.ToggleAsync();
        idleSession.Emit(new CaptionUpdate("first", "First caption", true));
        idleClock.Advance(14);
        Require(idleFades == 0 && idle.Text.Length > 0, "Silence expired before fifteen seconds");
        idleSession.Emit(new CaptionUpdate("next", "Fresh caption", true));
        idleClock.Advance(1);
        Require(idleFades == 0, "New caption did not restart inactivity countdown");
        idleSession.EmitActivity(AudioActivity.Speech);
        idleClock.Advance(13);
        idleSession.Emit(new CaptionUpdate("empty", "", true));
        idleSession.Emit(new CaptionUpdate("next", "Duplicate caption", true));
        idleClock.Advance(1);
        Require(idleFades == 1 && immediateClears == 0 && idle.Text == "" && idle.CaptionUtteranceId is null,
            "Missing translations did not request a fade, or empty/duplicate results extended the timeout");
        Require(idle.Events.Entries.Last().Message.Contains("capture is still live"),
            "Command center log did not distinguish an inactivity fade from stopped capture");
        idleClock.Advance(30);
        Require(idleFades == 1, "Empty screen repeatedly faded");
        idleSession.Emit("Speech returned");
        Require(idle.Text == "Speech returned", "New text after inactivity was suppressed");
        await idle.PauseAsync(); idleClock.Advance(20);
        Require(idleFades == 1 && idle.Text.Length > 0, "Inactivity cleared paused captions");
        await idle.ToggleAsync(); idleClock.Advance(15);
        Require(idleFades == 2 && idle.Text == "", "Resuming did not expire retained captions");
        await idle.ShutdownAsync();
        var deferred = new Queue<Action>();
        var raceClock = new ManualClock();
        var raceSession = new FakeSession(); raceSession.Started.SetResult();
        var race = new CaptionController(settings, _ => raceSession, deferred.Enqueue, raceClock);
        var raceClears = 0; race.CaptionsFading += () => raceClears++;
        void Drain() { while (deferred.TryDequeue(out var action)) action(); }
        await race.ToggleAsync();
        raceSession.EmitDiagnostic("Recovery may have left an unfinished caption."); Drain();
        Require(race.Errors.Entries.Count == 1 && !race.IsPaused, "Recovery diagnostic was hidden or stopped capture");
        raceSession.EmitStatus("Google connection interrupted — reconnecting…"); Drain();
        Require(race.Events.Entries.Last().Message.Contains("recovering"),
            "Command center log did not record reconnection");
        raceSession.Emit("Old caption"); Drain();
        raceClock.Advance(15); var staleInactivity = deferred.Dequeue();
        raceSession.Emit("New caption"); Drain(); staleInactivity();
        Require(raceClears == 0 && race.Text == "New caption", "Queued inactivity timeout erased newer captions");
        await race.PauseAsync();
        raceSession.EmitDiagnostic("Stale recovery notice"); Drain();
        Require(race.Errors.Entries.Count == 1, "Stale recovery diagnostics survived pause");
        Require(raceClears == 0 && race.Text.Length > 0, "Pause cleared retained captions");
        await race.ShutdownAsync();
        var filteredSession = new FakeSession(); filteredSession.Started.SetResult();
        var filtered = new CaptionController(settings, _ => filteredSession, a => a());
        var filteredUpdates = new List<CaptionUpdate>();
        var filteredPresentation = new CaptionPresentation();
        filteredPresentation.Configure(100, 3, text => text.Length, "filter-test");
        filtered.CaptionReceived += update => { filteredUpdates.Add(update); filteredPresentation.Submit(update); };
        await filtered.ToggleAsync();
        filteredSession.Emit(new CaptionUpdate("filtered-final", "Welcome sex today FUCK everyone.", true));
        Require(filtered.Text == "Welcome today everyone." && filteredUpdates.Single().Text == filtered.Text,
            "Unfiltered text reached caption history or presentation");
        Require(filteredUpdates[0].UtteranceId == "filtered-final" && filteredUpdates[0].IsFinal,
            "Filtering changed source identity or finality");
        Require(filteredPresentation.TryPresent(0, out var filterFrame) && filterFrame!.Lines.Single() == filtered.Text,
            "Displayed frame contained unfiltered text");
        filteredPresentation.Presented(0);
        filteredSession.Emit(new CaptionUpdate("filtered-partial", "se", false));
        Require(filteredPresentation.TryPresent(0, out _), "Provisional caption missing");
        filteredPresentation.Presented(0);
        filteredSession.Emit(new CaptionUpdate("filtered-partial", "sex", false));
        Require(filteredUpdates.Last().Text == "" && filtered.Text == "Welcome today everyone." &&
            filteredPresentation.CurrentLines.SequenceEqual(new[] { "Welcome today everyone." }),
            "Fully removed interim caption did not withdraw its previous tail");
        filteredSession.Emit(new CaptionUpdate("filtered-partial", "Welcome again.", true));
        Require(filteredPresentation.TryPresent(0, out filterFrame) && filterFrame!.Lines.Last() == "Welcome again.",
            "Empty filtered interim update prematurely finalized its source");
        filteredPresentation.Presented(0);
        filteredSession.Emit(new CaptionUpdate("only-blocked", "sex fuck", true));
        Require(filteredUpdates.Last().Text == "" && filteredPresentation.PendingPhrases == 0,
            "Fully blocked final occupied caption space");
        filteredSession.Emit(new CaptionUpdate("only-blocked", "Late safe duplicate", true));
        Require(filteredUpdates.Last().Text == "", "Filtered final lost duplicate suppression");
        await filtered.ShutdownAsync();
        var bilingualSettings = new CaptionSettings(loadLocalConfiguration: false) { GeminiKey = "test" };
        var bilingualSession = new FakeSession(); bilingualSession.Started.SetResult();
        var bilingualClock = new ManualClock();
        var bilingual = new CaptionController(bilingualSettings, snapshot =>
        {
            return bilingualSession;
        }, a => a(), bilingualClock);
        var bilingualUpdates = new List<CaptionUpdate>();
        var bilingualFades = 0; var languageClears = 0;
        bilingual.CaptionReceived += bilingualUpdates.Add;
        bilingual.CaptionsFading += () => bilingualFades++;
        bilingual.CaptionsCleared += () => languageClears++;
        await bilingual.ToggleAsync();
        bilingualSession.Emit(new CaptionUpdate("pair", "Welcome FUCK everyone.", true, "Bienvenue à tous."));
        Require(bilingualUpdates.Single() == new CaptionUpdate("pair", "Welcome everyone.", true, "Bienvenue à tous."),
            "English display filtering lost the French translation or pair identity");
        bilingualSession.Emit(new CaptionUpdate("french-only", "sex", true, "La séance commence."));
        bilingualClock.Advance(14);
        Require(bilingualFades == 0, "French-only visible text expired early");
        bilingualClock.Advance(1);
        Require(bilingualFades == 1, "Bilingual captions did not fade after inactivity");
        bilingualSession.Emit(new CaptionUpdate("only-french-after-clear", "sex", true, "La séance continue."));
        bilingualClock.Advance(15);
        Require(bilingualFades == 2 && bilingual.Text == "", "French text failed to fade when English history was empty");
        await bilingual.PauseAsync();
        bilingual.ClearCaptions();
        Require(languageClears == 1 && bilingual.CaptionUtteranceId is null,
            "Clearing paired captions retained stale captions");
        await bilingual.ShutdownAsync();
        Console.WriteLine("Controller checks passed: paused startup, selected source, pause during connect, rapid resume, stale callbacks, retained captions, shutdown, missing configuration, rolling log.");
    }
    private static async Task CheckUnreadInactivityAsync(bool burst)
    {
        var clock = new ManualClock();
        var session = new FakeSession(); session.Started.SetResult();
        var controller = new CaptionController(new CaptionSettings(loadLocalConfiguration: false) { GeminiKey = "test" },
            _ => session, action => action(), clock);
        // This regression checks complete reading time when catch-up is disabled.
        var presentation = new CaptionPresentation { ReadingWordsPerMinute = 180, CatchUpEnabled = false };
        presentation.Configure(24, 1, text => text.Length, "unread-inactivity");
        double Now() => (clock.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalSeconds;
        controller.HasUnreadCaptions = () => presentation.HasUnreadCaptions(Now());
        controller.CaptionReceived += update => presentation.Submit(update, Now());
        var fadedAt = -1d;
        controller.CaptionsFading += () => { fadedAt = Now(); presentation.Clear(); };
        controller.CaptionsCleared += presentation.Clear;
        var words = Enumerable.Range(1, 60).Select(index => $"w{index:00}").ToArray();
        var displayed = new List<string>();
        await controller.ToggleAsync();
        if (burst)
            for (var index = 0; index < 4; index++)
                session.Emit(new CaptionUpdate($"burst-{index}", string.Join(" ", words.Skip(index * 15).Take(15)), true));
        else session.Emit(new CaptionUpdate("long-final", string.Join(" ", words), true));
        for (var tick = 0; tick < 240 && fadedAt < 0; tick++)
        {
            if (presentation.TryPresent(Now(), out var frame))
            {
                displayed.AddRange(frame!.Lines.Single().Split(' ', StringSplitOptions.RemoveEmptyEntries));
                presentation.Presented(Now());
            }
            clock.Advance(.25);
            if (Now() == 15)
                Require(fadedAt < 0 && presentation.HasUnreadCaptions(Now()), "Inactivity erased unread long-final words at fifteen seconds");
            if (Now() == 16)
                session.EmitDiagnostic("Translation request is still pending.");
        }
        Require(displayed.SequenceEqual(words), "Inactivity discarded or replayed words while a later result was pending");
        Require(fadedAt >= 20 && !presentation.HasUnreadCaptions(Now()), "Caption did not finish its reading time before fading");
        // Explicit clearing still removes unread text immediately.
        session.Emit(new CaptionUpdate("clear-unread", string.Join(" ", words), true));
        controller.ClearCaptions();
        Require(!presentation.HasUnreadCaptions(Now()), "Explicit clear retained unread captions");
        await controller.ShutdownAsync();
    }

    private static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
    private static async Task UntilAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(5);
        Require(ready(), "Timed out waiting for controller transition");
    }
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = new();
        private double _seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(_seconds);
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
    private sealed class AssignmentSession : ICaptionSession, ILanguageAssignmentSession
    {
        public LanguageAssignment? LanguageAssignment { get; private set; } = new("en", "sw", "fr", 1);
        public event Action<LanguageAssignment?>? LanguageAssignmentChanged;
        public event Action<CaptionUpdate>? Caption;
        public event Action<string>? Status { add { } remove { } }
        public event Action<string>? Ended { add { } remove { } }
        public void Emit(CaptionUpdate update) => Caption?.Invoke(update);
        public void HintInputLanguage(string code) { LanguageAssignment = new(code, "en", "sw", 2); LanguageAssignmentChanged?.Invoke(LanguageAssignment); }
        public void ResetLanguageDetection() { LanguageAssignment = null; LanguageAssignmentChanged?.Invoke(null); }
        public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FinalizingRecordingSession : ICaptionSession, ICaptionRecordingSession, ICaptionDiagnosticsSession
    {
        public readonly TaskCompletionSource Saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<string>? RecordingDiagnostic;
        public event Action<string>? Diagnostic;
        public event Action<CaptionUpdate>? Caption { add { } remove { } }
        public event Action<string>? Status { add { } remove { } }
        public event Action<string>? Ended { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async ValueTask DisposeAsync()
        {
            await Saved.Task;
            Diagnostic?.Invoke("Stale live caption diagnostic.");
            RecordingDiagnostic?.Invoke("Recording finalization failed.");
        }
    }

    private sealed class FakeSession : ICaptionSession, IAudioActivitySession, ICaptionDiagnosticsSession
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public event Action<CaptionUpdate>? Caption;
        public event Action<AudioActivity>? AudioActivityChanged;
        public event Action<string>? Diagnostic;
        public void EmitDiagnostic(string message) => Diagnostic?.Invoke(message);
        public void EmitActivity(AudioActivity activity) => AudioActivityChanged?.Invoke(activity);
        public event Action<string>? Status;
        public event Action<string>? Ended;
        public void EmitStatus(string message) => Status?.Invoke(message);
        public void EmitEnded(string message) => Ended?.Invoke(message);
        public Task StartAsync(CancellationToken cancellationToken = default) => Started.Task;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public void Emit(string text) => Caption?.Invoke(new("id", text, false));
        public void Emit(CaptionUpdate update) => Caption?.Invoke(update);
    }
    private sealed class ClassifiedSession : ICaptionSession, ICaptionFailureSession
    {
        public bool Disposed { get; private set; }
        public event Action<CaptionUpdate>? Caption { add { } remove { } }
        public event Action<string>? Status { add { } remove { } }
        public event Action<string>? Ended { add { } remove { } }
        public event Action<CaptionFailure>? Failure;
        public void Fail(CaptionFailure failure) => Failure?.Invoke(failure);
        public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
