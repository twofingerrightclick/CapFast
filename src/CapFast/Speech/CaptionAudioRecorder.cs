using System.Globalization;
using System.Threading.Channels;

namespace CapFast.Speech;

// A separate, bounded disk worker keeps recording I/O out of speech inference.
// Storage trouble disables this session's recorder, never the live captions.
internal sealed class CaptionAudioRecorder : IAsyncDisposable
{
    public static string RecordingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CapFast", "Recordings");
    private sealed record Packet(byte[] Pcm, DateTimeOffset CapturedAt);
    private readonly Channel<Packet> _packets = Channel.CreateBounded<Packet>(new BoundedChannelOptions(100)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Action<string> _report;
    private readonly string _root;
    private readonly Func<DateTimeOffset> _now;
    private readonly int _segmentBytes;
    private readonly Task _worker;
    private int _failed;
    private readonly object _gate = new();
    private bool _stopped;
    private int _lowByte = -1;
    private DateTimeOffset _sampleStartedAt;

    internal CaptionAudioRecorder(Action<string> report, string? root = null,
        Func<DateTimeOffset>? now = null, int segmentSamples = 30 * 60 * FlacRecordingWriter.SampleRate)
    {
        if (segmentSamples <= 0 || segmentSamples > int.MaxValue / 2) throw new ArgumentOutOfRangeException(nameof(segmentSamples));
        _report = report; _root = root ?? RecordingsDirectory; _now = now ?? (() => DateTimeOffset.Now);
        _segmentBytes = segmentSamples * 2;
        _worker = Task.Run(WriteAsync);
    }

    public void Append(ReadOnlySpan<byte> pcm)
    {
        lock (_gate)
        {
            if (_stopped || Volatile.Read(ref _failed) != 0) return;
            var now = _now();
            if (_lowByte >= 0 && !pcm.IsEmpty)
            {
                if (!Enqueue([(byte)_lowByte, pcm[0]], _sampleStartedAt)) return;
                _lowByte = -1; pcm = pcm[1..];
            }
            while (!pcm.IsEmpty)
            {
                var count = Math.Min(3200, pcm.Length & ~1);
                if (count == 0) { _lowByte = pcm[0]; _sampleStartedAt = now; break; }
                if (!Enqueue(pcm[..count].ToArray(), now)) return;
                pcm = pcm[count..];
            }
        }
    }

    private bool Enqueue(byte[] pcm, DateTimeOffset now)
    {
        if (_packets.Writer.TryWrite(new(pcm, now))) return true;
        Failed("Audio recording stopped because storage could not keep up. Captions continue. Check the recordings folder and available disk space before restarting captions.");
        return false;
    }

    private void Failed(string message)
    {
        _packets.Writer.TryComplete();
        if (Interlocked.Exchange(ref _failed, 1) == 0) _report(message);
    }

    private async Task WriteAsync()
    {
        FlacRecordingWriter? file = null;
        var bytes = 0;
        string? date = null;
        try
        {
            await foreach (var packet in _packets.Reader.ReadAllAsync())
            {
                var day = packet.CapturedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (date != day || bytes == _segmentBytes)
                {
                    file?.Dispose(); file = null;
                    bytes = 0; date = day;
                }
                var offset = 0;
                while (offset < packet.Pcm.Length)
                {
                    if (file is null)
                    {
                        var directory = Path.Combine(_root, day);
                        Directory.CreateDirectory(directory);
                        var name = packet.CapturedAt.ToString("HH-mm-ss-fff", CultureInfo.InvariantCulture);
                        file = new(Path.Combine(directory, $"{name}-{Guid.NewGuid():N}.flac"));
                    }
                    var count = Math.Min(_segmentBytes - bytes, packet.Pcm.Length - offset);
                    file.Append(packet.Pcm.AsSpan(offset, count));
                    bytes += count; offset += count;
                    if (bytes == _segmentBytes) { file.Dispose(); file = null; bytes = 0; }
                }
            }
        }
        catch
        {
            Failed("Audio recording could not be saved. Captions continue. Check free disk space and access to the CapFast recordings folder, then restart captions to try again.");
        }
        finally
        {
            try { file?.Dispose(); }
            catch { Failed("The last audio recording could not be finalized. Check disk space and access to the recordings folder. Captions continue."); }
            while (_packets.Reader.TryRead(out _)) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopped = true;
            if (_lowByte >= 0 && Volatile.Read(ref _failed) == 0)
                Failed("Audio recording ended with an incomplete input sample. The completed audio was saved; check the selected input before restarting captions.");
            _packets.Writer.TryComplete();
        }
        await _worker;
    }
}
