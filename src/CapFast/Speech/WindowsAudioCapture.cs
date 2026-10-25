using System.Runtime.Versioning;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CapFast.Speech;

[SupportedOSPlatform("windows")]
internal static class WindowsAudioCapture
{
    public static IReadOnlyList<(string Id, string Name)> ListDevices(bool outputs = false)
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<(string Id, string Name)> { ("", "System default") };
        foreach (var device in enumerator.EnumerateAudioEndPoints(outputs ? DataFlow.Render : DataFlow.Capture, DeviceState.Active))
        {
            using (device) result.Add((device.ID, device.FriendlyName));
        }
        return result;
    }

    public static async Task RunAsync(string? deviceId, Action<byte[]> consume, CancellationToken token)
    {
        // Build off the UI thread so RecordingStopped is not marshalled to a UI
        // context that may be waiting for capture teardown.
        using var enumerator = new MMDeviceEnumerator();
        using var device = string.IsNullOrWhiteSpace(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            : enumerator.GetDevice(deviceId);
        await using var recorder = new WasapiRecorderBuilder().WithDevice(device)
            .WithFormat(new WaveFormat(16000, 16, 1)).Build();
        if (recorder.WaveFormat.SampleRate != 16000 || recorder.WaveFormat.BitsPerSample != 16 || recorder.WaveFormat.Channels != 1)
            throw new CaptionSessionException("The selected audio input has an unsupported audio format.");
        var chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(200)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        recorder.DataAvailable += (buffer, flags, _, _) =>
        {
            // WASAPI owns callback memory. Copy before handing it to inference.
            if (!chunks.Writer.TryWrite(buffer.ToArray()))
                chunks.Writer.TryComplete(new CaptionSessionException("Windows audio capture could not keep up. Pause captions and restart."));
        };
        recorder.RecordingStopped += (_, args) => chunks.Writer.TryComplete(
            token.IsCancellationRequested ? null : new CaptionSessionException(
                "The selected Windows audio input stopped. Check the device and Windows microphone privacy settings."));
        token.ThrowIfCancellationRequested();
        recorder.StartRecording();
        try
        {
            while (true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var pcm = await chunks.Reader.ReadAsync(timeout.Token);
                consume(pcm);
            }
        }
        finally { recorder.StopRecording(); }
    }
}
