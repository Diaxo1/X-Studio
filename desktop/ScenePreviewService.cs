using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace XStudio;

internal sealed class ScenePreviewService : IDisposable
{
    private readonly object gate = new();
    private Process? process;
    private readonly List<string> textFiles = [];
    private readonly Dictionary<string, (DateTime CheckedAt, bool Available)> cameraProbeCache = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> capturedSourceIds = [];
    private bool stopping;

    public event Action<byte[]>? FrameReady;
    public event Action<string>? PreviewFailed;
    public bool IsRunning { get { lock (gate) return process is { HasExited: false }; } }
    public bool CapturesSource(string sourceId) { lock (gate) return capturedSourceIds.Contains(sourceId); }
    public bool CaptureTargetFallback { get; private set; }

    public async Task StartAsync(StudioScene scene, string captureTarget, string? cameraDevice, IReadOnlyDictionary<string, string> phoneCaptureInputs)
    {
        Stop();
        var cameraAvailable = !scene.Sources.Any(source => source.Enabled && source.Kind == "Camera")
            || (!string.IsNullOrWhiteSpace(cameraDevice) && await IsCameraAvailableAsync(cameraDevice));
        var arguments = BuildArguments(scene, captureTarget, cameraDevice, cameraAvailable, phoneCaptureInputs);
        var child = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolveFfmpeg(), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            }, EnableRaisingEvents = true
        };
        foreach (var argument in arguments) child.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!child.Start()) throw new InvalidOperationException("The preview capture could not start.");
        }
        catch (Exception ex)
        {
            child.Dispose(); CleanupTextFiles();
            throw new InvalidOperationException("X Studio could not open the live preview. Check FFmpeg and the selected capture input.", ex);
        }
        lock (gate) { process = child; stopping = false; }
        var errors = ReadErrorsAsync(child);
        _ = ReadFramesAsync(child, errors);
        return;
    }

    public void Stop()
    {
        Process? child;
        lock (gate)
        {
            stopping = true;
            child = process;
            process = null;
        }
        if (child is not null)
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { }
            child.Dispose();
        }
        CleanupTextFiles();
    }

    private List<string> BuildArguments(StudioScene scene, string captureTarget, string? cameraDevice, bool cameraAvailable, IReadOnlyDictionary<string, string> phoneCaptureInputs)
    {
        const int fps = 6;
        var vertical = scene.Format.StartsWith("Vertical", StringComparison.OrdinalIgnoreCase);
        var width = vertical ? 360 : 640;
        var height = vertical ? 640 : 360;
        var hasExplicitCapture = scene.Sources.Any(source => source.Enabled && (source.Kind is "Display" or "App" or "Background"));
        var capture = "desktop";
        CaptureTargetFallback = false;
        if (!hasExplicitCapture && captureTarget != "desktop")
        {
            var targetWindow = CaptureWindowService.FindByTitle(captureTarget);
            if (targetWindow != IntPtr.Zero) capture = CaptureWindowService.ToFfmpegInput(targetWindow);
            else CaptureTargetFallback = true;
        }
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats" };
        if (hasExplicitCapture) args.AddRange(["-f", "lavfi", "-i", $"color=c=black:s={width}x{height}:r={fps}"]);
        else args.AddRange(["-thread_queue_size", "64", "-f", "gdigrab", "-framerate", fps.ToString(CultureInfo.InvariantCulture), "-draw_mouse", "0", "-i", capture]);
        var camera = new Dictionary<string, int>();
        var images = new Dictionary<string, int>();
        var phones = new Dictionary<string, int>();
        var captureInputs = new Dictionary<string, int>();
        var videoInputs = new Dictionary<string, int>();
        var captured = new HashSet<string>();
        var next = 1;
        foreach (var source in scene.Sources.Where(source => source.Enabled))
        {
            if (source.Kind == "Camera")
            {
                if (cameraAvailable && !string.IsNullOrWhiteSpace(cameraDevice))
                {
                    camera[source.Id] = next++;
                    args.AddRange(["-thread_queue_size", "256", "-f", "dshow", "-i", $"video={cameraDevice}"]);
                }
            }
            else if (source.Kind == "Image" && File.Exists(source.Content))
            {
                images[source.Id] = next++;
                args.AddRange(["-loop", "1", "-framerate", fps.ToString(CultureInfo.InvariantCulture), "-i", source.Content]);
            }
            else if (source.Kind == "Phone")
            {
                if (phoneCaptureInputs.TryGetValue(source.Id, out var phoneInput))
                {
                    phones[source.Id] = next++;
                    args.AddRange(["-thread_queue_size", "256", "-f", "gdigrab", "-framerate", fps.ToString(CultureInfo.InvariantCulture), "-draw_mouse", "0", "-i", phoneInput]);
                }
            }
            else if (source.Kind is "Display" or "App")
            {
                var window = source.Kind == "Display" ? IntPtr.Zero : CaptureWindowService.FindByTitle(source.Content);
                if (source.Kind == "App" && window == IntPtr.Zero) continue;
                captureInputs[source.Id] = next++;
                args.AddRange(["-thread_queue_size", "256", "-f", "gdigrab", "-framerate", fps.ToString(CultureInfo.InvariantCulture), "-draw_mouse", "0", "-i", window == IntPtr.Zero ? "desktop" : CaptureWindowService.ToFfmpegInput(window)]);
            }
            else if ((source.Kind is "Video" or "Background") && source.Content != "transparent" && File.Exists(source.Content))
            {
                videoInputs[source.Id] = next++;
                if (IsVideo(source.Content)) args.AddRange(["-stream_loop", "-1", "-i", source.Content]);
                else args.AddRange(["-loop", "1", "-framerate", fps.ToString(CultureInfo.InvariantCulture), "-i", source.Content]);
            }
        }

        foreach (var source in scene.Sources.Where(source => source.Enabled))
            if (source.Kind == "Text" || source.Kind == "Image" && images.ContainsKey(source.Id)
                || source.Kind == "Camera" && camera.ContainsKey(source.Id)
                || source.Kind == "Phone" && phones.ContainsKey(source.Id)
                || (source.Kind is "Display" or "App") && captureInputs.ContainsKey(source.Id)
                || (source.Kind is "Video" or "Background") && (source.Content == "transparent" || videoInputs.ContainsKey(source.Id))) captured.Add(source.Id);
        lock (gate) capturedSourceIds = captured;

        var fit = vertical
            ? $"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height}"
            : $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2";
        var filters = new List<string> { hasExplicitCapture ? $"[0:v]fps={fps},scale={width}:{height}[layer0]" : $"[0:v]fps={fps},{fit}[layer0]" };
        var current = 0;
        var fontFile = EscapePath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "arial.ttf"));
        foreach (var source in scene.Sources.Where(source => source.Enabled))
        {
            var nextLayer = current + 1;
            if (source.Kind == "Text")
            {
                var maxCharacters = Math.Max(8, (int)(width * source.Width / 100 / Math.Max(1, source.FontSize * .56)));
                var text = MakeTextFile(WrapText(source.Content, maxCharacters));
                var previewFontSize = Math.Max(8, (int)Math.Round(source.FontSize * width / (vertical ? 720d : 1280d)));
                filters.Add($"[layer{current}]drawtext=fontfile='{fontFile}':textfile='{EscapePath(text)}':fontsize={previewFontSize}:fontcolor=0x{source.Color.TrimStart('#')}:x=w*{Percent(source.X)}:y=h*{Percent(source.Y)}:box=1:boxcolor=black@0.38:boxborderw=6[layer{nextLayer}]");
            }
            else if (images.TryGetValue(source.Id, out var imageInput))
            {
                filters.Add($"[{imageInput}:v]{CropFilter(source)}{ScaleFilter(source, width, height)}[media{nextLayer}]");
                filters.Add(Overlay(current, nextLayer, source));
            }
            else if (camera.TryGetValue(source.Id, out var cameraInput))
            {
                filters.Add($"[{cameraInput}:v]fps={fps},{CropFilter(source)}{ScaleFilter(source, width, height)}[media{nextLayer}]");
                filters.Add(Overlay(current, nextLayer, source));
            }
            else if (phones.TryGetValue(source.Id, out var phoneInput))
            {
                filters.Add($"[{phoneInput}:v]fps={fps},{CropFilter(source)}{ScaleFilter(source, width, height)}[media{nextLayer}]");
                filters.Add(Overlay(current, nextLayer, source));
            }
            else if (captureInputs.TryGetValue(source.Id, out var captureIndex))
            {
                filters.Add($"[{captureIndex}:v]fps={fps},{CropFilter(source)}{ScaleFilter(source, width, height)}[media{nextLayer}]");
                filters.Add(Overlay(current, nextLayer, source));
            }
            else if (videoInputs.TryGetValue(source.Id, out var videoIndex))
            {
                var fitMedia = source.Kind == "Background" ? $"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height}" : $"{CropFilter(source)}{ScaleFilter(source, width, height)}";
                filters.Add($"[{videoIndex}:v]fps={fps},{fitMedia}[media{nextLayer}]");
                if (source.Kind == "Background") filters.Add($"[media{nextLayer}]setpts=PTS-STARTPTS[bkg{nextLayer}]");
                filters.Add(source.Kind == "Background" ? $"[bkg{nextLayer}]copy[layer{nextLayer}]" : Overlay(current, nextLayer, source));
            }
            else { continue; }
            current = nextLayer;
        }
        filters.Add($"[layer{current}]format=yuvj420p[sceneout]");
        args.AddRange(["-filter_complex", string.Join(';', filters), "-map", "[sceneout]", "-an", "-c:v", "mjpeg", "-q:v", "8", "-f", "image2pipe", "pipe:1"]);
        return args;
    }

    private async Task<bool> IsCameraAvailableAsync(string cameraDevice)
    {
        lock (gate)
        {
            if (cameraProbeCache.TryGetValue(cameraDevice, out var cached) && DateTime.UtcNow - cached.CheckedAt < TimeSpan.FromSeconds(20))
                return cached.Available;
        }
        var available = await ProbeCameraAsync(cameraDevice);
        lock (gate) cameraProbeCache[cameraDevice] = (DateTime.UtcNow, available);
        return available;
    }

    private static async Task<bool> ProbeCameraAsync(string cameraDevice)
    {
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolveFfmpeg(), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            }
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-thread_queue_size", "32", "-f", "dshow", "-i", $"video={cameraDevice}", "-frames:v", "1", "-f", "null", "-" })
            child.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!child.Start()) return false;
            var stderr = child.StandardError.ReadToEndAsync();
            var stdout = child.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { child.Kill(entireProcessTree: true); } catch { } return false; }
            _ = await stderr; _ = await stdout;
            return child.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static string Overlay(int current, int next, SceneSource source) =>
        $"[layer{current}][media{next}]overlay=x=W*{Percent(source.X)}:y=H*{Percent(source.Y)}:shortest=1[layer{next}]";

    private string MakeTextFile(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"xstudio-preview-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, text);
        textFiles.Add(path);
        return path;
    }

    private static string Percent(double value) => (value / 100).ToString("0.####", CultureInfo.InvariantCulture);
    private static string CropFilter(SceneSource source) => source.CropLeft + source.CropRight + source.CropTop + source.CropBottom <= 0 ? string.Empty : $"crop=iw*(1-{Percent(source.CropLeft + source.CropRight)}):ih*(1-{Percent(source.CropTop + source.CropBottom)}):iw*{Percent(source.CropLeft)}:ih*{Percent(source.CropTop)},";
    private static string ScaleFilter(SceneSource source, int canvasWidth, int canvasHeight)
    {
        var targetWidth = Math.Max(24, (int)(canvasWidth * source.Width / 100));
        if (source.Height <= 0) return $"scale={targetWidth}:-1";
        var targetHeight = Math.Max(24, (int)(canvasHeight * source.Height / 100));
        return $"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=increase,crop={targetWidth}:{targetHeight}";
    }
    private static bool IsVideo(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi" or ".m4v";
    private static string WrapText(string text, int maxCharacters)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", string.Empty).Split('\n'))
        {
            var line = string.Empty;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + word.Length + 1 > maxCharacters) { lines.Add(line); line = word; }
                else line = line.Length == 0 ? word : line + " " + word;
            }
            lines.Add(line);
        }
        return string.Join('\n', lines);
    }
    private static string EscapePath(string path) => Path.GetFullPath(path).Replace("\\", "/").Replace(":", "\\:").Replace("'", "\\'");

    private static string ResolveFfmpeg()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe");
        if (File.Exists(bundled)) return bundled;
        var path = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : "ffmpeg";
    }

    private async Task ReadFramesAsync(Process child, Task<string> errorsTask)
    {
        var buffer = new byte[65536];
        using var frame = new MemoryStream(256 * 1024);
        var inFrame = false; var previous = -1;
        try
        {
            while (true)
            {
                var count = await child.StandardOutput.BaseStream.ReadAsync(buffer);
                if (count == 0) break;
                for (var index = 0; index < count; index++)
                {
                    var value = buffer[index];
                    if (!inFrame && previous == 0xFF && value == 0xD8)
                    {
                        frame.SetLength(0); frame.WriteByte(0xFF); frame.WriteByte(0xD8); inFrame = true;
                    }
                    else if (inFrame)
                    {
                        frame.WriteByte(value);
                        if (previous == 0xFF && value == 0xD9)
                        {
                            if (frame.Length < 4 || frame.Length > 16 * 1024 * 1024) { frame.SetLength(0); inFrame = false; }
                            else
                            {
                                if (IsCurrent(child)) FrameReady?.Invoke(frame.ToArray());
                                frame.SetLength(0); inFrame = false;
                            }
                        }
                    }
                    previous = value;
                }
            }
            await child.WaitForExitAsync();
            var errors = await errorsTask;
            if (IsCurrent(child) && !IsStopping() && child.ExitCode != 0)
                PreviewFailed?.Invoke(string.IsNullOrWhiteSpace(errors)
                    ? "Live preview stopped. Check that the selected display, camera, and phone receiver are available."
                    : errors.Trim().Length > 700 ? errors.Trim()[^700..] : errors.Trim());
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
        finally
        {
            if (IsCurrent(child))
            {
                lock (gate) process = null;
                child.Dispose(); CleanupTextFiles();
            }
        }
    }

    private static async Task<string> ReadErrorsAsync(Process child)
    {
        try
        {
            return await child.StandardError.ReadToEndAsync();
        }
        catch (ObjectDisposedException) { return string.Empty; }
        catch (IOException) { return string.Empty; }
    }

    private bool IsCurrent(Process child) { lock (gate) return ReferenceEquals(process, child); }
    private bool IsStopping() { lock (gate) return stopping; }

    private void CleanupTextFiles()
    {
        foreach (var path in textFiles) try { File.Delete(path); } catch (IOException) { }
        textFiles.Clear();
    }

    public void Dispose() => Stop();
}
