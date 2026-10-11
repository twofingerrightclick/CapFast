using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using CapFast.Core;

namespace CapFast.Speech;

public sealed class GeminiCaptionSession : ICaptionSession, IAudioActivitySession, ICaptionDiagnosticsSession
{
    private sealed class Connection : IDisposable
    {
        public readonly ILiveTransport Transport;
        public readonly TaskCompletionSource Setup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly GeminiTranscript Transcript = new();
        public readonly GeminiInputTranscript InputTranscript = new();
        public readonly AudioConnectionPolicy Policy;
        public readonly CancellationTokenSource Lifetime;
        public readonly double OpenedAt;
        public Task? Receiver;
        public int PlannedClose;
        public int RolloverRequested;
        public int TransportFailed;
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
    private readonly GoogleCaptionMode _mode;
    private readonly FlashTextTranslator? _translator;
    private readonly FlashTextTranslator? _frenchTranslator;
    private readonly SourceTranslationPipeline? _pipeline;
    private Process? _microphone;
    private Task? _sendAudio;
    private Task? _readAudio;
    private Task? _monitorAudio;
    private int _started;
    private int _disposed;
    private int _ended;
    private readonly string? _audioDeviceId;
    private readonly bool _rejectMusic;
    private CaptionSessionException? _failure;

    public event Action<string>? Diagnostic;
    public event Action<AudioActivity>? AudioActivityChanged;
    public event Action<CaptionUpdate>? Caption;
    public event Action<string>? Status;
    public event Action<string>? Ended;

    public GeminiCaptionSession(string apiKey, string? audioDeviceId = null, GoogleCaptionMode mode = GoogleCaptionMode.TwoStage,
        bool frenchEnabled = false, bool rejectMusic = false)
        : this(apiKey, audioDeviceId, mode, () => new WebSocketLiveTransport(apiKey.Trim()), new SystemLiveClock(), frenchEnabled, rejectMusic) { }

    internal GeminiCaptionSession(string apiKey, string? audioDeviceId, GoogleCaptionMode mode,
        Func<ILiveTransport> transportFactory, ILiveClock clock, bool frenchEnabled = false, bool rejectMusic = false)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("A Gemini API key is required.");
        // Same header authentication used by Google's native Live SDK; no key in URLs/logs.
        _transportFactory = transportFactory;
        _clock = clock;
        _audioDeviceId = audioDeviceId;
        _rejectMusic = rejectMusic;
        _mode = frenchEnabled ? GoogleCaptionMode.TwoStage : mode;
        if (_mode == GoogleCaptionMode.TwoStage)
        {
            _translator = new FlashTextTranslator(apiKey);
            if (frenchEnabled) _frenchTranslator = new FlashTextTranslator(apiKey, targetLanguage: "French");
            _pipeline = new SourceTranslationPipeline(_translator.TranslateAsync, _lifetime.Token, coalesceWindow: TimeSpan.Zero,
                translateFrench: _frenchTranslator is null ? null : _frenchTranslator.TranslateAsync);
            _pipeline.Caption += update => { if (!_lifetime.IsCancellationRequested) Caption?.Invoke(update); };
            _pipeline.Failed += Fail;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cancellation = cancellationToken.Register(() => _lifetime.Cancel());
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Session already started.");
        if (!OperatingSystem.IsMacOS()) throw new CaptionSessionException("Gemini audio capture currently supports macOS only.");
        var helper = Path.Combine(AppContext.BaseDirectory, "CapFast.AudioCapture");
        if (!File.Exists(helper)) throw new CaptionSessionException("Mac audio helper is missing. Launch the packaged CapFast.app.");
        Status?.Invoke("Checking Mac microphone permission…");
        await RequestMicrophonePermissionAsync(_lifetime.Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var stage = "connection";
        try
        {
            Status?.Invoke("Microphone permission granted. Connecting to Gemini…");
            await EnsureConnectedAsync(token: timeout.Token);
            stage = "setup";
            timeout.Token.ThrowIfCancellationRequested();
            // Capture begins only after the service accepts setup; permission was checked first.
            stage = "capture";
            var captureInfo = new ProcessStartInfo(helper)
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            captureInfo.ArgumentList.Add("--activity");
            if (!string.IsNullOrWhiteSpace(_audioDeviceId))
            {
                captureInfo.ArgumentList.Add("--device");
                captureInfo.ArgumentList.Add(_audioDeviceId);
            }
            _microphone = Process.Start(captureInfo) ?? throw new CaptionSessionException("Gemini connected, but the Mac audio helper could not start.");
            Status?.Invoke("Gemini connected — starting the selected Mac audio input.");
            _monitorAudio = MonitorAudioAsync(_microphone);
            _sendAudio = SendAudioAsync(_microphone);
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
                ? "Gemini connected, but session setup failed or timed out. Check the selected Google Live model and configuration. Microphone permission is already granted."
                : "Gemini setup succeeded, but the Mac audio helper could not start. Check the selected input and relaunch the packaged app.");
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
        403 or 7 => $"Gemini denied access ({stage} {code}). Check API key restrictions, project billing, and Live Translate model access.",
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
            return $"Gemini denied access ({details}). Check API key restrictions and access to the Live Translate model.";
        if (Has("model") && Has("not found", "not supported", "not available", "does not exist"))
            return $"Gemini reported that the selected Google Live model is unavailable or unsupported ({details}). Verify access to the selected mode's model.";
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
        var connection = new Connection(_transportFactory(), _lifetime.Token, Now);
        _connection = connection;
        await connection.Transport.ConnectAsync(token);
        connection.Receiver = ReceiveAsync(connection);
        await SendAsync(connection, GoogleLiveProtocol.CreateSetup(_mode), token);
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
                var updates = _mode == GoogleCaptionMode.TwoStage ? connection.InputTranscript.Apply(root) : connection.Transcript.Apply(root);
                foreach (var update in updates)
                {
                    if (_lifetime.IsCancellationRequested) continue;
                    connection.Policy.Transcript(update.IsFinal, Now);
                    if (_pipeline is not null)
                    {
                        if (!_pipeline.TryAcceptTranscript(update, Now))
                            throw new CaptionSessionException("Translation fell behind the audio. Captions paused to avoid a growing delay; restart the session.");
                    }
                    else Caption?.Invoke(update);
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
        catch (CaptionSessionException safe) { if (Volatile.Read(ref connection.PlannedClose) == 0) Fail(safe.Message); }
        catch { if (Volatile.Read(ref connection.PlannedClose) == 0) Fail("Gemini returned an invalid response. Capture paused; check service availability and start again."); }
        finally { connection.Setup.TrySetException(_failure ?? new CaptionSessionException("Gemini setup did not complete. Check the selected Google Live model and retry.")); }
    }

    private async Task SendAudioAsync(Process microphone)
    {
        var frames = Channel.CreateBounded<ClassifiedAudioFrame>(new BoundedChannelOptions(200)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        _readAudio = ReadAudioAsync(microphone, frames.Writer);
        try { await PumpAudioAsync(frames.Reader); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (CaptionSessionException safe) { Fail(safe.Message); }
        catch { Fail("Google audio streaming failed. Check the network and restart the session."); }
    }

    private async Task ReadAudioAsync(Process microphone, ChannelWriter<ClassifiedAudioFrame> writer)
    {
        try
        {
            var packet = new byte[3201];
            var first = true;
            while (!_lifetime.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(first ? 10 : 5));
                var length = await microphone.StandardOutput.BaseStream.ReadAtLeastAsync(packet, packet.Length, false, timeout.Token);
                if (length == 0) break;
                if (length != packet.Length || packet[0] > (byte)AudioFrameKind.Music)
                    throw new CaptionSessionException("Mac speech classification returned an invalid frame. Capture stopped; restart the packaged app.");
                first = false;
                if (!writer.TryWrite(new(packet.AsSpan(1).ToArray(), (AudioFrameKind)packet[0])))
                    throw new CaptionSessionException("Google connection recovery could not keep up with the audio. Capture paused before the bounded local queue overflowed.");
            }
            if (!_lifetime.IsCancellationRequested)
            {
                if (_monitorAudio is { } monitor) try { await monitor.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
                if (!_lifetime.IsCancellationRequested)
                    throw new CaptionSessionException("The selected Mac audio input stopped producing audio. Check the input device and restart the session.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (CaptionSessionException safe) { Fail(safe.Message); }
        catch { Fail("The selected Mac audio input stopped producing audio. Check the input device and restart the session."); }
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
        var gate = new SpeechAudioGate(_rejectMusic);
        var activity = AudioActivity.Idle;
        var recovering = false;
        AudioActivityChanged?.Invoke(activity);
        await foreach (var frame in frames.ReadAllAsync(_lifetime.Token))
        {
            var decision = gate.Process(frame.Pcm, frame.Kind);
            if (decision.Activity != activity) { activity = decision.Activity; AudioActivityChanged?.Invoke(activity); }
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
            var classifierError = false;
            while (await microphone.StandardError.ReadLineAsync(_lifetime.Token) is { } line)
            {
                permissionDenied |= line == "MIC_PERMISSION_DENIED";
                deviceUnavailable |= line == "MIC_DEVICE_UNAVAILABLE";
                formatError |= line is "MIC_FORMAT_ERROR" or "MIC_CONVERSION_ERROR";
                startError |= line == "MIC_START_ERROR";
                backpressure |= line == "MIC_BACKPRESSURE";
                classifierError |= line == "MIC_CLASSIFIER_ERROR";
            }
            await microphone.WaitForExitAsync(_lifetime.Token);
            if (!_lifetime.IsCancellationRequested) Fail(permissionDenied
                ? "Microphone permission denied. Enable CapFast in System Settings → Privacy & Security → Microphone, then relaunch."
                : deviceUnavailable ? "The selected audio input is unavailable. Choose another input in Settings and start again."
                : formatError ? "The selected audio input has an unsupported audio format. Choose another input in Settings and start again."
                : startError ? "Mac audio capture could not start. Check the selected input and close other apps holding the device."
                : classifierError ? "Mac speech and music classification is unavailable. Capture stopped without uploading unclassified audio. Relaunch the packaged app."
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
        if (_microphone is not null) Ended?.Invoke(message);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _connection?.Transport.Abort();
        try { if (_microphone is { HasExited: false }) _microphone.Kill(); } catch { }
        if (_pipeline is not null) await _pipeline.DisposeAsync();
        var completion = Task.WhenAll(new[] { _connection?.Receiver, _sendAudio, _readAudio, _monitorAudio }.OfType<Task>());
        try { await completion.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        _microphone?.Dispose();
        _connection?.Dispose();
        _translator?.Dispose();
        _frenchTranslator?.Dispose();
        // Canceled workers can still finish after the timeout; dispose CTS when all finish.
        _ = completion.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
