namespace CapFast.Core;

public sealed record ErrorEntry(DateTimeOffset Time, string Message);

public sealed class ErrorLog
{
    private readonly int _limit;
    private readonly Queue<ErrorEntry> _entries = new();
    private readonly object _gate = new();
    public ErrorLog(int limit = 100) => _limit = limit > 0 ? limit : throw new ArgumentOutOfRangeException(nameof(limit));
    public IReadOnlyList<ErrorEntry> Entries { get { lock (_gate) return _entries.ToArray(); } }
    public event Action? Changed;
    public void Add(string message)
    {
        lock (_gate)
        {
            _entries.Enqueue(new(DateTimeOffset.Now, message));
            while (_entries.Count > _limit) _entries.Dequeue();
        }
        Changed?.Invoke();
    }
    public void Clear() { lock (_gate) _entries.Clear(); Changed?.Invoke(); }
}
