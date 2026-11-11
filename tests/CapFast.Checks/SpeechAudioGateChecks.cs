using CapFast.Core;

public static class SpeechAudioGateChecks
{
    public static void RunFixtures(string directory)
    {
        foreach (var filename in new[] { "silence", "noise", "tone", "instrumental-trumpet", "vocal-song", "speech-music-equal", "speech-music-quarter", "talk", "early-30s", "late-30s" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, filename + ".frames"));
            Check(bytes.Length > 0 && bytes.Length % 3201 == 0, "Malformed classifier fixture: " + filename);
            var gate = new SpeechAudioGate();
            var uploaded = 0;
            var endings = 0;
            for (var offset = 0; offset < bytes.Length; offset += 3201)
            {
                var decision = gate.Process(bytes.AsSpan(offset + 1, 3200).ToArray(), (AudioFrameKind)bytes[offset]);
                uploaded += decision.Audio.Count;
                if (decision.EndStream) endings++;
            }
            var speechFixture = filename is "talk" or "early-30s" or "late-30s";
            if (speechFixture) Check(uploaded > 0, "No approved speech in fixture: " + filename);
            if (filename is "silence" or "noise" or "tone")
                Check(uploaded == 0, "Unexpected approved audio in quiet fixture: " + filename);
            // Music fixtures are observational: operators now pause capture manually.
            Console.WriteLine($"{filename}: {bytes.Length / 3201 / 10d:F1}s classified, {uploaded / 10d:F1}s approved, {endings} stream endings.");
        }
    }

    public static void Run()
    {
        var gate = new SpeechAudioGate();
        byte[] Frame(int index) { var bytes = new byte[SpeechAudioGate.FrameBytes]; bytes[0] = (byte)index; return bytes; }
        for (var i = 0; i < 1000; i++)
        {
            var quiet = gate.Process(Frame(0), AudioFrameKind.Quiet);
            Check(quiet.Audio.Count == 0 && !quiet.EndStream && quiet.Activity == AudioActivity.Idle,
                "Silence uploaded audio or repeatedly ended a stream.");
        }
        for (var i = 0; i < 19; i++) Check(gate.Process(Frame(i), AudioFrameKind.Speech).Audio.Count == 0,
            "Speech uploaded before two seconds qualified.");
        var accepted = gate.Process(Frame(19), AudioFrameKind.Speech);
        Check(accepted.Audio.Count == 20 && accepted.Audio.Select(frame => frame[0]).SequenceEqual(Enumerable.Range(0, 20).Select(i => (byte)i)),
            "Qualification discarded or reordered the opening speech.");
        gate.Reset();
        for (var i = 0; i < 10; i++) gate.Process(Frame(i), AudioFrameKind.Speech);
        for (var i = 0; i < 5; i++)
            Check(gate.Process(Frame(100), AudioFrameKind.Quiet).Audio.Count == 0,
                "A half-second pause counted toward speech qualification.");
        for (var i = 10; i < 19; i++)
            Check(gate.Process(Frame(i), AudioFrameKind.Speech).Audio.Count == 0,
                "Speech uploaded before two accumulated speech seconds.");
        accepted = gate.Process(Frame(19), AudioFrameKind.Speech);
        var pausedOpening = Enumerable.Range(0, 10).Select(i => (byte)i)
            .Concat(Enumerable.Repeat((byte)100, 5)).Concat(Enumerable.Range(10, 10).Select(i => (byte)i));
        Check(accepted.Audio.Count == 25 && accepted.Audio.Select(frame => frame[0]).SequenceEqual(pausedOpening),
            "A half-second pause discarded speech before two seconds could qualify.");
        gate.Reset();
        for (var i = 0; i < 10; i++) gate.Process(Frame(i), AudioFrameKind.Speech);
        for (var i = 0; i < 3; i++) gate.Process(Frame(100), AudioFrameKind.Quiet);
        for (var i = 10; i < 19; i++) Check(gate.Process(Frame(i), AudioFrameKind.Speech).Audio.Count == 0, "Quiet time counted as qualifying speech.");
        accepted = gate.Process(Frame(19), AudioFrameKind.Speech);
        Check(accepted.Audio.Count == 23, "A short natural pause lost the beginning of qualified speech.");
        for (var i = 0; i < 9; i++) Check(!gate.Process(Frame(0), AudioFrameKind.Quiet).EndStream, "A short pause prematurely ended speech.");
        accepted = gate.Process(Frame(20), AudioFrameKind.Speech);
        Check(accepted.Audio.Count == 10 && accepted.Audio.Take(9).All(frame => frame[0] == 0)
            && accepted.Audio[^1][0] == 20,
            "Qualified speech requalified or lost its buffered pause on resumption.");
        for (var i = 0; i < 9; i++) Check(!gate.Process(Frame(0), AudioFrameKind.Quiet).EndStream, "A short pause prematurely ended speech.");
        Check(gate.Process(Frame(0), AudioFrameKind.Quiet).EndStream, "Quiet did not finalize the accepted speech stream.");
        Check(!gate.Process(Frame(0), AudioFrameKind.Quiet).EndStream, "Quiet finalized more than once.");
        gate.Reset();
        for (var i = 0; i < 19; i++) gate.Process(Frame(i), AudioFrameKind.Speech);
        for (var i = 0; i < 10; i++) gate.Process(Frame(0), AudioFrameKind.Quiet);
        Check(gate.Process(Frame(0), AudioFrameKind.Speech).Audio.Count == 0, "Separate short utterances combined across a long pause.");
        try { gate.Process(new byte[1], AudioFrameKind.Speech); throw new Exception("Malformed frame accepted."); }
        catch (InvalidDataException) { }
        var shorter = new SpeechAudioGate(1.5, .4);
        for (var i = 0; i < 14; i++)
            Check(shorter.Process(Frame(i), AudioFrameKind.Speech).Audio.Count == 0,
                "Configured qualification uploaded speech too early.");
        Check(shorter.Process(Frame(14), AudioFrameKind.Speech).Audio.Count == 15,
            "Configured qualification was ignored.");
        for (var i = 0; i < 3; i++)
            Check(!shorter.Process(Frame(0), AudioFrameKind.Quiet).EndStream, "Configured silence reset fired early.");
        Check(shorter.Process(Frame(0), AudioFrameKind.Quiet).EndStream, "Configured silence reset was ignored.");
        var longer = new SpeechAudioGate(4, 3);
        for (var i = 0; i < 39; i++)
            Check(longer.Process(Frame(i), AudioFrameKind.Speech).Audio.Count == 0,
                "Maximum speech qualification uploaded early.");
        Check(longer.Process(Frame(39), AudioFrameKind.Speech).Audio.Count == 40,
            "Maximum speech qualification did not open.");
        for (var i = 0; i < 29; i++)
            Check(!longer.Process(Frame(0), AudioFrameKind.Quiet).EndStream, "Maximum silence reset fired early.");
        Check(longer.Process(Frame(0), AudioFrameKind.Quiet).EndStream, "Maximum silence reset did not end speech.");
        longer.Reset();
        var longestOpening = new List<byte>();
        for (var i = 0; i < 40; i++)
        {
            longestOpening.Add((byte)i);
            var decision = longer.Process(Frame(i), AudioFrameKind.Speech);
            if (i < 39)
            {
                Check(decision.Audio.Count == 0, "Pauses counted toward the configured four speech seconds.");
                for (var j = 0; j < 29; j++)
                {
                    longestOpening.Add(100);
                    Check(longer.Process(Frame(100), AudioFrameKind.Quiet).Audio.Count == 0,
                        "Quiet frames bypassed the configured speech requirement.");
                }
            }
            else Check(decision.Audio.Count == 1171 && decision.Audio.Count == longer.MaximumBufferedFrames
                && decision.Audio.Select(frame => frame[0]).SequenceEqual(longestOpening),
                "The buffer cap discarded or reordered a valid opening at the maximum configured timings.");
        }
        Console.WriteLine("Speech gate checks passed: silence, two-second qualification, opening audio, short pauses, stream end, and bounds.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
