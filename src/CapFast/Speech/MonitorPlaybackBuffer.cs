using System.Buffers.Binary;
using NAudio.Wave;

namespace CapFast.Speech;

// Shared-mode playback pulls silence until capture arrives. Keep at most 250 ms
// and discard stale queued audio when the input and output clocks drift apart.
internal sealed class MonitorPlaybackBuffer(WaveFormat format, CancellationToken stopped) : IWaveProvider
{
    private readonly BufferedWaveProvider _buffer = new(format, TimeSpan.FromMilliseconds(250));
    public WaveFormat WaveFormat => _buffer.WaveFormat;

    public void Append(ReadOnlySpan<byte> pcm)
    {
        if (stopped.IsCancellationRequested) return;
        if (pcm.Length % WaveFormat.BlockAlign != 0) throw new IOException("Incomplete monitoring audio frame.");
        if (_buffer.BufferedBytes + pcm.Length > _buffer.BufferLength) _buffer.ClearBuffer();
        var capacity = _buffer.BufferLength / WaveFormat.BlockAlign * WaveFormat.BlockAlign;
        if (pcm.Length > capacity) pcm = pcm[^capacity..];
        _buffer.AddSamples(pcm);
    }

    public int Read(Span<byte> buffer)
    {
        if (stopped.IsCancellationRequested) { buffer.Clear(); return buffer.Length; }
        var count = _buffer.Read(buffer);
        // Stop can race with a render read; never return queued audio after stop.
        if (stopped.IsCancellationRequested) buffer.Clear();
        return count;
    }

    public static double PeakDb(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length % 2 != 0) throw new IOException("Incomplete monitoring audio sample.");
        var peak = 0;
        for (var i = 0; i < pcm.Length; i += 2)
            peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm[i..])));
        return peak == 0 ? -60 : Math.Clamp(20 * Math.Log10(peak / 32768.0), -60, 0);
    }
}
