using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CapFast.Core;

namespace CapFast.Speech;

public static class MacAudioInputs
{
    public static async Task<IReadOnlyList<AudioInput>> ListAsync(CancellationToken cancellationToken = default)
    {
        var inputs = new List<AudioInput> { new("", "System default") };
        if (!OperatingSystem.IsMacOS()) return inputs;
        var helper = Path.Combine(AppContext.BaseDirectory, "CapFast.AudioCapture");
        if (!File.Exists(helper)) throw new InvalidOperationException("Audio input discovery requires the packaged Mac app.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var info = new ProcessStartInfo(helper)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("--list-inputs");
        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new IOException();
            var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            await stderr; // Drain fixed helper errors; never expose arbitrary process output.
            if (process.ExitCode != 0) throw new IOException();
            using var json = JsonDocument.Parse(output);
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > 256) throw new IOException();
            foreach (var device in json.RootElement.EnumerateArray())
            {
                var id = device.GetProperty("id").GetString();
                var name = device.GetProperty("name").GetString();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || id.Length > 4096 || name.Length > 1024)
                    throw new IOException();
                if (inputs.All(input => input.Id != id)) inputs.Add(new(id, name));
            }
            return inputs;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new InvalidOperationException("Could not list Mac audio inputs. Check connected devices and reopen Settings."); }
        finally
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var output = new StringBuilder();
        var chunk = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(chunk, token)) != 0)
        {
            if (output.Length + count > 65536) throw new IOException();
            output.Append(chunk, 0, count);
        }
        return output.ToString();
    }
}
