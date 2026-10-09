namespace CapFast.Core;

public enum AudioConnectionDecision { Keep, CloseIdle, TranscriptionTimeout }

// Connection lifetime follows accepted speech, not the microphone's continuous
// local heartbeat. Known provisional text blocks idle close. Receipt timing plus
// a long quiet drain is best-effort; it cannot prove provider coverage of every sample.
public sealed class AudioConnectionPolicy
{
    private readonly object _lock = new();
    private double? _idleSince;
    private double? _lastUpload;
    private double? _lastFinal;
    private double? _endedAt;
    private bool _partial;

    public AudioConnectionPolicy(double openedAt) => _idleSince = openedAt;
    public void ObserveActivity(AudioActivity activity, double now)
    {
        lock (_lock)
        {
            if (activity == AudioActivity.Speech) _idleSince = null;
            else _idleSince ??= now;
        }
    }
    public void Uploaded(double now)
    {
        lock (_lock) { _lastUpload = now; _endedAt = null; }
    }
    public void Ended(double now) { lock (_lock) _endedAt = now; }
    public void Transcript(bool final, double now)
    {
        lock (_lock) { _partial = !final; if (final) _lastFinal = now; }
    }
    public AudioConnectionDecision Poll(double now)
    {
        lock (_lock)
        {
            var finished = !_partial && (_lastUpload is null || _lastFinal >= _lastUpload);
            if (_endedAt is { } ended && !finished && now - ended >= 30)
                return AudioConnectionDecision.TranscriptionTimeout;
            if (_idleSince is { } idle && now - idle >= 20 && finished &&
                (_lastUpload is null || _endedAt is not null))
                return AudioConnectionDecision.CloseIdle;
            return AudioConnectionDecision.Keep;
        }
    }
}
