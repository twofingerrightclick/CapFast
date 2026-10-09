using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using CapFast.Core;

namespace CapFast.Speech;

public sealed class GeminiCaptionSession : ICaptionSession, IAudioActivitySession
{
    private sealed class Connection : IDisposable
    {
        public readonly ClientWebSocket Socket = new();
        public readonly TaskCompletionSource Setup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly GeminiTranscript Transcript = new();
        public readonly GeminiInputTranscript InputTranscript = new();
        public readonly AudioConnectionPolicy Policy = new(Now);
        public readonly CancellationTokenSource Lifetime;
        public Task? Receiver;
        public int PlannedClose;
        public Connection(string apiKey, CancellationToken lifetime)
        {
            Lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            Socket.Options.SetRequestHeader("x-goog-api-key", apiKey);
            Socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            Socket.Options.CollectHttpResponseDetails = true;
        }
        public void Dispose() { Socket.Dispose(); Lifetime.Dispose(); }
    }
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private readonly string _apiKey;
    private readonly CancellationTokenSource _lifetime = new();
    private Connection? _connection;
    private readonly GoogleCaptionMode _mode;
    private readonly FlashTextTranslator? _translator;
    private readonly SourceTranslationPipeline? _pipeline;
    private Process? _microphone;
    private Task? _sendAudio;
    private Task? _monitorAudio;
    private int _started;
    private int _disposed;
    private int _ended;
    private readonly string? _audioDeviceId;
    private CaptionSessionException? _failure;

    public event Action<AudioActivity>? AudioActivityChanged;
    public event Action<CaptionUpdate>? Caption;
    public event Action<string>? Status;
    public event Action<string>? Ended;

    public GeminiCaptionSession(string apiKey, string? audioDeviceId = null, GoogleCaptionMode mode = GoogleCaptionMode.TwoStage)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("A Gemini API key is required.");
        // Same header authentication used by Google's native Live SDK; no key in URLs/logs.
        _apiKey = apiKey.Trim();
        _audioDeviceId = audioDeviceId;
        _mode = mode;
        if (mode == GoogleCaptionMode.TwoStage)
        {
            _translator = new FlashTextTranslator(apiKey);
            _pipeline = new SourceTranslationPipeline(_translator.TranslateAsync, _lifetime.Token, coalesceWindow: TimeSpan.Zero);
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
            await ConnectAsync(timeout.Token);
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
            _connection?.Socket.Abort();
            throw;
        }
        catch (Exception error)
        {
            _lifetime.Cancel();
            _connection?.Socket.Abort();
            // Never expose server responses, HTTP exceptions, or credentials to the UI.
            if (error is CaptionSessionException safe) throw safe;
            if (_failure is not null) throw _failure;
            if (stage == "connection")
            {
                var status = (int)(_connection?.Socket.HttpStatusCode ?? 0);
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
        var connection = new Connection(_apiKey, _lifetime.Token);
        _connection = connection;
        await connection.Socket.ConnectAsync(new Uri("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent"), token);
        connection.Receiver = ReceiveAsync(connection);
        await SendAsync(connection, GoogleLiveProtocol.CreateSetup(_mode), token);
        await connection.Setup.Task.WaitAsync(token);
    }

    private static Task SendAsync(Connection connection, object message, CancellationToken token) => connection.Socket.SendAsync(
        new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(message)), WebSocketMessageType.Text, true, token);

    private async Task CloseIdleAsync(Connection connection)
    {
        Interlocked.Exchange(ref connection.PlannedClose, 1);
        // This is a deliberate local idle close, not retrying a server policy error.
        connection.Lifetime.Cancel();
        connection.Socket.Abort();
        if (connection.Receiver is { } receiver)
            await receiver;
        if (ReferenceEquals(_connection, connection)) _connection = null;
        connection.Dispose();
    }

    private async Task ReceiveAsync(Connection connection)
    {
        try
        {
            var buffer = new byte[16384];
            while (!connection.Lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await connection.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), connection.Lifetime.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (Volatile.Read(ref connection.PlannedClose) != 0) return;
                        var code = result.CloseStatus is { } close ? (int)close : 0;
                        throw new CaptionSessionException(DescribeCloseFailure(code, result.CloseStatusDescription, connection.Setup.Task.IsCompletedSuccessfully));
                    }
                    if (result.MessageType is not (WebSocketMessageType.Text or WebSocketMessageType.Binary) || message.Length + result.Count > 2 * 1024 * 1024)
                        throw new IOException("Invalid service frame.");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                using var json = GoogleLiveProtocol.ParseMessage(message.GetBuffer().AsMemory(0, checked((int)message.Length)), result.MessageType);
                var root = json.RootElement;
                if (Volatile.Read(ref connection.PlannedClose) != 0 || !ReferenceEquals(_connection, connection)) return;
                if (root.TryGetProperty("error", out var serviceError))
                {
                    var code = serviceError.TryGetProperty("code", out var value) && value.TryGetInt32(out var number) ? number : 0;
                    throw new CaptionSessionException(ServiceFailure(code, "service"));
                }
                if (root.TryGetProperty("goAway", out _))
                {
                    Fail("Gemini session is expiring. Start again to reconnect.");
                    return;
                }
                if (root.TryGetProperty("setupComplete", out _)) connection.Setup.TrySetResult();
                // Audio output is deliberately discarded; captions use only output transcription.
                if (_mode == GoogleCaptionMode.TwoStage)
                {
                    foreach (var update in connection.InputTranscript.Apply(root))
                    {
                        if (_lifetime.IsCancellationRequested) continue;
                        connection.Policy.Transcript(update.IsFinal, Now);
                        if (!_pipeline!.TryAcceptTranscript(update, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency))
                            throw new CaptionSessionException("English translation fell behind the audio. Captions paused to avoid a growing delay; restart the session.");
                    }
                }
                else foreach (var update in connection.Transcript.Apply(root))
                    if (!_lifetime.IsCancellationRequested)
                    {
                        connection.Policy.Transcript(update.IsFinal, Now);
                        Caption?.Invoke(update);
                    }
            }
        }
        catch (OperationCanceledException) when (connection.Lifetime.IsCancellationRequested) { }
        catch (CaptionSessionException safe) { if (Volatile.Read(ref connection.PlannedClose) == 0) Fail(safe.Message); }
        catch { if (Volatile.Read(ref connection.PlannedClose) == 0) Fail("Gemini connection or response failed. Check the network and retry. Microphone permission was checked separately."); }
        finally { connection.Setup.TrySetException(_failure ?? new CaptionSessionException("Gemini setup did not complete. Check the selected Google Live model and retry.")); }
    }

    private async Task SendAudioAsync(Process microphone)
    {
        var uploading = false;
        try
        {
            var packet = new byte[3201]; // Local class byte + 100 ms PCM16.
            var gate = new SpeechAudioGate();
            var first = true;
            var activity = AudioActivity.Idle;
            AudioActivityChanged?.Invoke(activity);
            while (!_lifetime.IsCancellationRequested)
            {
                // Quiet packets continue locally, so read timeout diagnoses the input
                // helper without ever uploading silence to Google.
                using var audioTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                audioTimeout.CancelAfter(TimeSpan.FromSeconds(first ? 10 : 5));
                uploading = false;
                var length = await microphone.StandardOutput.BaseStream.ReadAtLeastAsync(packet, packet.Length, false, audioTimeout.Token);
                if (length == 0) break;
                if (length != packet.Length || packet[0] > (byte)AudioFrameKind.Music)
                    throw new CaptionSessionException("Mac speech classification returned an invalid frame. Capture stopped; restart the packaged app.");
                if (first) { Status?.Invoke("Gemini live — listening for speech; silence and music stay on this Mac."); first = false; }
                var decision = gate.Process(packet.AsSpan(1).ToArray(), (AudioFrameKind)packet[0]);
                if (decision.Activity != activity)
                {
                    activity = decision.Activity;
                    AudioActivityChanged?.Invoke(activity);
                }
                var connection = _connection;
                connection?.Policy.ObserveActivity(decision.Activity, Now);
                if (decision.Audio.Count > 0 && connection is null)
                {
                    Status?.Invoke("Speech detected — reconnecting to Gemini…");
                    using var reconnect = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    reconnect.CancelAfter(TimeSpan.FromSeconds(10));
                    try { await ConnectAsync(reconnect.Token); }
                    catch (Exception error) when (!_lifetime.IsCancellationRequested)
                    {
                        throw new CaptionSessionException(error is CaptionSessionException safe ? safe.Message
                            : "Gemini could not reconnect after silence. Check the network and start again.");
                    }
                    connection = _connection!;
                    connection.Policy.ObserveActivity(decision.Activity, Now);
                    Status?.Invoke("Gemini live — translating qualified speech.");
                }
                if (decision.EndStream && connection is not null)
                {
                    using var endTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    endTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    uploading = true;
                    await SendAsync(connection, new { realtimeInput = new { audioStreamEnd = true } }, endTimeout.Token);
                    connection.Policy.Ended(Now);
                }
                foreach (var chunk in decision.Audio)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    uploading = true;
                    connection!.Policy.Uploaded(Now);
                    await SendAsync(connection, new { realtimeInput = new { audio = new
                    {
                        mimeType = "audio/pcm;rate=16000", data = Convert.ToBase64String(chunk)
                    } } }, timeout.Token);
                }
                if (connection is not null)
                {
                    var idle = connection.Policy.Poll(Now);
                    if (idle == AudioConnectionDecision.TranscriptionTimeout)
                        throw new CaptionSessionException("Google did not finish transcribing the accepted speech. Capture paused to avoid losing the unfinished caption; start again.");
                    if (idle == AudioConnectionDecision.CloseIdle)
                    {
                        await CloseIdleAsync(connection);
                        Status?.Invoke("Listening locally — Google reconnects when speech resumes.");
                    }
                }
            }
            if (!_lifetime.IsCancellationRequested)
            {
                // EOF and stderr completion race. Let the monitor report a fixed
                // classifier/device error before replacing it with a generic message.
                if (_monitorAudio is { } monitor)
                    try { await monitor.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
                if (!_lifetime.IsCancellationRequested)
                    Fail("The selected Mac audio input stopped producing audio. Check the input device and restart the session.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (CaptionSessionException safe) { Fail(safe.Message); }
        catch { Fail(uploading
            ? "Gemini audio upload failed or timed out. Check the network and restart the session."
            : "The selected Mac audio input stopped producing audio. Check the input device in Settings and restart the session."); }
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
        _connection?.Socket.Abort();
        try { if (_microphone is { HasExited: false }) _microphone.Kill(); } catch { }
        if (_microphone is not null) Ended?.Invoke(message);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _connection?.Socket.Abort();
        try { if (_microphone is { HasExited: false }) _microphone.Kill(); } catch { }
        if (_pipeline is not null) await _pipeline.DisposeAsync();
        var completion = Task.WhenAll(new[] { _connection?.Receiver, _sendAudio, _monitorAudio }.OfType<Task>());
        try { await completion.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        _microphone?.Dispose();
        _connection?.Dispose();
        _translator?.Dispose();
        // Canceled workers can still finish after the timeout; dispose CTS when all finish.
        _ = completion.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
