using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CapFast.Speech;

// One instance per capture session; recurrent state and 64-sample context must
// survive chunk boundaries but must never carry over to a new microphone session.
internal sealed class SileroSpeechDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly float[] _context = new float[64];
    private float[] _state = new float[256];

    public SileroSpeechDetector(string? modelPath = null)
    {
        using var options = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1 };
        _session = new InferenceSession(modelPath ?? Path.Combine(AppContext.BaseDirectory, "Models", "silero_vad.onnx"), options);
    }

    public float Predict(float[] samples)
    {
        if (samples.Length != 512) throw new ArgumentException("Silero requires 512 samples at 16 kHz.", nameof(samples));
        var input = new float[576];
        _context.CopyTo(input, 0);
        samples.CopyTo(input, 64);
        using var results = _session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(input, new[] { 1, 576 })),
            NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(_state, new[] { 2, 1, 128 })),
            NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>(new long[] { 16000 }, Array.Empty<int>()))
        });
        var probability = results.First(x => x.Name == "output").AsTensor<float>().First();
        _state = results.First(x => x.Name == "stateN").AsTensor<float>().ToArray();
        Array.Copy(samples, 448, _context, 0, 64);
        if (!float.IsFinite(probability) || probability is < 0 or > 1)
            throw new InvalidDataException("Invalid speech probability.");
        return probability;
    }

    public void Reset() { Array.Clear(_context); Array.Clear(_state); }
    public void Dispose() => _session.Dispose();
}
