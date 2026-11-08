using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace XStudio;

internal sealed class MicrophoneTestService : IDisposable
{
    private readonly object gate = new();
    private Process? process;

    public event Action<double>? LevelChanged;
    public event Action<string>? Failed;
    public bool IsRunning { get { lock (gate) return process is { HasExited: false }; } }

    public async Task StartAsync(string device)
    {
        Stop();
        var child = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolveFfmpeg(), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            }, EnableRaisingEvents = true
        };
        var args = new[]
        {
            "-hide_banner", "-loglevel", "error", "-thread_queue_size", "512", "-f", "dshow",
            "-audio_buffer_size", "100", "-i", $"audio={device}", "-af",
            "astats=metadata=1:reset=1,ametadata=print:key=lavfi.astats.Overall.RMS_level:file=-",
            "-f", "null", "NUL"
        };
        foreach (var argument in args) child.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!child.Start()) throw new InvalidOperationException("The microphone test could not start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            child.Dispose();
            throw new InvalidOperationException("X Studio could not start the microphone test. Check that its bundled FFmpeg is installed.", ex);
        }
        var errorTask = child.StandardError.ReadToEndAsync();
        lock (gate) process = child;
        _ = ReadLevelsAsync(child);
        _ = MonitorExitAsync(child, errorTask);
        await Task.Delay(350);
        if (child.HasExited)
        {
            var error = await errorTask;
            Stop();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? "FFmpeg could not open that microphone. Refresh the devices list and choose another input."
                : error.Trim().Length > 500 ? error.Trim()[^500..] : error.Trim());
        }
    }

    public async Task ProbeAsync(string device)
    {
        await StartAsync(device);
        await Task.Delay(500);
        var available = false;
        lock (gate)
        {
            available = process is { HasExited: false };
        }
        Stop();
        if (!available) throw new InvalidOperationException("The microphone closed before X Studio could use it.");
    }

    public void Stop()
    {
        Process? child;
        lock (gate) { child = process; process = null; }
        if (child is null) return;
        try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { }
        child.Dispose();
    }

    private async Task ReadLevelsAsync(Process child)
    {
        try
        {
            while (await child.StandardOutput.ReadLineAsync() is { } line)
            {
                var match = Regex.Match(line, @"lavfi\.astats\.Overall\.RMS_level=(-?(?:\d+(?:\.\d+)?|inf))", RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                var text = match.Groups[1].Value;
                var db = text.Equals("-inf", StringComparison.OrdinalIgnoreCase) ? -60d :
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : -60d;
                var level = Math.Clamp((db + 60) / 60 * 100, 0, 100);
                if (IsCurrent(child)) LevelChanged?.Invoke(level);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task MonitorExitAsync(Process child, Task<string> errorTask)
    {
        try
        {
            await child.WaitForExitAsync();
            var error = await errorTask;
            if (IsCurrent(child) && child.ExitCode != 0)
                Failed?.Invoke(string.IsNullOrWhiteSpace(error) ? "The microphone input stopped. Check the selected device and its Windows privacy permissions." : error.Trim());
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private bool IsCurrent(Process child) { lock (gate) return ReferenceEquals(process, child); }

    private static string ResolveFfmpeg()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe");
        if (File.Exists(bundled)) return bundled;
        var path = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : "ffmpeg";
    }

    public void Dispose() => Stop();
}
