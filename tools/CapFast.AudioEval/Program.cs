using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapFast.Core;
using CapFast.Speech;

// Audio is replayed directly into the same Live API, at real-time speed.
// References are saved locally for comparison and never enter service requests.
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var root = Directory.GetCurrentDirectory();
            var limit = 12;
            var run = args.Contains("--run");
            var normalize = false;
            string? clipId = null;
            var manifestPath = Path.Combine(root, "swahili-pack", "manifest.json");
            var output = Path.Combine(root, "artifacts", "swahili-audio", DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ"));
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--limit") limit = int.Parse(args[++i]);
                else if (args[i] == "--output") output = Path.GetFullPath(args[++i]);
                else if (args[i] == "--normalize-audio") normalize = true;
                else if (args[i] == "--clip-id") clipId = args[++i];
                else if (args[i] == "--manifest") manifestPath = Path.GetFullPath(args[++i]);
                else if (args[i] != "--run") throw new SafeError("Use --run, --limit 1..12, and --output NEW_DIRECTORY.");
            }
            if (limit is < 1 or > 12) throw new SafeError("Limit must be 1..12.");
            if (Directory.Exists(output)) throw new SafeError("Choose a new output directory.");
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var clips = manifest.RootElement.GetProperty("clips").EnumerateArray()
                .Where(clip => clipId is null || clip.GetProperty("id").ToString() == clipId).Take(limit).ToArray();
            if (clips.Length == 0) throw new SafeError("Clip ID is not present in the pack.");
            var key = run ? new LocalConfiguration().GoogleKey.Trim() : "";
            if (run && key.Length == 0) throw new SafeError("No Google API key is configured in the environment or local API file.");
            Directory.CreateDirectory(output);
            var reports = new List<object>();
            var failures = false;
            foreach (var clip in clips)
            {
                var id = clip.GetProperty("id").ToString();
                var filename = clip.GetProperty("localAudio").GetString()!;
                var pcm = ReadPcm(Path.Combine(Path.GetDirectoryName(manifestPath)!, filename), normalize, out var gain, out var rms, out var peak);
                var result = run ? await ReplayAsync(pcm, key) : new ReplayResult();
                failures |= result.Error is not null;
                reports.Add(new
                {
                    id, audioFile = filename, durationSeconds = pcm.Length / 32000d,
                    startSeconds = clip.TryGetProperty("startSeconds", out var offset) ? offset.GetDouble() : (double?)null,
                    gainFactor = gain, originalRmsDbfs = rms > 0 ? 20 * Math.Log10(rms) : (double?)null,
                    originalPeak = peak, outputPeak = peak * gain,
                    sourceText = clip.GetProperty("sourceText").GetString(),
                    referenceText = clip.GetProperty("referenceText").GetString(),
                    result
                });
                await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
                {
                    mode = run ? "TwoStage" : "dry-run", model = GoogleLiveProtocol.LiveModel(),
                    translationModel = FlashTextTranslator.Model,
                    manifestPath,
                    normalizedAudio = normalize,
                    methodology = "Independent per-clip sessions; mono PCM16 16kHz paced in 100ms chunks, approximately 2 seconds trailing silence, audioStreamEnd, 15 seconds drain plus bounded pending translation wait. English references are never sent. Output audio discarded. This bypasses microphone/BlackHole and the caption UI. Opt-in normalization amplifies float samples to peak 0.8 before PCM16 conversion; originals are preserved.",
                    reports
                }, JsonOptions));
                Console.WriteLine($"Clip {id}: {(result.Error ?? (run ? $"{result.Output.Length} caption characters" : "validated audio"))}");
                if (result.StopProvider) break;
            }
            Console.WriteLine($"Saved {output}");
            return failures ? 1 : 0;
        }
        catch (SafeError safe) { Console.WriteLine(safe.Message); return 1; }
        catch { Console.WriteLine("Evaluation failed. No raw exceptions or credentials were logged."); return 1; }
    }

    private static byte[] ReadPcm(string path, bool normalize, out double gain, out double rms, out double peak)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 32 * 1024 * 1024 || bytes.Length < 12 || System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF")
            throw new SafeError("Invalid WAV input.");
        ushort format = 0, channels = 0, bits = 0;
        uint rate = 0;
        byte[]? samples = null;
        for (var pos = 12; pos + 8 <= bytes.Length;)
        {
            var kind = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            var length = checked((int)BitConverter.ToUInt32(bytes, pos + 4));
            pos += 8;
            if (length < 0 || pos + (long)length > bytes.Length) throw new SafeError("Invalid WAV chunk.");
            if (kind == "fmt " && length >= 16)
            {
                format = BitConverter.ToUInt16(bytes, pos); channels = BitConverter.ToUInt16(bytes, pos + 2);
                rate = BitConverter.ToUInt32(bytes, pos + 4); bits = BitConverter.ToUInt16(bytes, pos + 14);
            }
            if (kind == "data") samples = bytes.AsSpan(pos, length).ToArray();
            pos += length + (length & 1);
        }
        if (samples is null || format != 3 || channels != 1 || rate != 16000 || bits != 32 || samples.Length % 4 != 0)
            throw new SafeError("Expected mono 16kHz float32 WAV from this pack.");
        var pcm = new byte[samples.Length / 2];
        peak = 0;
        double squared = 0;
        for (var i = 0; i < samples.Length / 4; i++)
        {
            var sample = BitConverter.ToSingle(samples, i * 4);
            if (!float.IsFinite(sample)) throw new SafeError("Non-finite WAV sample.");
            peak = Math.Max(peak, Math.Abs(sample)); squared += (double)sample * sample;
        }
        rms = Math.Sqrt(squared / (samples.Length / 4));
        gain = normalize && peak > 0 ? 0.8 / peak : 1;
        for (var i = 0; i < samples.Length / 4; i++)
        {
            var sample = BitConverter.ToSingle(samples, i * 4);
            if (!float.IsFinite(sample)) throw new SafeError("Non-finite WAV sample.");
            var value = (short)Math.Clamp(Math.Round(sample * gain * 32768), -32768, 32767);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), value);
        }
        return pcm;
    }

    private static async Task<ReplayResult> ReplayAsync(byte[] pcm, string key)
    {
        var report = new ReplayResult();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("x-goog-api-key", key);
        socket.Options.CollectHttpResponseDetails = true;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(pcm.Length / 32000d + 50));
        var setup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputParser = new GeminiInputTranscript();
        var watch = Stopwatch.StartNew();
        Task? receiver = null;
        var finals = new List<string>();
        var acceptedSources = 0;
        var completedCaptions = 0;
        var currentBatchSize = 1;
        using var detector = new FlashTextTranslator(key, targetLanguage: "language code", languageDetection: true);
        var languages = new TranscriptLanguageResolver();
        await using var pipeline = new SourceTranslationPipeline(async (source, assignment, token) =>
        {
            var start = watch.Elapsed.TotalSeconds;
            using var primary = new FlashTextTranslator(key, sourceLanguage: SpeechLanguageAssignment.Name(assignment.Input), targetLanguage: SpeechLanguageAssignment.Name(assignment.Primary));
            using var secondary = new FlashTextTranslator(key, sourceLanguage: SpeechLanguageAssignment.Name(assignment.Input), targetLanguage: SpeechLanguageAssignment.Name(assignment.Secondary));
            var one = primary.TranslateAsync(source, token); var two = secondary.TranslateAsync(source, token);
            await Task.WhenAll(one, two);
            report.Translations.Add(new TranslationEvent(source.Replace(key, "[REDACTED]"), one.Result, start, watch.Elapsed.TotalSeconds, currentBatchSize, assignment.Input, assignment.Primary, assignment.Secondary, two.Result));
            return (one.Result, two.Result);
        }, lifetime.Token);
        {
            pipeline.SourceAccepted += _ => Interlocked.Increment(ref acceptedSources);
            pipeline.BatchStarted += sources => currentBatchSize = sources.Count;
            pipeline.Caption += update => { finals.Add(update.Text); Interlocked.Add(ref completedCaptions, currentBatchSize); };
            pipeline.Failed += message => { report.Error = message; report.StopProvider = true; lifetime.Cancel(); };
        }
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            connect.CancelAfter(TimeSpan.FromSeconds(20));
            await socket.ConnectAsync(new Uri("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent"), connect.Token);
            receiver = Receive();
            var setupRequest = JsonSerializer.SerializeToNode(GoogleLiveProtocol.CreateSetup())!;
            await Send(setupRequest, connect.Token);
            await setup.Task.WaitAsync(connect.Token);
            report.SetupSeconds = watch.Elapsed.TotalSeconds;
            var sendStart = watch.Elapsed;
            // Exactly the same packet size/rate as the native Mac capture helper.
            var total = pcm.Length + 64000;
            for (var offset = 0; offset < total; offset += 3200)
            {
                var count = Math.Min(3200, total - offset);
                var chunk = offset < pcm.Length ? pcm.AsSpan(offset, Math.Min(count, pcm.Length - offset)).ToArray() : new byte[count];
                await Send(new { realtimeInput = new { audio = new { mimeType = "audio/pcm;rate=16000", data = Convert.ToBase64String(chunk) } } }, lifetime.Token);
                var due = sendStart + TimeSpan.FromSeconds((offset + count) / 32000d);
                var wait = due - watch.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, lifetime.Token);
            }
            await Send(new { realtimeInput = new { audioStreamEnd = true } }, lifetime.Token);
            report.AudioFinishedSeconds = watch.Elapsed.TotalSeconds;
            await Task.Delay(TimeSpan.FromSeconds(15), lifetime.Token);
            // Receive the final ASR segment, then allow a bounded in-flight translation
            // to finish instead of silently cancelling it at the fixed audio drain.
            {
                var deadline = watch.Elapsed + TimeSpan.FromSeconds(20);
                while (Volatile.Read(ref completedCaptions) < Volatile.Read(ref acceptedSources) && watch.Elapsed < deadline)
                    await Task.Delay(100, lifetime.Token);
            }
        }
        catch (SafeError safe) { report.Error = safe.Message; report.StopProvider = safe.StopProvider; }
        catch
        {
            var code = (int)socket.HttpStatusCode;
            report.Error ??= code >= 400 ? $"Google connection HTTP {code}." : "Live connection, setup, or replay failed or timed out.";
            report.StopProvider |= code is 400 or 401 or 403 or 404 or 429;
        }
        finally
        {
            lifetime.Cancel(); socket.Abort();
            if (receiver is not null) try { await receiver.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            await pipeline.DisposeAsync();
            report.PendingTranslations = Math.Max(0, acceptedSources - completedCaptions);
            if (report.PendingTranslations > 0) report.Error ??= "Audio drain ended before every finalized source segment was translated.";
            report.Output = string.Join(" ", finals.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (report.Output.Length == 0) report.Error ??= "No caption returned during the bounded replay and drain.";
            report.ElapsedSeconds = watch.Elapsed.TotalSeconds;
        }
        return report;

        Task Send(object value, CancellationToken token) => socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(value)), WebSocketMessageType.Text, true, token);
        async Task Receive()
        {
            try
            {
                var buffer = new byte[16384];
                while (!lifetime.IsCancellationRequested)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult frame;
                    do
                    {
                        frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), lifetime.Token);
                        if (frame.MessageType == WebSocketMessageType.Close)
                            throw new SafeError($"Google WebSocket closed with code {(int?)frame.CloseStatus ?? 0}.", !setup.Task.IsCompletedSuccessfully);
                        if (message.Length + frame.Count > 2 * 1024 * 1024)
                            throw new SafeError("Oversized Live response.");
                        if (frame.MessageType == WebSocketMessageType.Binary) report.BinaryFrames++;
                        message.Write(buffer, 0, frame.Count);
                    } while (!frame.EndOfMessage);
                    using var json = JsonDocument.Parse(message.GetBuffer().AsMemory(0, checked((int)message.Length)));
                    var root = json.RootElement;
                    if (root.TryGetProperty("error", out var error))
                    {
                        var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var v) ? v : 0;
                        throw new SafeError($"Google service rejected request with code {code}.", code is 3 or 7 or 8 or 16 or 400 or 401 or 403 or 404 or 429);
                    }
                    if (root.TryGetProperty("setupComplete", out _)) setup.TrySetResult();
                    if (root.TryGetProperty("goAway", out _)) throw new SafeError("Live service requested session rollover.");
                    if (root.TryGetProperty("serverContent", out var content))
                    {
                        foreach (var field in new[] { "inputTranscription", "interimInputTranscription", "outputTranscription" })
                            if (content.TryGetProperty(field, out var tr) && tr.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                                report.Events.Add(new TranscriptEvent(field, text.GetString()!.Replace(key, "[REDACTED]"), watch.Elapsed.TotalSeconds, tr.TryGetProperty("languageCode", out var language) ? language.GetString() : null, tr.EnumerateObject().Select(p => p.Name).ToArray()));
                        report.CompletionEvents += new[] { "generationComplete", "turnComplete" }.Count(f => content.TryGetProperty(f, out var p) && p.ValueKind == JsonValueKind.True);
                    }
                    foreach (var original in inputParser.Apply(root))
                    {
                        var update = original;
                        {
                            var detected = await languages.ResolveAsync(original, detector.DetectLanguageAsync, lifetime.Token);
                            if (detected is null) continue;
                            update = detected;
                        }
                        if (!pipeline.TryAcceptTranscript(update, watch.Elapsed.TotalSeconds))
                            throw new SafeError("Translation queue could not keep up with speech.");
                    }
                    if (report.Events.Count > 4096) throw new SafeError("Transcript event limit reached.");
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (SafeError safe)
            {
                report.Error = safe.Message; report.StopProvider = safe.StopProvider;
                setup.TrySetException(safe); lifetime.Cancel();
            }
            catch { report.Error = "Live receive failed or returned invalid data."; setup.TrySetException(new SafeError(report.Error)); lifetime.Cancel(); }
        }
    }
    private sealed class SafeError(string message, bool stopProvider = false) : Exception(message) { public bool StopProvider { get; } = stopProvider; }
    private sealed record TranscriptEvent(string Kind, string Text, double Seconds, string? LanguageCode, string[] Fields);
    private sealed record TranslationEvent(string Source, string Translation, double StartedSeconds, double FinishedSeconds, int SourceSegments, string? SourceLanguage = null, string? PrimaryLanguage = null, string? SecondaryLanguage = null, string? SecondaryTranslation = null);
    private sealed class ReplayResult
    {
        public string Output { get; set; } = "";
        public string? Error { get; set; }
        public bool StopProvider { get; set; }
        public double SetupSeconds { get; set; }
        public double AudioFinishedSeconds { get; set; }
        public double ElapsedSeconds { get; set; }
        public int CompletionEvents { get; set; }
        public int BinaryFrames { get; set; }
        public int PendingTranslations { get; set; }
        public List<TranscriptEvent> Events { get; } = [];
        public List<TranslationEvent> Translations { get; } = [];
    }
}
