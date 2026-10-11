namespace CapFast.Core;

public enum AudioFrameKind : byte { Quiet = 0, Speech = 1, Music = 2 }
public enum AudioActivity { Idle, Speech, Music }
public sealed record SpeechGateResult(AudioActivity Activity, IReadOnlyList<byte[]> Audio, bool EndStream);

// Operates on locally classified 100 ms PCM frames. Qualification counts speech,
// not elapsed time; short natural gaps do not discard the start of an utterance.
public sealed class SpeechAudioGate
{
    public const int FrameBytes = 3200;
    private readonly bool _rejectMusic;
    private readonly List<byte[]> _candidate = new();
    private readonly List<byte[]> _gap = new();
    private int _speechFrames;
    private int _quietFrames;
    private bool _open;
    public bool IsOpen => _open;

    public SpeechAudioGate(bool rejectMusic = true) => _rejectMusic = rejectMusic;

    public SpeechGateResult Process(byte[] pcm, AudioFrameKind kind)
    {
        if (pcm.Length != FrameBytes) throw new InvalidDataException("Invalid local audio frame.");
        if (kind is not (AudioFrameKind.Quiet or AudioFrameKind.Speech or AudioFrameKind.Music))
            throw new InvalidDataException("Invalid local audio activity.");
        if (kind == AudioFrameKind.Music && !_rejectMusic) kind = AudioFrameKind.Speech;
        if (kind == AudioFrameKind.Music)
        {
            var end = _open;
            Reset();
            return new(AudioActivity.Music, Array.Empty<byte[]>(), end);
        }
        if (kind == AudioFrameKind.Quiet)
        {
            _quietFrames++;
            if (_speechFrames > 0 || _open) _gap.Add(pcm.ToArray());
            if (_quietFrames >= 4)
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
        if (_candidate.Count > 80)
        {
            // A pathological stream cannot accumulate an unbounded local buffer.
            Reset();
            return new(AudioActivity.Idle, Array.Empty<byte[]>(), false);
        }
        if (_speechFrames < 20) return new(AudioActivity.Speech, Array.Empty<byte[]>(), false);
        _open = true;
        var accepted = _candidate.ToArray(); _candidate.Clear();
        return new(AudioActivity.Speech, accepted, false);
    }

    public void Reset()
    {
        _candidate.Clear(); _gap.Clear(); _speechFrames = 0; _quietFrames = 0; _open = false;
    }
}
