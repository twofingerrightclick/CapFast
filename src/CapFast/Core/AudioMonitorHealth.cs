namespace CapFast.Core;

// Monotonic milliseconds, supplied by the caller so startup and stall deadlines
// can be checked without opening a microphone or waiting in tests.
public sealed class AudioMonitorHealth(long startedAt)
{
    private long? _readyAt;
    private long? _lastLevelAt;

    public void Ready(long now) => _readyAt ??= now;
    public void ObserveLevel(long now) => _lastLevelAt = now;
    public bool HasFreshLevel(long now) => _lastLevelAt is { } last && now - last < 1500;
    public bool HasTimedOut(long now)
    {
        if (_lastLevelAt is { } last) return now - last >= 5000;
        if (_readyAt is { } ready) return now - ready >= 5000;
        // Allow the native permission prompt its full 60-second deadline.
        return now - startedAt >= 65000;
    }
}
