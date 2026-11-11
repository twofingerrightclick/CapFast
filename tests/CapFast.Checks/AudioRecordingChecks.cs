using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using CapFast.Speech;
using NAudio.Wave;

internal static class AudioRecordingChecks
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CapFast-recording-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var random = new Random(417);
            var pcm = new byte[4096 * 132 * 2 + 38];
            for (var i = 0; i < pcm.Length / 2; i++)
            {
                var block = i / 4096 % 6;
                var sample = block switch
                {
                    0 => 0,
                    1 => 17000,
                    2 => (int)(24000 * Math.Sin(i * 0.06)),
                    3 => random.Next(short.MinValue, short.MaxValue + 1),
                    4 => i % 2 == 0 ? short.MinValue : short.MaxValue,
                    _ => (i % 4096) * 8 - 16384
                };
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)sample);
            }
            byte[]? reference = null;
            foreach (var chunk in new[] { 1, 7, 3200, pcm.Length })
            {
                var path = Path.Combine(directory, $"codec-{chunk}.flac");
                using (var file = new FlacRecordingWriter(path))
                    for (var i = 0; i < pcm.Length; i += chunk)
                        file.Append(pcm.AsSpan(i, Math.Min(chunk, pcm.Length - i)));
                var flac = File.ReadAllBytes(path);
                VerifyMetadata(flac, pcm);
                if (reference is not null) Check(flac.SequenceEqual(reference), "FLAC depends on capture packet boundaries.");
                reference = flac;
                VerifyWithIndependentDecoder(path, pcm);
            }
            // Cross the three-byte coded frame-number boundary used in long recordings.
            var longSilence = new byte[4096 * 2050 * 2 + 2];
            var longPath = Path.Combine(directory, "long.flac");
            using (var file = new FlacRecordingWriter(longPath)) file.Append(longSilence);
            VerifyMetadata(File.ReadAllBytes(longPath), longSilence);
            VerifyWithIndependentDecoder(longPath, longSilence);
            foreach (var samples in new[] { 1, 2, 15, 16, 4095, 4096, 4097 })
            {
                var tail = pcm.AsSpan(0, samples * 2).ToArray();
                var path = Path.Combine(directory, $"tail-{samples}.flac");
                using (var file = new FlacRecordingWriter(path)) file.Append(tail);
                VerifyMetadata(File.ReadAllBytes(path), tail);
                VerifyWithIndependentDecoder(path, tail);
            }

            var notices = new List<string>();
            var root = Path.Combine(directory, "recordings");
            var now = new DateTimeOffset(2026, 11, 1, 23, 59, 59, TimeSpan.FromHours(-8));
            var input = pcm.AsSpan(8192 * 2, 800).ToArray();
            await using (var recording = new CaptionAudioRecorder(notices.Add, root, () => now, segmentSamples: 100))
            {
                // Carry half a sample across midnight, then change date and rotate.
                recording.Append(input.AsSpan(0, 3));
                now = now.AddSeconds(2);
                recording.Append(input.AsSpan(3, 1));
                recording.Append(input.AsSpan(4));
            }
            Check(notices.Count == 0, "Normal recording reported a failure.");
            var files = Directory.GetFiles(root, "*.flac", SearchOption.AllDirectories);
            Check(files.Length == 5, "Midnight/segment rotation lost or added a recording.");
            Check(Directory.GetFiles(Path.Combine(root, "2026-11-01")).Length == 1, "A split sample was assigned to the wrong date.");
            var expected = new[] { input[..4], input[4..204], input[204..404], input[404..604], input[604..] };
            foreach (var file in files)
            {
                var flac = File.ReadAllBytes(file);
                var original = expected.Single(part => MD5.HashData(part).AsSpan().SequenceEqual(flac.AsSpan(26, 16)));
                VerifyMetadata(flac, original);
                VerifyWithIndependentDecoder(file, original);
            }
            await using (var recording = new CaptionAudioRecorder(notices.Add, root, () => now)) recording.Append(input);
            Check(Directory.GetFiles(root, "*.flac", SearchOption.AllDirectories).Length == 6, "Restart overwrote an existing recording.");
            var stopped = new CaptionAudioRecorder(notices.Add, root, () => now);
            stopped.Append(input);
            await stopped.DisposeAsync();
            stopped.Append(input); // A late capture callback must not reopen the finished file.
            await stopped.DisposeAsync();
            Check(Directory.GetFiles(root, "*.flac", SearchOption.AllDirectories).Length == 7 && notices.Count == 0,
                "Late capture or repeated stop changed a finalized recording.");
            var emptyRoot = Path.Combine(directory, "empty");
            await using (var recording = new CaptionAudioRecorder(notices.Add, emptyRoot)) { }
            Check(!Directory.Exists(emptyRoot), "A session without audio created an empty recording.");
            var blockedRoot = Path.Combine(directory, "blocked");
            File.WriteAllText(blockedRoot, "This path is a file, not a directory.");
            await using (var recording = new CaptionAudioRecorder(notices.Add, blockedRoot)) recording.Append(input);
            Check(notices.Count == 1 && notices[0].Contains("Captions continue"), "Storage failure was not isolated and reported once.");
            Console.WriteLine("Audio recording checks passed: lossless sample hashes, arbitrary packets, tails, long frame numbers, midnight, segment rotation, restart, empty capture, and storage failure.");
            Console.WriteLine(Environment.GetEnvironmentVariable("CAPFAST_FFMPEG") is not null
                ? "FFmpeg's independent FLAC decoder recovered every fixture sample exactly."
                : "Independent decode checks skipped; set CAPFAST_FFMPEG to a local FFmpeg executable to verify sample recovery.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void VerifyMetadata(byte[] flac, byte[] pcm)
    {
        Check(flac.AsSpan(0, 4).SequenceEqual("fLaC"u8), "Recording is not FLAC.");
        var info = BinaryPrimitives.ReadUInt64BigEndian(flac.AsSpan(18));
        Check(info >> 44 == 16000 && (info >> 36 & 31) == 15 && (info >> 41 & 7) == 0, "Capture format changed.");
        Check((info & 0xfffffffff) == (ulong)pcm.Length / 2, "Recording sample count is wrong.");
        Check(flac.AsSpan(26, 16).SequenceEqual(MD5.HashData(pcm)), "Recording checksum does not describe the input PCM.");
    }

    internal static void RecordFixture(string input, string output)
    {
        using var wave = new WaveFileReader(input);
        Check(wave.WaveFormat.Encoding == WaveFormatEncoding.Pcm && wave.WaveFormat.SampleRate == 16000
            && wave.WaveFormat.Channels == 1 && wave.WaveFormat.BitsPerSample == 16,
            "Fixture must be a 16 kHz, 16-bit mono PCM WAV, matching caption capture.");
        var pcm = new byte[wave.Length];
        wave.ReadExactly(pcm);
        var elapsed = Stopwatch.StartNew();
        using (var file = new FlacRecordingWriter(output))
            for (var offset = 0; offset < pcm.Length; offset += 3200)
                file.Append(pcm.AsSpan(offset, Math.Min(3200, pcm.Length - offset)));
        elapsed.Stop();
        var flac = File.ReadAllBytes(output);
        VerifyMetadata(flac, pcm);
        VerifyWithIndependentDecoder(output, pcm);
        var seconds = pcm.Length / 32000d;
        Console.WriteLine($"Recorded {seconds:F1}s in {elapsed.Elapsed.TotalSeconds:F2}s: {flac.Length / 1000000d:F2} MB, projected {flac.Length / seconds * 3600 / 1000000:F1} MB/hour. Exact source checksum verified: {output}");
    }

    private static void VerifyWithIndependentDecoder(string path, byte[] pcm)
    {
        var executable = Environment.GetEnvironmentVariable("CAPFAST_FFMPEG");
        if (string.IsNullOrWhiteSpace(executable)) return;
        var output = path + ".wav";
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in new[] { "-v", "error", "-xerror", "-i", path, "-c:a", "pcm_s16le", output }) info.ArgumentList.Add(argument);
        using var decoder = Process.Start(info)!;
        if (!decoder.WaitForExit(15000)) { decoder.Kill(); throw new Exception("System FLAC decoder timed out."); }
        Check(decoder.ExitCode == 0, "System FLAC decoder rejected recording: " + decoder.StandardError.ReadToEnd());
        using var wave = new WaveFileReader(output);
        Check(wave.WaveFormat.SampleRate == 16000 && wave.WaveFormat.Channels == 1 && wave.WaveFormat.BitsPerSample == 16,
            "System decoder changed audio format.");
        var decoded = new byte[wave.Length];
        wave.ReadExactly(decoded);
        Check(decoded.SequenceEqual(pcm), "System decoder did not recover the exact captured audio.");
    }
}
