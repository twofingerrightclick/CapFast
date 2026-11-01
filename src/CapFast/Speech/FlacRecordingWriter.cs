using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CapFast.Speech;

// Small streaming encoder for our single capture format, following RFC 9639.
// Fixed predictors and Rice coding preserve every sample; incompressible frames
// use verbatim storage. No native codec, external process, or lossy conversion.
internal sealed class FlacRecordingWriter : IDisposable
{
    internal const int SampleRate = 16000;
    private const int BlockSamples = 4096;
    private readonly FileStream _file;
    private readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    private readonly byte[] _pcm = new byte[BlockSamples * 2];
    private readonly int[] _samples = new int[BlockSamples];
    private int _bytes, _frame;
    private ulong _totalSamples;
    private bool _disposed;

    public FlacRecordingWriter(string path)
    {
        _file = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            Span<byte> header = stackalloc byte[42];
            header.Clear();
            "fLaC"u8.CopyTo(header);
            header[4] = 0x80; header[7] = 34; // Last metadata block: STREAMINFO.
            BinaryPrimitives.WriteUInt16BigEndian(header[8..], BlockSamples);
            BinaryPrimitives.WriteUInt16BigEndian(header[10..], BlockSamples);
            BinaryPrimitives.WriteUInt64BigEndian(header[18..], StreamInfo(0));
            _file.Write(header); _file.Flush();
        }
        catch { _file.Dispose(); _md5.Dispose(); throw; }
    }

    private static ulong StreamInfo(ulong samples) => ((ulong)SampleRate << 44) | (15UL << 36) | samples;

    public void Append(ReadOnlySpan<byte> pcm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!pcm.IsEmpty)
        {
            var count = Math.Min(_pcm.Length - _bytes, pcm.Length);
            pcm[..count].CopyTo(_pcm.AsSpan(_bytes));
            _bytes += count; pcm = pcm[count..];
            if (_bytes == _pcm.Length) WriteFrame();
        }
    }

    private int Residual(int i, int order) => order switch
    {
        0 => _samples[i],
        1 => _samples[i] - _samples[i - 1],
        2 => _samples[i] - 2 * _samples[i - 1] + _samples[i - 2],
        _ => throw new ArgumentOutOfRangeException(nameof(order))
    };
    private static uint Fold(int value) => (uint)((value << 1) ^ (value >> 31));

    private void WriteFrame()
    {
        var count = _bytes / 2;
        if (count == 0) return;
        for (var i = 0; i < count; i++)
            _samples[i] = BinaryPrimitives.ReadInt16LittleEndian(_pcm.AsSpan(i * 2));
        var constant = true;
        for (var i = 1; i < count; i++) constant &= _samples[i] == _samples[0];
        // Compare actual bit costs, including warm-up samples and Rice overhead.
        long bestCost = count * 16;
        var bestOrder = -1; var bestRice = 0;
        if (!constant)
        {
            for (var order = 0; order <= 2 && order < count; order++)
            {
                for (var rice = 0; rice < 15; rice++)
                {
                    long cost = order * 16 + 10;
                    for (var i = order; i < count && cost < bestCost; i++)
                        cost += (Fold(Residual(i, order)) >> rice) + 1 + rice;
                    if (cost < bestCost) { bestCost = cost; bestOrder = order; bestRice = rice; }
                }
            }
        }
        var bits = new Bits();
        bits.Write(0xfff8, 16); // Fixed block-size stream.
        bits.Write(0x75, 8); // 16-bit explicit block size, 16 kHz.
        bits.Write(0x08, 8); // Mono, 16-bit samples.
        WriteFrameNumber(bits, _frame++);
        bits.Write((uint)(count - 1), 16);
        bits.Write(Crc(bits.Bytes, 8, 0x07), 8);
        bits.Write(constant ? 0u : bestOrder < 0 ? 2u : (uint)((8 + bestOrder) << 1), 8);
        if (constant) bits.Write((uint)_samples[0], 16);
        else if (bestOrder < 0)
        {
            for (var i = 0; i < count; i++) bits.Write((uint)_samples[i], 16);
        }
        else
        {
            for (var i = 0; i < bestOrder; i++) bits.Write((uint)_samples[i], 16);
            bits.Write(0, 6); // Rice coding method 0, partition order 0.
            bits.Write((uint)bestRice, 4);
            for (var i = bestOrder; i < count; i++)
            {
                var folded = Fold(Residual(i, bestOrder));
                bits.Zeroes((int)(folded >> bestRice)); bits.Write(1, 1);
                bits.Write(folded, bestRice);
            }
        }
        bits.Align();
        bits.Write(Crc(bits.Bytes, 16, 0x8005), 16);
        _file.Write(bits.Bytes);
        _md5.AppendData(_pcm.AsSpan(0, count * 2));
        _totalSamples += (uint)count;
        _bytes = 0;
        // Flush completed frames regularly; a crash still leaves a decodable
        // stream with unknown duration/MD5 rather than a broken container.
        if (_frame % 4 == 0) _file.Flush();
    }

    private static void WriteFrameNumber(Bits bits, int number)
    {
        if (number < 0x80) { bits.Write((uint)number, 8); return; }
        var length = number < 0x800 ? 2 : number < 0x10000 ? 3 : number < 0x200000 ? 4 : 5;
        bits.Write((uint)((0xff << (8 - length)) | (number >> (6 * (length - 1)))), 8);
        for (var i = length - 2; i >= 0; i--) bits.Write((uint)(0x80 | ((number >> (6 * i)) & 63)), 8);
    }

    private static uint Crc(ReadOnlySpan<byte> bytes, int width, uint polynomial)
    {
        uint crc = 0;
        foreach (var value in bytes)
        {
            crc ^= (uint)value << (width - 8);
            for (var i = 0; i < 8; i++) crc = (crc & (1u << (width - 1))) != 0 ? (crc << 1) ^ polynomial : crc << 1;
        }
        return crc & ((1u << width) - 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if ((_bytes & 1) != 0) throw new IOException("Recording ended with an incomplete PCM sample.");
            WriteFrame();
            Span<byte> info = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(info, StreamInfo(_totalSamples));
            _file.Position = 18; _file.Write(info);
            _file.Write(_md5.GetHashAndReset());
            _file.Flush();
        }
        finally { _file.Dispose(); _md5.Dispose(); }
    }

    private sealed class Bits
    {
        private readonly MemoryStream _data = new();
        private uint _pending;
        private int _count;
        public ReadOnlySpan<byte> Bytes => _data.GetBuffer().AsSpan(0, (int)_data.Length);
        public void Write(uint value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                _pending = (_pending << 1) | ((value >> i) & 1);
                if (++_count == 8) { _data.WriteByte((byte)_pending); _pending = 0; _count = 0; }
            }
        }
        public void Zeroes(int count)
        {
            while (count >= 32) { Write(0, 32); count -= 32; }
            Write(0, count);
        }
        public void Align() { if (_count != 0) Write(0, 8 - _count); }
    }
}
