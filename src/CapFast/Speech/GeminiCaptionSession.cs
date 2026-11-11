using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using CapFast.Core;

namespace CapFast.Speech;

public sealed class GeminiCaptionSession : ICaptionSession, ICaptionFailureSession, IAudioActivitySession, ICaptionDiagnosticsSession, ICaptionRecordingSession, ILanguageAssignmentSession
{
    private sealed record ReceivedSource(CaptionUpdate Update, SourceTranslationPipeline.ReceivedBlock Block, double ReceivedAt, string? Hint);
    private sealed class Connection : IDisposable
    {
        public readonly ILiveTransport Transport;
        public readonly TaskCompletionSource Setup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly GeminiInputTranscript InputTranscript = new();
        public readonly TranscriptLanguageResolver Languages = new();
        public readonly AudioConnectionPolicy Policy;
        public readonly CancellationTokenSource Lifetime;
        public readonly double OpenedAt;
        public long AudioGeneration;
        public readonly Channel<ReceivedSource> Sources = Channel.CreateBounded<ReceivedSource>(32);
        public Task? SourceWorker;
        public Task? Receiver;
        public int PlannedClose;
        public int RolloverRequested;
        public int TransportFailed;
        public double LanguageCheckRetryAt;
        public bool UploadedAny;
        public bool StreamEnded;
        public Connection(ILiveTransport transport, CancellationToken lifetime, double now)
        {
            Transport = transport;
            Lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            Policy = new(now);
            OpenedAt = now;
        }
        public void Dispose() { Transport.Dispose(); Lifetime.Dispose(); }
    }
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<ILiveTransport> _transportFactory;
    private readonly ILiveClock _clock;
    private readonly LiveRetryBudget _retries = new();
    private double Now => _clock.Now;
    private Connection? _connection;
    private readonly FlashTextTranslator? _languageDetector;
    private readonly FlashMultilingualTranslator? _multilingualTranslator;
    private readonly bool _useCombinedTranslation;
    private readonly Func<string, CancellationToken, Task<string?>>? _detectionOverride;
    private readonly List<Task> _sourceWorkers = new();
    private BlockTextTranslator? _blockTranslator;
    private readonly SourceTranslationPipeline? _pipeline;
    private Process? _microphone;
    private SileroSpeechDetector? _speechDetector;
    private bool _captureActive;
    private Task? _sendAudio;
    private Task? _readAudio;
    private Task? _monitorAudio;
    private CaptionAudioRecorder? _recording;
    private int _started;
    private int _disposed;
    private int _ended;
    private readonly string? _audioDeviceId;
    private readonly double _speechQualificationSeconds;
    private readonly double _silenceResetSeconds;
    private CaptionSessionException? _failure;

    public LanguageAssignment? LanguageAssignment => _pipeline?.Assignment;
    public event Action<LanguageAssignment?>? LanguageAssignmentChanged;
    public bool CanPresentCaption(CaptionUpdate update) => _pipeline?.CanPresentCaption(update) == true;
    public void ResetLanguageDetection() => _pipeline?.ResetForNextUtterance();
    public void HintInputLanguage(string code) => _pipeline?.HintInputLanguage(code);
    public event Action<string>? Diagnostic;
    public event Action<string>? RecordingDiagnostic;
    internal event Action? FinalSourceRegistered;
    public event Action<AudioActivity>? AudioActivityChanged;
    public event Action<CaptionUpdate>? Caption;
    public event Action<string>? Status;
    public event Action<string>? Ended;
    public event Action<CaptionFailure>? Failure;

    public GeminiCaptionSession(string apiKey, string? audioDeviceId = null,
        double speechQualificationSeconds = SpeechAudioGate.DefaultSpeechQualificationSeconds,
        double silenceResetSeconds = SpeechAudioGate.DefaultSilenceResetSeconds)
        : this(apiKey, audioDeviceId, () => new WebSocketLiveTransport(apiKey.Trim()), new SystemLiveClock(),
            speechQualificationSeconds: speechQualificationSeconds, silenceResetSeconds: silenceResetSeconds) { }

    internal GeminiCaptionSession(string apiKey, string? audioDeviceId,
        Func<ILiveTransport> transportFactory, ILiveClock clock,
        Func<string, CancellationToken, Task<string?>>? detectLanguage = null,
        Func<string, LanguageAssignment, CancellationToken, Task<(string Primary, string Secondary)>>? drainTranslate = null,
        Func<string, string, string?, CancellationToken, Task<MultilingualCaptionResult>>? combinedTranslate = null,
        double speechQualificationSeconds = SpeechAudioGate.DefaultSpeechQualificationSeconds,
        double silenceResetSeconds = SpeechAudioGate.DefaultSilenceResetSeconds)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("A Gemini API key is required.");
        // Same header authentication used by Google's native Live SDK; no key in URLs/logs.
        _transportFactory = transportFactory;
        _clock = clock; _detectionOverride = detectLanguage;
        _audioDeviceId = audioDeviceId;
        _speechQualificationSeconds = SpeechAudioGate.NormalizeSpeechQualification(speechQualificationSeconds);
        _silenceResetSeconds = SpeechAudioGate.NormalizeSilenceReset(silenceResetSeconds);
        _useCombinedTranslation = combinedTranslate is not null || (detectLanguage is null && drainTranslate is null);
        if (_useCombinedTranslation)
        {
            if (combinedTranslate is null)
            {
                _multilingualTranslator = new(apiKey);
                combinedTranslate = _multilingualTranslator.TranslateAsync;
            }
        }
        else
        {
            _languageDetector = new FlashTextTranslator(apiKey, targetLanguage: "language code", languageDetection: true);
            _blockTranslator = new(apiKey);
        }
        Func<string, LanguageAssignment, CancellationToken, Task<(string Primary, string Secondary)>>? legacyTranslate = drainTranslate;
        if (legacyTranslate is null && _blockTranslator is not null) legacyTranslate = _blockTranslator.TranslateAsync;
        legacyTranslate ??= (string text, LanguageAssignment assignment, CancellationToken token) =>
            throw new InvalidOperationException("Combined translation must handle this fragment.");
        _pipeline = new SourceTranslationPipeline(legacyTranslate, _lifetime.Token,
            drainTranslate: legacyTranslate, capacity: _useCombinedTranslation ? 32 : 8,
            translateMultilingual: combinedTranslate);
        _pipeline.AssignmentChanged += assignment => LanguageAssignmentChanged?.Invoke(assignment);
        _pipeline.Caption += update => { if (!_lifetime.IsCancellationRequested) Caption?.Invoke(update); };
        _pipeline.Failed += Fail;
        _pipeline.Diagnostic += notice => Diagnostic?.Invoke(notice);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cancellation = cancellationToken.Register(() => _lifetime.Cancel());
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Session already started.");
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
            throw new CaptionSessionException("Audio capture supports macOS and Windows only.");
        var helper = Path.Combine(AppContext.BaseDirectory, "CapFast.AudioCapture");
        if (OperatingSystem.IsMacOS())
        {
            if (!File.Exists(helper)) throw new CaptionSessionException("Mac audio helper is missing. Launch the packaged CapFast.app.");
            Status?.Invoke("Checking Mac microphone permission…");
            await RequestMicrophonePermissionAsync(_lifetime.Token);
        }
        Status?.Invoke("Loading local speech detection…");
        try { _speechDetector = await Task.Run(() => new SileroSpeechDetector(), _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
        catch { throw new CaptionSessionException("Local speech classification is unavailable. Reinstall the packaged app with its model and runtime dependencies."); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var stage = "connection";
        try
        {
            Status?.Invoke("Connecting to Gemini…");
            await EnsureConnectedAsync(token: timeout.Token);
            stage = "setup";
            timeout.Token.ThrowIfCancellationRequested();
            // Capture begins only after the service accepts setup. macOS permission
            // was checked first; WASAPI checks Windows access when capture starts.
            stage = "capture";
            _captureActive = true;
            if (OperatingSystem.IsMacOS())
            {
                var captureInfo = new ProcessStartInfo(helper)
                {
                    UseShellExecute = false, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true
                };
                if (!string.IsNullOrWhiteSpace(_audioDeviceId))
                {
                    captureInfo.ArgumentList.Add("--device");
                    captureInfo.ArgumentList.Add(_audioDeviceId);
                }
                _microphone = Process.Start(captureInfo) ?? throw new CaptionSessionException("Mac audio capture could not start. Relaunch the packaged app.");
                _monitorAudio = MonitorAudioAsync(_microphone);
            }
            Status?.Invoke("Gemini connected — starting the selected audio input.");
            _recording = new CaptionAudioRecorder(message => RecordingDiagnostic?.Invoke(message));
            _sendAudio = Task.Run(() => SendAudioAsync(_microphone));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _lifetime.Cancel();
            _connection?.Transport.Abort();
            throw;
        }
        catch (Exception error)
        {
            _lifetime.Cancel();
            _connection?.Transport.Abort();
            // Never expose server responses, HTTP exceptions, or credentials to the UI.
            if (error is CaptionSessionException safe) throw safe;
            if (_failure is not null) throw _failure;
            if (stage == "connection")
            {
                var status = (int)(_connection?.Transport.HttpStatus ?? 0);
                throw new CaptionSessionException(status > 0 ? ServiceFailure(status, "connection HTTP")
                    : "Gemini connection failed before setup. Check internet access, DNS, firewall, and the Gemini endpoint.");
            }
            throw new CaptionSessionException(stage == "setup"
                ? "Gemini connected, but the startup deadline expired before audio capture began. Check the connection and service availability."
                : "Audio capture could not start. Check the selected input, microphone privacy settings, and packaged app dependencies.");
        }
    }

    public static async Task RequestMicrophonePermissionAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS()) throw new CaptionSessionException("Microphone permission preflight currently supports macOS only.");
        var helper = Path.Combine(AppContext.BaseDirectory, "CapFast.AudioCapture");
        if (!File.Exists(helper)) throw new CaptionSessionException("Mac audio helper is missing. Launch the packaged CapFast.app.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(65));
        var info = new ProcessStartInfo(helper)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        info.ArgumentList.Add("--request-permission");
        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new IOException();
            var output = process.StandardOutput.ReadLineAsync(timeout.Token).AsTask();
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var code = await output;
            var error = await errors;
            if (process.ExitCode == 0 && code == "MIC_PERMISSION_GRANTED") return;
            throw new CaptionSessionException(error.Split('\n').Contains("MIC_PERMISSION_DENIED")
                ? "Microphone permission denied. Enable CapFast in System Settings → Privacy & Security → Microphone, then relaunch."
                : error.Split('\n').Contains("MIC_PERMISSION_TIMEOUT")
                    ? "Microphone permission request timed out. Respond to the Mac permission dialog, then start again."
                    : "Mac microphone permission could not be checked. Relaunch the packaged CapFast.app and check Privacy & Security → Microphone.");
        }
        catch (CaptionSessionException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new CaptionSessionException("Microphone permission request timed out. Respond to the Mac permission dialog, then start again."); }
        catch { throw new CaptionSessionException("The Mac microphone permission helper could not run. Relaunch the packaged CapFast.app."); }
        finally { try { if (!process.HasExited) process.Kill(); } catch { } }
    }

    private static string ServiceFailure(int code, string stage) => code switch
    {
        401 or 16 => $"Gemini rejected authentication ({stage} {code}). Check the Google API key in Settings.",
        403 or 7 => $"Gemini denied access ({stage} {code}). Check API key restrictions, project billing, and Transcribe Live model access.",
        404 or 5 => $"Gemini model or endpoint is unavailable ({stage} {code}). Verify access to the selected Google Live model.",
        429 or 8 => $"Gemini quota is exhausted ({stage} {code}). Check the project's quota and billing, then retry.",
        400 or 3 => $"Gemini rejected session configuration ({stage} {code}). The selected Google Live model or setup may be unsupported.",
        500 or 503 or 13 or 14 => $"Gemini service is unavailable ({stage} {code}). Retry when the service or connection recovers.",
        _ => $"Gemini rejected the session ({stage} {code}). Check model access and service availability."
    };

    // Classify a few known server phrases into locally authored messages. Never return,
    // retain in diagnostics, or interpolate the close reason (it can contain credentials).
    public static string DescribeCloseFailure(int code, string? reason, bool setupComplete)
    {
        var stage = setupComplete ? "after setup" : "before setup was accepted";
        var details = $"WebSocket {code}, {stage}";
        bool Has(params string[] phrases) => reason is not null &&
            phrases.Any(phrase => reason.Contains(phrase, StringComparison.OrdinalIgnoreCase));

        if (Has("api key not valid", "invalid api key", "api_key_invalid", "api key expired", "unauthenticated"))
            return $"Gemini rejected authentication ({details}). Check the Google API key in Settings.";
        if (Has("quota", "resource_exhausted", "rate limit", "billing", "payment"))
            return $"Gemini reported a quota or billing problem ({details}). Check the Google project's quota and billing.";
        if (Has("permission denied", "permission_denied", "access denied", "api key restriction", "unregistered caller"))
            return $"Gemini denied access ({details}). Check API key restrictions and access to the Transcribe Live model.";
        if (Has("model") && Has("not found", "not supported", "not available", "does not exist"))
            return $"Gemini reported that the selected Google Live model is unavailable or unsupported ({details}). Verify access to Transcribe Live.";
        if (Has("unknown name", "unknown field", "invalid argument", "invalid_argument", "invalid json", "malformed", "unsupported setup", "cannot find field"))
            return $"Gemini rejected the session setup ({details}). The model or setup fields may be unsupported; microphone permission is a separate check.";
        if (Has("internal error", "internal server", "unavailable", "service error"))
            return $"Gemini reported a service error ({details}). Retry later; this does not identify a microphone problem.";
        if (code == 1008)
            return $"Gemini closed the session with a policy error ({details}). Check API key restrictions and model access.";
        if (code == 1011)
            return $"Gemini closed the session with an internal server error ({details}). The close code alone does not identify the cause. Check model availability and project access, then retry.";
        return $"Gemini closed the session ({details}). Start again to reconnect; check model access if setup was not accepted.";
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        var connection = new Connection(_transportFactory(), _lifetime.Token, Now) { AudioGeneration = _pipeline?.AudioGeneration ?? 0 };
        _connection = connection;
        await connection.Transport.ConnectAsync(token);
        connection.SourceWorker = ProcessSourcesAsync(connection);
        lock (_sourceWorkers) _sourceWorkers.Add(connection.SourceWorker);
        _ = connection.SourceWorker.ContinueWith(task => { lock (_sourceWorkers) _sourceWorkers.Remove(task); }, TaskScheduler.Default);
        connection.Receiver = ReceiveAsync(connection);
        await SendAsync(connection, GoogleLiveProtocol.CreateSetup(), token);
        await connection.Setup.Task.WaitAsync(token);
    }

    private static Task SendAsync(Connection connection, object message, CancellationToken token) =>
        connection.Transport.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), token);

    private async Task CloseConnectionAsync(Connection connection)
    {
        Interlocked.Exchange(ref connection.PlannedClose, 1);
        connection.Lifetime.Cancel();
        connection.Transport.Abort();
        if (connection.Receiver is { } receiver) await receiver;
        if (ReferenceEquals(_connection, connection)) _connection = null;
        connection.Dispose();
    }

    private static bool TerminalCloseReason(string? reason)
    {
        if (reason is null) return false;
        return new[] { "api key", "unauth", "quota", "resource_exhausted", "rate limit", "billing", "payment",
            "permission", "access denied", "unregistered caller", "invalid", "unknown field", "unknown name", "malformed", "model", "configuration" }
            .Any(word => reason.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
    private static bool RetryableClose(int code, string? reason)
    {
        if (TerminalCloseReason(reason)) return false;
        if (code == 1006) return true;
        return code == 1011 && reason is not null &&
            new[] { "internal error", "internal server", "temporarily unavailable", "service unavailable" }
                .Any(word => reason.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private async Task EnsureConnectedAsync(bool recovering = false, CancellationToken token = default)
    {
        if (token == default) token = _lifetime.Token;
        while (!token.IsCancellationRequested)
        {
            if (recovering)
            {
                var delay = _retries.NextDelay();
                Status?.Invoke("Google connection interrupted — reconnecting…");
                await _clock.Delay(delay, token);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try { await ConnectAsync(timeout.Token); return; }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                var connection = _connection;
                var status = connection?.Transport.HttpStatus ?? 0;
                var retryable = error is TransientLiveException ||
                    error is WebSocketException && (status == 0 || status is 500 or 502 or 503 or 504) ||
                    error is OperationCanceledException && timeout.IsCancellationRequested;
                if (connection is not null) await CloseConnectionAsync(connection);
                if (!retryable)
                    throw error is CaptionSessionException safe ? safe : new CaptionSessionException(status >= 400
                        ? ServiceFailure(status, "connection HTTP") : "Google connection or setup failed. Check the model, credentials, and network.");
                recovering = true;
            }
        }
        token.ThrowIfCancellationRequested();
    }

    private async Task RotateAsync(Connection connection, bool transportFailure)
    {
        if (!transportFailure && connection.UploadedAny && !connection.StreamEnded)
        {
            try { await EndAudioAsync(connection); }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException && !_lifetime.IsCancellationRequested)
            { transportFailure = true; }
        }
        if (!transportFailure && connection.UploadedAny)
        {
            var deadline = Now + 3;
            while (!connection.Policy.Drained && Now < deadline)
                await _clock.Delay(TimeSpan.FromMilliseconds(100), _lifetime.Token);
        }
        if (connection.UploadedAny && (!connection.Policy.Drained || transportFailure))
        {
            const string notice = "Google connection refreshed; unfinished transcription may be incomplete.";
            Status?.Invoke(notice);
            Diagnostic?.Invoke(notice);
        }
        await CloseConnectionAsync(connection);
    }

    private async Task EndAudioAsync(Connection connection)
    {
        if (!connection.UploadedAny || connection.StreamEnded) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await SendAsync(connection, new { realtimeInput = new { audioStreamEnd = true } }, timeout.Token);
        connection.StreamEnded = true;
        connection.Policy.Ended(Now);
    }

    private async Task ReceiveAsync(Connection connection)
    {
        try
        {
            while (!connection.Lifetime.IsCancellationRequested)
            {
                var message = await connection.Transport.ReceiveAsync(connection.Lifetime.Token);
                if (message.Type == WebSocketMessageType.Close)
                {
                    if (Volatile.Read(ref connection.PlannedClose) != 0) return;
                    var code = message.CloseCode ?? 0;
                    if (RetryableClose(code, message.CloseReason) ||
                        Volatile.Read(ref connection.RolloverRequested) != 0 && !TerminalCloseReason(message.CloseReason) && code is 1000 or 1001 or 1012)
                        throw new TransientLiveException();
                    throw new CaptionSessionException(DescribeCloseFailure(code, message.CloseReason, connection.Setup.Task.IsCompletedSuccessfully));
                }
                using var json = GoogleLiveProtocol.ParseMessage(message.Data, message.Type);
                var root = json.RootElement;
                if (Volatile.Read(ref connection.PlannedClose) != 0 || !ReferenceEquals(_connection, connection)) return;
                if (root.TryGetProperty("error", out var serviceError))
                {
                    var code = serviceError.TryGetProperty("code", out var value) && value.TryGetInt32(out var number) ? number : 0;
                    if (code is 500 or 503 or 13 or 14) throw new TransientLiveException();
                    throw new CaptionSessionException(ServiceFailure(code, "service"));
                }
                if (root.TryGetProperty("goAway", out _)) Interlocked.Exchange(ref connection.RolloverRequested, 1);
                if (root.TryGetProperty("setupComplete", out _)) connection.Setup.TrySetResult();
                var updates = connection.InputTranscript.Apply(root);
                foreach (var received in updates)
                {
                    if (_lifetime.IsCancellationRequested) continue;
                    connection.Policy.Transcript(received.IsFinal, Now);
                    if (_pipeline is not null)
                    {
                        if (connection.AudioGeneration != _pipeline.AudioGeneration) continue;
                        var block = _pipeline.CaptureReceivedBlock(connection.AudioGeneration);
                        if (block.ManualRevision < 0) continue;
                        if (!connection.Sources.Writer.TryWrite(new(received, block, Now, _pipeline.CaptureHint(received.UtteranceId))))
                            throw new CaptionSessionException("Language identification fell behind the audio. Restart the session to continue.");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (connection.Lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is TransientLiveException or WebSocketException)
        {
            if (Volatile.Read(ref connection.PlannedClose) == 0)
            {
                Interlocked.Exchange(ref connection.TransportFailed, 1);
                connection.Setup.TrySetException(new TransientLiveException());
                connection.Transport.Abort();
            }
        }
        catch (System.Text.Json.JsonException) { if (Volatile.Read(ref connection.PlannedClose) == 0) Fail("Gemini returned malformed session JSON. Capture paused; check service availability and start again."); }
        catch (CaptionSessionException safe) { if (Volatile.Read(ref connection.PlannedClose) == 0) Fail(safe.Message); }
        catch { if (Volatile.Read(ref connection.PlannedClose) == 0) Fail("Gemini returned an invalid response. Capture paused; check service availability and start again."); }
        finally { connection.Sources.Writer.TryComplete(); connection.Setup.TrySetException(_failure ?? new CaptionSessionException("Gemini setup did not complete. Check the selected Google Live model and retry.")); }
    }

    private async Task ProcessSourcesAsync(Connection connection)
    {
        try
        {
            await foreach (var source in connection.Sources.Reader.ReadAllAsync(_lifetime.Token))
            {
                // Manual reset and hints invalidate queued recognition work. Natural
                // retirement keeps its revision valid so received finals can drain.
                if (!_pipeline!.IsReceivedBlockValid(source.Block)) continue;
                try
                {
                    // Recognition can accumulate two speakers in one utterance.
                    // Route each stable source chunk after segmentation, while
                    // preserving explicit provider codes and operator hints.
                    var trustedLanguage = SpeechLanguageAssignment.Normalize(source.Hint ?? source.Update.SourceLanguage);
                    var resolved = source.Update with { SourceLanguage = trustedLanguage ?? "auto" };
                    Func<string, CancellationToken, Task<string?>>? detectSegment = !_useCombinedTranslation && trustedLanguage is null
                        ? (text, token) => DetectSegmentAsync(connection, source.Block, text, token) : null;
                    if (resolved.IsFinal)
                    {
                        // Acceptance and ordering are registered synchronously by the pipeline.
                        // Translation must not occupy the bounded recognition reader.
                        var translation = _pipeline!.CompleteReceivedFinalAsync(resolved, source.Block, source.ReceivedAt, detectSegment);
                        FinalSourceRegistered?.Invoke();
                        var final = ObserveReceivedFinalAsync(translation, source.Block);
                        lock (_sourceWorkers) _sourceWorkers.Add(final);
                        _ = final.ContinueWith(task => { lock (_sourceWorkers) _sourceWorkers.Remove(task); }, TaskScheduler.Default);
                    }
                    else if (ReferenceEquals(_connection, connection) && _pipeline!.IsReceivedBlockCurrent(source.Block) &&
                        !_pipeline.TryAcceptTranscript(resolved, source.ReceivedAt, connection.AudioGeneration, detectSegment))
                        throw new CaptionSessionException("Translation fell behind the audio. Restart the session to continue.");
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    if (ReferenceEquals(_connection, connection) && Volatile.Read(ref connection.PlannedClose) == 0 && _pipeline!.IsReceivedBlockCurrent(source.Block))
                        Fail(error is CaptionSessionException safe ? safe.Message : "Google language identification failed. Restart the session to continue.");
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Retired recognition work cannot fail the next speech block.
            if (ReferenceEquals(_connection, connection) && Volatile.Read(ref connection.PlannedClose) == 0)
                Fail(error is CaptionSessionException safe ? safe.Message : "Google language identification failed. Restart the session to continue.");
        }
    }

    private async Task<string?> DetectSegmentAsync(Connection connection, SourceTranslationPipeline.ReceivedBlock block,
        string text, CancellationToken token)
    {
        lock (connection) if (Now < connection.LanguageCheckRetryAt)
            return _pipeline!.IsReceivedBlockCurrent(block) ? "auto" : _pipeline.FallbackInput(block) ?? "auto";
        try
        {
            return SpeechLanguageAssignment.Normalize(await (_detectionOverride ?? _languageDetector!.DetectLanguageAsync)(text, token)) ?? "auto";
        }
        catch (CaptionSessionException error) when (!error.IsTerminalService && !error.IsContentBlocked)
        {
            var current = _pipeline!.IsReceivedBlockCurrent(block);
            if (current) lock (connection) connection.LanguageCheckRetryAt = Now + 2;
            Diagnostic?.Invoke("Google language check failed; continuing with " +
                (current ? "automatic source translation." : "the received speech block's source language.") +
                (current ? " Retrying language identification after a short cooldown." : "") + $" {error.Message}");
            return current ? "auto" : _pipeline.FallbackInput(block) ?? "auto";
        }
    }

    private async Task ObserveReceivedFinalAsync(Task translation, SourceTranslationPipeline.ReceivedBlock block)
    {
        try { await translation; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Observe faults from acceptance as well as translation. Async methods
            // capture even failures before their first await in the returned task.
            if (_lifetime.IsCancellationRequested || Volatile.Read(ref _disposed) != 0 || !_pipeline!.IsReceivedBlockValid(block)) return;
            if (_pipeline!.IsReceivedBlockCurrent(block))
                Fail(error is CaptionSessionException safe ? safe.Message : "Google text translation failed. Restart the session to continue.");
            else Diagnostic?.Invoke("Google text translation failed for an earlier speech block; its unfinished caption is unavailable.");
        }
    }

    private async Task SendAudioAsync(Process? microphone)
    {
        var frames = Channel.CreateBounded<ClassifiedAudioFrame>(new BoundedChannelOptions(200)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        _readAudio = ReadAudioAsync(microphone, frames.Writer);
        try { await PumpAudioAsync(frames.Reader); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (CaptionSessionException safe) { Fail(safe.Message); }
        catch { Fail("Google audio streaming failed. Check the network and restart the session."); }
        finally
        {
            // Include capture teardown in this task even if DisposeAsync observed
            // it before the worker assigned _readAudio. The model stays alive
            // until the final inference and WASAPI callback have completed.
            if (_readAudio is { } reader) await reader;
        }
    }

    private async Task ReadAudioAsync(Process? microphone, ChannelWriter<ClassifiedAudioFrame> writer)
    {
        try
        {
            await using var recording = _recording!;
            var framer = new PcmSpeechFramer(samples =>
            {
                try { return _speechDetector!.Predict(samples); }
                catch { throw new CaptionSessionException("Local speech classification is unavailable. Reinstall the packaged app with its model and runtime dependencies."); }
            }, frame =>
            {
                if (!writer.TryWrite(frame))
                    throw new CaptionSessionException("Google connection recovery could not keep up with the audio. Capture paused before the bounded local queue overflowed.");
            });
            void Consume(ReadOnlySpan<byte> pcm)
            {
                recording.Append(pcm);
                framer.Append(pcm);
            }
            if (OperatingSystem.IsWindows())
            {
                await WindowsAudioCapture.RunAsync(_audioDeviceId, pcm => Consume(pcm), _lifetime.Token);
                return;
            }
            if (microphone is null) throw new CaptionSessionException("Mac audio helper is missing.");
            var packet = new byte[3200];
            var first = true;
            while (!_lifetime.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(first ? 10 : 5));
                var length = await microphone.StandardOutput.BaseStream.ReadAsync(packet, timeout.Token);
                if (length == 0) break;
                first = false;
                Consume(packet.AsSpan(0, length));
            }
            if (!_lifetime.IsCancellationRequested)
            {
                if (_monitorAudio is { } monitor) try { await monitor.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
                if (!_lifetime.IsCancellationRequested)
                    throw new CaptionSessionException("The selected audio input stopped producing audio. Check the device and microphone privacy settings, then restart.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (CaptionSessionException safe) { Fail(safe.Message); }
        catch { Fail("The selected audio input stopped producing audio. Check the device and microphone privacy settings, then restart."); }
        finally { writer.TryComplete(); }
    }

    // The same pump is exercised by fake transports and the packaged microphone.
    internal async Task RunClassifiedAudioAsync(ChannelReader<ClassifiedAudioFrame> frames)
    {
        try
        {
            await EnsureConnectedAsync();
            await PumpAudioAsync(frames);
        }
        catch (OperationCanceledException) when (_failure is not null) { throw _failure; }
    }

    private async Task PumpAudioAsync(ChannelReader<ClassifiedAudioFrame> frames)
    {
        var gate = new SpeechAudioGate(_speechQualificationSeconds, _silenceResetSeconds);
        var activity = AudioActivity.Idle;
        var recovering = false;
        var awaitingSpeechBlock = false;
        AudioActivityChanged?.Invoke(activity);
        await foreach (var frame in frames.ReadAllAsync(_lifetime.Token))
        {
            var blockExpired = _pipeline?.ObserveAudio(frame.Kind switch { AudioFrameKind.Speech => AudioActivity.Speech, _ => AudioActivity.Idle }, Now) == true;
            if (blockExpired || (_pipeline is not null && _connection is { } currentStream && currentStream.AudioGeneration != _pipeline.AudioGeneration))
            {
                // A fresh recognition stream makes even a previously unseen late
                // final belong to the retired audio generation, never the next block.
                if (_connection is { } expired) await CloseConnectionAsync(expired);
                gate = new SpeechAudioGate(_speechQualificationSeconds, _silenceResetSeconds);
                awaitingSpeechBlock = true;
                Status?.Invoke("Listening locally — Google reconnects when speech resumes.");
            }
            var observedActivity = frame.Kind switch { AudioFrameKind.Speech => AudioActivity.Speech,
                _ => AudioActivity.Idle };
            if (observedActivity != activity) { activity = observedActivity; AudioActivityChanged?.Invoke(activity); }
            if (awaitingSpeechBlock)
            {
                if (frame.Kind != AudioFrameKind.Speech) continue;
                awaitingSpeechBlock = false;
            }
            var decision = gate.Process(frame.Pcm, frame.Kind);

            var connection = _connection;
            if (connection is not null)
            {
                connection.Policy.ObserveActivity(decision.Activity, Now);
                var failed = Volatile.Read(ref connection.TransportFailed) != 0;
                if (failed || Volatile.Read(ref connection.RolloverRequested) != 0 || Now - connection.OpenedAt >= 480)
                {
                    await RotateAsync(connection, failed);
                    recovering |= failed;
                    connection = null;
                }
            }
            if (decision.Audio.Count > 0 && connection is null)
            {
                Status?.Invoke("Speech detected — connecting to Google…");
                await EnsureConnectedAsync(recovering);
                recovering = false;
                connection = _connection!;
                connection.Policy.ObserveActivity(decision.Activity, Now);
                Status?.Invoke("Google live — translating qualified speech.");
            }
            if (decision.EndStream && connection is not null)
            {
                try { await EndAudioAsync(connection); }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException && !_lifetime.IsCancellationRequested)
                {
                    await RotateAsync(connection, true); recovering = true; connection = null;
                }
            }
            foreach (var chunk in decision.Audio)
            {
                if (connection is null)
                {
                    await EnsureConnectedAsync(recovering); recovering = false; connection = _connection!;
                    connection.Policy.ObserveActivity(decision.Activity, Now);
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                connection.Policy.Uploaded(Now);
                connection.UploadedAny = true;
                connection.StreamEnded = false;
                try
                {
                    await SendAsync(connection, new { realtimeInput = new { audio = new
                    { mimeType = "audio/pcm;rate=16000", data = Convert.ToBase64String(chunk) } } }, timeout.Token);
                    _retries.AudioSucceeded();
                }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException && !_lifetime.IsCancellationRequested)
                {
                    // The provider may already have received this frame. Replaying it
                    // blindly would duplicate speech; preserve all later queued frames.
                    await RotateAsync(connection, true); recovering = true; connection = null;
                }
            }
            if (connection is not null)
            {
                var idle = connection.Policy.Poll(Now);
                if (idle == AudioConnectionDecision.TranscriptionTimeout)
                    throw new CaptionSessionException("Google did not finish transcribing the accepted speech. Capture paused to avoid losing the unfinished caption; start again.");
                if (idle == AudioConnectionDecision.CloseIdle)
                {
                    await CloseConnectionAsync(connection);
                    _retries.Reset();
                    Status?.Invoke("Listening locally — Google reconnects when speech resumes.");
                }
            }
        }
    }

    private async Task MonitorAudioAsync(Process microphone)
    {
        try
        {
            // Helper emits fixed status codes only. Never forward arbitrary process output.
            var permissionDenied = false;
            var deviceUnavailable = false;
            var formatError = false;
            var startError = false;
            var backpressure = false;
            while (await microphone.StandardError.ReadLineAsync(_lifetime.Token) is { } line)
            {
                permissionDenied |= line == "MIC_PERMISSION_DENIED";
                deviceUnavailable |= line == "MIC_DEVICE_UNAVAILABLE";
                formatError |= line is "MIC_FORMAT_ERROR" or "MIC_CONVERSION_ERROR";
                startError |= line == "MIC_START_ERROR";
                backpressure |= line == "MIC_BACKPRESSURE";
            }
            await microphone.WaitForExitAsync(_lifetime.Token);
            if (!_lifetime.IsCancellationRequested) Fail(permissionDenied
                ? "Microphone permission denied. Enable CapFast in System Settings → Privacy & Security → Microphone, then relaunch."
                : deviceUnavailable ? "The selected audio input is unavailable. Choose another input in Settings and start again."
                : formatError ? "The selected audio input has an unsupported audio format. Choose another input in Settings and start again."
                : startError ? "Mac audio capture could not start. Check the selected input and close other apps holding the device."
                : backpressure ? "Mac audio capture stopped because the upload could not keep up. Check the network and restart."
                : "Mac audio capture stopped. Check the selected audio input and start again.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { Fail("Mac audio capture stopped. Start again to reconnect."); }
    }

    private void Fail(string message)
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _ended, 1) != 0) return;
        _failure = new CaptionSessionException(message);
        _connection?.Setup.TrySetException(_failure);
        _lifetime.Cancel();
        _connection?.Transport.Abort();
        try { if (_microphone is { HasExited: false }) _microphone.Kill(); } catch { }
        if (_captureActive)
        {
            Failure?.Invoke(new(message, CaptionFailurePolicy.RequiresOperatorAction(message)));
            Ended?.Invoke(message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _connection?.Transport.Abort();
        try { if (_microphone is { HasExited: false }) _microphone.Kill(); } catch { }
        if (_pipeline is not null) await _pipeline.DisposeAsync();
        Task[] sources;
        lock (_sourceWorkers) sources = _sourceWorkers.ToArray();
        var completion = Task.WhenAll(new[] { _connection?.Receiver, _sendAudio, _readAudio, _monitorAudio }.OfType<Task>().Concat(sources));
        try { await completion.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        // Paused means the latest file is finalized and safe to copy. Caption
        // transport teardown stays bounded; disk finalization remains awaited.
        if (_recording is { } recording) await recording.DisposeAsync();
        _microphone?.Dispose();
        _connection?.Dispose();
        _blockTranslator?.Dispose();
        _languageDetector?.Dispose();
        _multilingualTranslator?.Dispose();
        // Canceled workers can still finish after the timeout; dispose CTS when all finish.
        _ = completion.ContinueWith(_ => { _speechDetector?.Dispose(); _lifetime.Dispose(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
