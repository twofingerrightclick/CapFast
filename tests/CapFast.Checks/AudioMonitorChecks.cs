using CapFast.Core;
using CapFast.Speech;
using NAudio.Wave;
using System.Buffers.Binary;

internal static class AudioMonitorChecks
{
    public static void Run()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        var permission = new AudioMonitorHealth(100);
        Require(!permission.HasTimedOut(60100), "Monitor cut off the permission prompt.");
        Require(permission.HasTimedOut(65100), "Unresponsive monitor startup never timed out.");
        Require(!permission.HasFreshLevel(100), "Startup displayed a level before receiving audio.");

        var noAudio = new AudioMonitorHealth(0);
        noAudio.Ready(60000);
        Require(!noAudio.HasTimedOut(64999), "First audio timeout started before the engine was ready.");
        Require(noAudio.HasTimedOut(65000), "Engine with no first audio never timed out.");
        noAudio.Ready(65000);
        Require(noAudio.HasTimedOut(65000), "Repeated ready signals extended the audio deadline.");

        var running = new AudioMonitorHealth(0);
        running.Ready(10);
        running.ObserveLevel(100);
        Require(running.HasFreshLevel(1599) && !running.HasFreshLevel(1600), "Stale level remained visible.");
        Require(!running.HasTimedOut(5099) && running.HasTimedOut(5100), "Stalled audio timeout was incorrect.");
        running.ObserveLevel(5050);
        Require(!running.HasTimedOut(5100), "New audio did not reset the stall timeout.");
        // Older helpers can still emit levels without the readiness marker.
        var legacy = new AudioMonitorHealth(0);
        legacy.ObserveLevel(1000);
        Require(!legacy.HasTimedOut(5999) && legacy.HasTimedOut(6000), "Level-only helper bypassed the watchdog.");
        var silence = new byte[1920];
        Require(MonitorPlaybackBuffer.PeakDb(silence) == -60, "Silent input showed signal.");
        BinaryPrimitives.WriteInt16LittleEndian(silence, short.MinValue);
        Require(MonitorPlaybackBuffer.PeakDb(silence) == 0, "Negative full scale was not detected as clipping.");
        BinaryPrimitives.WriteInt16LittleEndian(silence, 16384);
        Require(Math.Abs(MonitorPlaybackBuffer.PeakDb(silence) + 6.0206) < .001, "Input meter scale is incorrect.");
        try { MonitorPlaybackBuffer.PeakDb(new byte[1]); throw new Exception("Truncated PCM sample was accepted."); }
        catch (IOException) { }

        var format = new WaveFormat(48000, 16, 2);
        using var stop = new CancellationTokenSource();
        var playback = new MonitorPlaybackBuffer(format, stop.Token);
        playback.Append(silence);
        var read = new byte[silence.Length];
        Require(playback.Read(read) == read.Length && read.SequenceEqual(silence), "Playback changed captured PCM.");
        Array.Fill(read, (byte)99);
        playback.Read(read);
        Require(read.All(x => x == 0), "Playback underrun repeated stale sound.");
        playback.Append(new byte[48000]); // Fill the 250 ms buffer.
        playback.Append(silence); // Overflow must discard stale sound and keep the latest audio.
        playback.Read(read);
        Require(read.SequenceEqual(silence), "Overflow retained delayed audio.");
        var oversized = new byte[96000];
        Array.Fill(oversized, (byte)37, 48000, 48000);
        playback.Append(oversized);
        var tail = new byte[48000];
        playback.Read(tail);
        Require(tail.All(x => x == 37), "Oversized callback did not preserve only its newest audio.");
        playback.Append(silence);
        stop.Cancel();
        playback.Read(read);
        Require(read.All(x => x == 0), "Stop leaked queued audio into playback.");
        playback.Append(silence);
        playback.Read(read);
        Require(read.All(x => x == 0), "Late capture callback reactivated stopped playback.");
        Console.WriteLine("Monitor playback checks passed: peak levels, PCM fidelity, underrun, bounded overflow, and immediate stop.");
        Console.WriteLine("Audio monitor checks passed: permission, startup, missing first audio, stalls, and stale levels.");
    }
}
