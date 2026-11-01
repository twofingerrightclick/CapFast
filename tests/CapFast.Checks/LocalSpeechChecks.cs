using CapFast.Core;
using CapFast.Speech;

internal static class LocalSpeechChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static void Run()
    {
        // An exact multiple of both inference windows and gate frames. Exercise
        // split samples and windows spanning two frames with adversarial chunking.
        var pcm = new byte[25600];
        new Random(417).NextBytes(pcm);
        List<ClassifiedAudioFrame> Frame(int chunk)
        {
            var result = new List<ClassifiedAudioFrame>();
            var window = 0;
            var framer = new PcmSpeechFramer(_ => ++window == 4 ? 0.9f : 0.1f, result.Add);
            for (var offset = 0; offset < pcm.Length; offset += chunk)
                framer.Append(pcm.AsSpan(offset, Math.Min(chunk, pcm.Length - offset)));
            return result;
        }
        var whole = Frame(pcm.Length);
        foreach (var chunk in new[] { 1, 7, 1024, 3200, 4093 })
        {
            var split = Frame(chunk);
            Check(split.Count == 8 && split.SelectMany(x => x.Pcm).SequenceEqual(pcm), "Framing lost, duplicated, or reordered PCM.");
            Check(split.Select(x => x.Kind).SequenceEqual(whole.Select(x => x.Kind)), "Speech decisions depended on capture callback boundaries.");
        }
        Check(whole[0].Kind == AudioFrameKind.Speech && whole[1].Kind == AudioFrameKind.Speech
            && whole.Skip(2).All(x => x.Kind == AudioFrameKind.Quiet), "Overlapping inference window was not applied to both frames.");
        using var detector = new SileroSpeechDetector();
        var silence = Enumerable.Range(0, 100).Select(_ => detector.Predict(new float[512])).ToArray();
        Check(silence.All(p => p < 0.5f), "Silero classified digital silence as speech.");
        detector.Reset();
        var repeated = Enumerable.Range(0, 100).Select(_ => detector.Predict(new float[512])).ToArray();
        Check(silence.Zip(repeated).All(p => Math.Abs(p.First - p.Second) < 1e-6), "Silero reset retained context or recurrent state.");
        var fixture = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(fixture, new byte[204800]); // 6.4 seconds, 200 inference windows
            var window = 0;
            var measured = System.Text.Json.JsonSerializer.SerializeToElement(MeasurePcm(fixture,
                _ => ++window <= 50 || window is > 75 and <= 150 ? 0.9f : 0.1f));
            Check(measured.GetProperty("speechSeconds").GetDouble() == 4.0
                && measured.GetProperty("uploadedSeconds").GetDouble() == 4.8
                && measured.GetProperty("speechWithheldSeconds").GetDouble() == 0,
                "A sub-second pause discarded speech or lost buffered PCM in the production framer/gate.");
            window = 0;
            measured = System.Text.Json.JsonSerializer.SerializeToElement(MeasurePcm(fixture,
                _ => ++window <= 50 || window is > 100 and <= 175 ? 0.9f : 0.1f));
            Check(measured.GetProperty("speechSeconds").GetDouble() == 4.0
                && measured.GetProperty("uploadedSeconds").GetDouble() == 2.4
                && measured.GetProperty("speechWithheldSeconds").GetDouble() == 1.6,
                "Replay report did not distinguish discarded short speech from qualified speech.");
        }
        finally { File.Delete(fixture); }
        Console.WriteLine("Local speech checks passed: real Silero inference, silence, state reset, arbitrary byte boundaries, overlapping windows, and exact PCM preservation.");
    }

    // Offline measurement only: no microphone, credentials, or cloud calls.
    public static object MeasurePcm(string path, Func<float[], float>? predict = null)
    {
        using var detector = predict is null ? new SileroSpeechDetector() : null;
        var gate = new SpeechAudioGate();
        var recent = new byte[80][];
        var kinds = new List<bool>();
        var uploaded = new List<bool>();
        var opens = new List<double>();
        var ends = new List<double>();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var framer = new PcmSpeechFramer(predict ?? detector!.Predict, frame =>
        {
            recent[kinds.Count % recent.Length] = frame.Pcm;
            kinds.Add(frame.Kind == AudioFrameKind.Speech);
            uploaded.Add(false);
            var wasOpen = gate.IsOpen;
            var decision = gate.Process(frame.Pcm, frame.Kind);
            if (!wasOpen && gate.IsOpen) opens.Add(kinds.Count / 10d);
            if (decision.EndStream) ends.Add(kinds.Count / 10d);
            // The gate returns a contiguous buffered suffix ending at this frame.
            for (var n = uploaded.Count - decision.Audio.Count; n < uploaded.Count; n++)
            {
                Check(n >= 0 && !uploaded[n], "Gate returned duplicate or excess audio.");
                Check(decision.Audio[ n - (uploaded.Count - decision.Audio.Count)].SequenceEqual(recent[n % recent.Length]),
                    "Gate report mapped returned PCM to the wrong input frame.");
                uploaded[n] = true;
            }
        });
        using var input = File.OpenRead(path);
        Check(input.Length % 2 == 0, "PCM has a partial sample.");
        var buffer = new byte[4093];
        int read;
        while ((read = input.Read(buffer)) > 0) framer.Append(buffer.AsSpan(0, read));
        elapsed.Stop();
        object[] Ranges(Func<int, bool> include)
        {
            var ranges = new List<object>();
            var start = -1;
            for (var n = 0; n <= kinds.Count; n++)
            {
                if (n < kinds.Count && include(n)) { if (start < 0) start = n; }
                else if (start >= 0) { ranges.Add(new { startSeconds = start / 10d, endSeconds = n / 10d }); start = -1; }
            }
            return ranges.ToArray();
        }
        return new
        {
            file = path, durationSeconds = input.Length / 32000d,
            classifiedSeconds = kinds.Count / 10d,
            speechSeconds = kinds.Count(x => x) / 10d,
            uploadedSeconds = uploaded.Count(x => x) / 10d,
            speechUploadedSeconds = kinds.Where((speech, n) => speech && uploaded[n]).Count() / 10d,
            speechWithheldSeconds = kinds.Where((speech, n) => speech && !uploaded[n]).Count() / 10d,
            tailSeconds = input.Length / 32000d - kinds.Count / 10d,
            processingSeconds = elapsed.Elapsed.TotalSeconds,
            gateOpenedAtSeconds = opens, streamEndedAtSeconds = ends,
            speechRanges = Ranges(n => kinds[n]), uploadRanges = Ranges(n => uploaded[n]),
            withheldSpeechRanges = Ranges(n => kinds[n] && !uploaded[n])
        };
    }

    public static void RunPcm(string path)
    {
        using var detector = new SileroSpeechDetector();
        var gate = new SpeechAudioGate();
        var frames = 0; var speech = 0; var uploaded = 0;
        var framer = new PcmSpeechFramer(detector.Predict, frame =>
        {
            frames++;
            if (frame.Kind == AudioFrameKind.Speech) speech++;
            uploaded += gate.Process(frame.Pcm, frame.Kind).Audio.Count;
        });
        using var input = File.OpenRead(path);
        var buffer = new byte[4093];
        int read;
        while ((read = input.Read(buffer)) > 0) framer.Append(buffer.AsSpan(0, read));
        Check(uploaded > 0, "Speech fixture did not pass the two-second gate.");
        Console.WriteLine($"{Path.GetFileName(path)}: {frames / 10d:F1}s audio, {speech / 10d:F1}s speech, {uploaded / 10d:F1}s passed gate.");
    }
}
