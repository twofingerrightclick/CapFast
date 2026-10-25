using System.Buffers.Binary;
using CapFast.Core;

namespace CapFast.Speech;

// Accept arbitrary capture byte boundaries, run contiguous 32 ms inference
// windows, then emit the gate's original 100 ms PCM frames without resampling,
// duplication, or padding. Any speech window overlapping a frame qualifies it.
internal sealed class PcmSpeechFramer(Func<float[], float> predict, Action<ClassifiedAudioFrame> emit)
{
    private readonly byte[] _window = new byte[1024];
    private readonly float[] _samples = new float[512];
    private byte[] _frame = new byte[SpeechAudioGate.FrameBytes];
    private int _windowBytes;
    private int _frameBytes;
    private bool _speech;

    public void Append(ReadOnlySpan<byte> pcm)
    {
        while (!pcm.IsEmpty)
        {
            var count = Math.Min(_window.Length - _windowBytes, pcm.Length);
            pcm[..count].CopyTo(_window.AsSpan(_windowBytes));
            _windowBytes += count;
            pcm = pcm[count..];
            if (_windowBytes != _window.Length) continue;
            for (var i = 0; i < _samples.Length; i++)
                _samples[i] = BinaryPrimitives.ReadInt16LittleEndian(_window.AsSpan(i * 2, 2)) / 32768f;
            var speech = predict(_samples) >= 0.5f;
            var offset = 0;
            while (offset < _window.Length)
            {
                count = Math.Min(_frame.Length - _frameBytes, _window.Length - offset);
                _window.AsSpan(offset, count).CopyTo(_frame.AsSpan(_frameBytes));
                _frameBytes += count;
                offset += count;
                _speech |= speech;
                if (_frameBytes != _frame.Length) continue;
                emit(new(_frame, _speech ? AudioFrameKind.Speech : AudioFrameKind.Quiet));
                _frame = new byte[SpeechAudioGate.FrameBytes];
                _frameBytes = 0;
                _speech = false;
            }
            _windowBytes = 0;
        }
    }
}
