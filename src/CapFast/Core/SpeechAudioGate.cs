namespace CapFast.Core;

public enum AudioFrameKind : byte { Quiet = 0, Speech = 1 }
public enum AudioActivity { Idle, Speech }
public sealed record SpeechGateResult(AudioActivity Activity, IReadOnlyList<byte[]> Audio, bool EndStream);

// Operates on locally classified 100 ms PCM frames. Qualification counts speech,
// not elapsed time. Retain gaps shorter than the configured quiet reset so pauses do
// not discard the start of an utterance or force already qualified speech to requalify.
public sealed class SpeechAudioGate
{
    public const int FrameBytes = 3200;
    public const double DefaultSpeechQualificationSeconds = 2;
    public const double DefaultSilenceResetSeconds = 1;
    public const double MinimumSpeechQualificationSeconds = .5;
    public const double MaximumSpeechQualificationSeconds = 4;
    public const double MinimumSilenceResetSeconds = .2;
    public const double MaximumSilenceResetSeconds = 3;
    private readonly int _qualificationSpeechFrames;
    private readonly int _quietResetFrames;
    private readonly List<byte[]> _candidate = new();
    private readonly List<byte[]> _gap = new();
    private int _speechFrames;
    private int _quietFrames;
    private bool _open;
    public bool IsOpen => _open;
    public int MaximumBufferedFrames { get; }

    public SpeechAudioGate(double speechQualificationSeconds = DefaultSpeechQualificationSeconds,
        double silenceResetSeconds = DefaultSilenceResetSeconds)
    {
        _qualificationSpeechFrames = (int)Math.Round(NormalizeSpeechQualification(speechQualificationSeconds) * 10);
        _quietResetFrames = (int)Math.Round(NormalizeSilenceReset(silenceResetSeconds) * 10);
        // At qualification there can be one tolerated gap between each pair of
        // speech frames. Bound storage without discarding a valid paused opening.
        MaximumBufferedFrames = 1 + (_qualificationSpeechFrames - 1) * _quietResetFrames;
    }

    internal static double NormalizeSpeechQualification(double seconds) => Normalize(seconds,
        MinimumSpeechQualificationSeconds, MaximumSpeechQualificationSeconds, DefaultSpeechQualificationSeconds);
    internal static double NormalizeSilenceReset(double seconds) => Normalize(seconds,
        MinimumSilenceResetSeconds, MaximumSilenceResetSeconds, DefaultSilenceResetSeconds);
    private static double Normalize(double seconds, double minimum, double maximum, double fallback) =>
        double.IsFinite(seconds) && seconds >= minimum && seconds <= maximum
            ? Math.Round(seconds * 10, MidpointRounding.AwayFromZero) / 10 : fallback;

    public SpeechGateResult Process(byte[] pcm, AudioFrameKind kind)
    {
        if (pcm.Length != FrameBytes) throw new InvalidDataException("Invalid local audio frame.");
        if (kind is not (AudioFrameKind.Quiet or AudioFrameKind.Speech))
            throw new InvalidDataException("Invalid local audio activity.");
        if (kind == AudioFrameKind.Quiet)
        {
            _quietFrames++;
            if (_speechFrames > 0 || _open) _gap.Add(pcm.ToArray());
            if (_quietFrames >= _quietResetFrames)
            {
                var end = _open;
                Reset();
                return new(AudioActivity.Idle, Array.Empty<byte[]>(), end);
            }
            return new(AudioActivity.Idle, Array.Empty<byte[]>(), false);
        }

        _quietFrames = 0;
        if (_open)
        {
            var audio = _gap.Append(pcm.ToArray()).ToArray();
            _gap.Clear();
            return new(AudioActivity.Speech, audio, false);
        }
        _candidate.AddRange(_gap); _gap.Clear();
        _candidate.Add(pcm.ToArray());
        _speechFrames++;
        if (_candidate.Count > MaximumBufferedFrames)
        {
            // A pathological stream cannot accumulate an unbounded local buffer.
            Reset();
            return new(AudioActivity.Idle, Array.Empty<byte[]>(), false);
        }
        if (_speechFrames < _qualificationSpeechFrames) return new(AudioActivity.Speech, Array.Empty<byte[]>(), false);
        _open = true;
        var accepted = _candidate.ToArray(); _candidate.Clear();
        return new(AudioActivity.Speech, accepted, false);
    }

    public void Reset()
    {
        _candidate.Clear(); _gap.Clear(); _speechFrames = 0; _quietFrames = 0; _open = false;
    }
}
