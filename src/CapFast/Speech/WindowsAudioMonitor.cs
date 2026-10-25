using System.Runtime.Versioning;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CapFast.Speech;

[SupportedOSPlatform("windows")]
internal static class WindowsAudioMonitor
{
    // Called on a worker thread; callbacks must marshal UI changes themselves.
    public static async Task RunAsync(string inputId, string outputId, bool listen,
        Action ready, Action<double> level, CancellationToken token)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var input = string.IsNullOrEmpty(inputId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console) : enumerator.GetDevice(inputId);
        using var output = !listen ? null : string.IsNullOrEmpty(outputId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console) : enumerator.GetDevice(outputId);
        // Full-band stereo monitoring, independent of the 16 kHz caption feed.
        var format = new WaveFormat(48000, 16, 2);
        await using var recorder = new WasapiRecorderBuilder().WithDevice(input).WithFormat(format).Build();
        if (recorder.WaveFormat.SampleRate != format.SampleRate || recorder.WaveFormat.BitsPerSample != 16 || recorder.WaveFormat.Channels != 2)
            throw new IOException("Unsupported monitoring audio format.");
        await using var player = output is null ? null : new WasapiPlayerBuilder().WithDevice(output).WithSharedMode().WithLatency(50).Build();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var audio = new MonitorPlaybackBuffer(format, lifetime.Token);
        var chunks = Channel.CreateBounded<(byte[] Pcm, long At)>(new BoundedChannelOptions(8)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Fail(string message, Exception? cause = null)
        {
            failed.TrySetResult(new IOException(message, cause));
            // Silences the render provider immediately; disposal occurs on this worker.
            lifetime.Cancel();
        }
        using var notifications = enumerator.CreateNotificationClient(false);
        var selectedInput = input.ID;
        var selectedOutput = output?.ID;
        notifications.DeviceStateChanged += (_, e) =>
        {
            if ((e.DeviceId == selectedInput || e.DeviceId == selectedOutput) && e.NewState != DeviceState.Active)
                Fail($"The monitoring audio device became {e.NewState}.");
        };
        notifications.DeviceRemoved += (_, e) => { if (e.DeviceId == selectedInput || e.DeviceId == selectedOutput) Fail("The monitoring audio device was removed."); };
        notifications.DefaultDeviceChanged += (_, e) =>
        {
            if (e.Role != Role.Console) return;
            if ((string.IsNullOrEmpty(inputId) && e.Flow == DataFlow.Capture && e.DeviceId != selectedInput) ||
                (listen && string.IsNullOrEmpty(outputId) && e.Flow == DataFlow.Render && e.DeviceId != selectedOutput)) Fail("The default monitoring audio device changed.");
        };
        recorder.DataAvailable += (buffer, flags, _, _) =>
        {
            if (lifetime.IsCancellationRequested) return;
            var pcm = buffer.ToArray(); // WASAPI owns callback memory.
            if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(pcm);
            chunks.Writer.TryWrite((pcm, Environment.TickCount64));
        };
        recorder.RecordingStopped += (_, args) =>
        {
            if (!lifetime.IsCancellationRequested) Fail("Windows monitoring capture stopped unexpectedly.", args.Exception);
        };
        if (player is not null)
        {
            player.PlaybackStopped += (_, args) =>
            {
                if (!lifetime.IsCancellationRequested) Fail("Windows monitoring playback stopped unexpectedly.", args.Exception);
            };
            player.Init(audio);
        }
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            player?.Play();
            recorder.StartRecording();
            ready();
            var lastReport = Environment.TickCount64;
            double peak = -60;
            while (true)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var chunk = await chunks.Reader.ReadAsync(deadline.Token);
                if (Environment.TickCount64 - chunk.At > 250) continue;
                if (listen) audio.Append(chunk.Pcm);
                peak = Math.Max(peak, MonitorPlaybackBuffer.PeakDb(chunk.Pcm));
                if (Environment.TickCount64 - lastReport >= 100)
                {
                    level(peak); peak = -60; lastReport = Environment.TickCount64;
                }
            }
        }
        catch (OperationCanceledException) when (failed.Task.IsCompleted && !token.IsCancellationRequested)
        { throw new IOException("Windows audio monitoring failed.", await failed.Task); }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Windows audio monitoring received no input audio for five seconds.", exception); }
        finally
        {
            lifetime.Cancel();
            // Both engines are disposed asynchronously before the next session starts.
            player?.Stop();
            recorder.StopRecording();
        }
    }
}
