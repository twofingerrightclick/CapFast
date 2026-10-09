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
            Check(speechFixture ? uploaded > 0 : uploaded == 0, "Unexpected approved audio in classifier fixture: " + filename);
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
        var music = gate.Process(Frame(20), AudioFrameKind.Music);
        Check(music.Audio.Count == 0 && music.EndStream && music.Activity == AudioActivity.Idle && !gate.IsOpen,
            "Music was uploaded or failed to close qualified speech immediately.");
        Check(!gate.Process(Frame(20), AudioFrameKind.Music).EndStream, "Music repeatedly ended an already closed stream.");
        for (var i = 0; i < 19; i++) gate.Process(Frame(i), AudioFrameKind.Speech);
        gate.Process(Frame(0), AudioFrameKind.Music);
        Check(gate.Process(Frame(0), AudioFrameKind.Speech).Audio.Count == 0, "Music retained the pre-qualification speech candidate.");
        gate.Reset();
        for (var i = 0; i < 10; i++) gate.Process(Frame(i), AudioFrameKind.Speech);
        for (var i = 0; i < 3; i++) gate.Process(Frame(100), AudioFrameKind.Quiet);
        for (var i = 10; i < 19; i++) Check(gate.Process(Frame(i), AudioFrameKind.Speech).Audio.Count == 0, "Quiet time counted as qualifying speech.");
        accepted = gate.Process(Frame(19), AudioFrameKind.Speech);
        Check(accepted.Audio.Count == 23, "A short natural pause lost the beginning of qualified speech.");
        for (var i = 0; i < 3; i++) Check(!gate.Process(Frame(0), AudioFrameKind.Quiet).EndStream, "A short pause prematurely ended speech.");
        Check(gate.Process(Frame(0), AudioFrameKind.Quiet).EndStream, "Quiet did not finalize the accepted speech stream.");
        Check(!gate.Process(Frame(0), AudioFrameKind.Quiet).EndStream, "Quiet finalized more than once.");
        gate.Reset();
        for (var i = 0; i < 19; i++) gate.Process(Frame(i), AudioFrameKind.Speech);
        for (var i = 0; i < 4; i++) gate.Process(Frame(0), AudioFrameKind.Quiet);
        Check(gate.Process(Frame(0), AudioFrameKind.Speech).Audio.Count == 0, "Separate short utterances combined across a long pause.");
        try { gate.Process(new byte[1], AudioFrameKind.Speech); throw new Exception("Malformed frame accepted."); }
        catch (InvalidDataException) { }
        Console.WriteLine("Speech gate checks passed: silence, two-second qualification, opening audio, music veto, short pauses, stream end, and bounds.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
