using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace XStudio;

internal sealed record EngineUpdate(string State, string Encoder, string Message, IReadOnlyDictionary<string, string> DestinationStates);

internal sealed class StreamEngine : IDisposable
{
    private readonly object gate = new();
    private Process? process;
    private bool stopRequested;
    private bool hardwareAttempt;
    private bool frameSeen;
    private string lastStderr = string.Empty;
    private string state = "Idle";
    private string encoder = "Not started";
    private string message = string.Empty;
    private Dictionary<string, string> destinationStates = [];
    private EngineConfig? currentConfig;

    public event Action<EngineUpdate>? StatusChanged;
    public bool IsRunning { get { lock (gate) return process is not null; } }

    public async Task<List<string>> GetAudioDevicesAsync()
    {
        var output = await RunAndCollectAsync(["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"], 10);
        return ParseDeviceNames(output, "audio");
    }

    public async Task<List<string>> GetVideoDevicesAsync()
    {
        var output = await RunAndCollectAsync(["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"], 10);
        return ParseDeviceNames(output, "video");
    }

    private static List<string> ParseDeviceNames(string output, string requestedType)
    {
        var sectionName = requestedType == "video" ? "DirectShow video devices" : "DirectShow audio devices";
        var otherSection = requestedType == "video" ? "DirectShow audio devices" : "DirectShow video devices";
        var hasSectionHeaders = output.Contains(sectionName, StringComparison.OrdinalIgnoreCase);
        var section = hasSectionHeaders ? output.Split(sectionName, 2, StringSplitOptions.None).LastOrDefault() ?? string.Empty : output;
        if (hasSectionHeaders) section = section.Split(otherSection, 2, StringSplitOptions.None)[0];
        var names = Regex.Matches(section, $"\"([^\"\\r\\n]+)\"\\s*\\({requestedType}\\)", RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value.Trim())
            .Where(name => name.Length > 0 && !name.StartsWith("@device_", StringComparison.OrdinalIgnoreCase));
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task StartAsync(IReadOnlyCollection<Destination> destinations, StudioScene scene, string captureTarget, string? cameraDevice, string? audioDevice, string quality, IReadOnlyDictionary<string, string> phoneCaptureInputs)
    {
        if (IsRunning) throw new InvalidOperationException("A broadcast is already running.");
        if (destinations.Count == 0) throw new InvalidOperationException("Select at least one configured destination on the live desk.");
        if (scene.Sources.Any(source => source.Enabled && source.Kind == "Camera") && string.IsNullOrWhiteSpace(cameraDevice))
            throw new InvalidOperationException("This scene includes a camera. Choose a camera in Settings first.");
        if (scene.Sources.Any(source => source.Enabled && source.Kind == "Camera") && !await CanOpenCameraAsync(cameraDevice!))
            throw new InvalidOperationException($"X Studio could not open the selected camera ‘{cameraDevice}’. Close apps that are already using it, then test the camera again.");
        if (scene.Sources.Any(source => source.Enabled && source.Kind == "Phone") && !scene.Sources.Any(source => source.Enabled && source.Kind == "Phone" && source.Content is "Android" or "iPhone"))
            throw new InvalidOperationException("Choose Android or iPhone for the phone source.");
        foreach (var source in scene.Sources.Where(source => source.Enabled && source.Kind == "Phone"))
            if (!phoneCaptureInputs.ContainsKey(source.Id))
                throw new InvalidOperationException($"The {source.Content} screen is not visible yet. Start screen mirroring and wait for the phone display to appear before going live.");
        var sceneOwnsCapture = scene.Sources.Any(source => source.Enabled && (source.Kind is "Display" or "App" or "Background"));
        if (!sceneOwnsCapture && captureTarget != "desktop" && CaptureWindowService.FindByTitle(captureTarget) == IntPtr.Zero)
            throw new InvalidOperationException("The selected app window is no longer open. Reopen it or choose it again in Capture settings.");
        foreach (var source in scene.Sources.Where(source => source.Enabled && source.Kind == "Image"))
            if (!File.Exists(source.Content)) throw new InvalidOperationException($"The image for {source.Name} could not be found. Edit the scene source and choose its file again.");
        foreach (var source in scene.Sources.Where(source => source.Enabled && (source.Kind == "Video" || source.Kind == "Background" && source.Content != "transparent")))
            if (!File.Exists(source.Content)) throw new InvalidOperationException($"The media for {source.Name} could not be found. Edit the scene source and choose its file again.");
        foreach (var source in scene.Sources.Where(source => source.Enabled && source.Kind == "App"))
            if (CaptureWindowService.FindBySource(source) == IntPtr.Zero) throw new InvalidOperationException($"X Studio couldn't resolve the selected window for {source.Name}. Edit the source and choose the open app again.");
        foreach (var destination in destinations) ValidateDestination(destination);

        var encoderList = await RunAndCollectAsync(["-hide_banner", "-encoders"], 10);
        var hardwareAvailable = encoderList.Contains("h264_mf", StringComparison.Ordinal);
        var softwareAvailable = encoderList.Contains("libx264", StringComparison.Ordinal);
        if (!hardwareAvailable && !softwareAvailable) throw new InvalidOperationException("This FFmpeg build has no H.264 encoder.");

        var vertical = scene.Format.StartsWith("Vertical", StringComparison.OrdinalIgnoreCase);
        var (width, height, fps, bitrate) = quality == "480p30"
            ? vertical ? (480, 854, 30, "1200k") : (854, 480, 30, "1200k")
            : vertical ? (720, 1280, 30, "2500k") : (1280, 720, 30, "2500k");
        var targets = destinations.Select(destination => new StreamTarget(destination.Id, destination.Name, destination.ServerUrl, BuildOutputUrl(destination))).ToList();
        var sceneSnapshot = new StudioScene { Id = scene.Id, Name = scene.Name, Format = scene.Format, Template = scene.Template, Sources = scene.Sources.Select(CloneSource).ToList() };
        var textFiles = new Dictionary<string, string>();
        foreach (var source in sceneSnapshot.Sources.Where(source => source.Enabled && source.Kind == "Text"))
        {
            var path = Path.Combine(Path.GetTempPath(), $"xstudio-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(path, WrapText(source.Content, Math.Max(8, (int)(width * source.Width / 100 / Math.Max(1, source.FontSize * .56)))));
            textFiles[source.Id] = path;
        }
        var config = new EngineConfig(targets, sceneSnapshot, captureTarget, cameraDevice, audioDevice, width, height, fps, bitrate, hardwareAvailable, softwareAvailable, textFiles, phoneCaptureInputs);
        lock (gate)
        {
            if (process is not null) throw new InvalidOperationException("A broadcast is already running.");
            currentConfig = config;
            stopRequested = false;
            frameSeen = false;
            hardwareAttempt = hardwareAvailable;
            lastStderr = string.Empty;
            state = "Starting";
            encoder = hardwareAvailable ? "Media Foundation hardware" : "x264 ultrafast software";
            message = $"Preparing {scene.Name} and connecting RTMP outputs.";
            destinationStates = targets.ToDictionary(target => target.Id, _ => "Connecting");
        }
        Publish();
        try { Launch(config, hardwareAvailable); }
        catch { CleanupTextFiles(config); throw; }
    }

    public void Stop()
    {
        Process? current;
        lock (gate)
        {
            current = process;
            if (current is null) { state = "Idle"; message = string.Empty; PublishUnsafe(); return; }
            stopRequested = true;
            state = "Stopping";
            message = "Stopping capture and closing outputs.";
        }
        Publish();
        try { current.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void Launch(EngineConfig config, bool useHardware)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveFfmpeg(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in BuildArguments(config, useHardware)) startInfo.ArgumentList.Add(argument);

        var child = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        child.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) HandleStderr(eventArgs.Data, child, config); };
        child.Exited += (_, _) => HandleExit(child, config, useHardware);
        try
        {
            if (!child.Start()) throw new InvalidOperationException("FFmpeg could not be started.");
            lock (gate)
            {
                if (stopRequested) { child.Kill(entireProcessTree: true); return; }
                process = child;
                hardwareAttempt = useHardware;
                encoder = useHardware ? "Media Foundation hardware" : "x264 ultrafast software";
                if (useHardware && state == "Fallback") message = "Hardware encoder unavailable; using software encoding.";
                else if (useHardware) message = "Trying hardware H.264 encoding.";
                else message = "Using low-latency x264 software encoding.";
            }
            child.BeginErrorReadLine();
            child.BeginOutputReadLine();
            Publish();
        }
        catch (Exception ex)
        {
            child.Dispose();
            lock (gate)
            {
                process = null;
                state = "Error";
                message = ex is System.ComponentModel.Win32Exception
                    ? "FFmpeg was not found. Add ffmpeg.exe to PATH or set FFMPEG_PATH."
                    : ex.Message;
            }
            Publish();
            throw new InvalidOperationException(message, ex);
        }
    }

    private static List<string> BuildArguments(EngineConfig config, bool useHardware)
    {
        var hasExplicitCapture = config.Scene.Sources.Any(source => source.Enabled && (source.Kind is "Display" or "App" or "Background"));
        var captureInput = config.CaptureTarget == "desktop" ? "desktop" : CaptureWindowService.ToFfmpegInput(CaptureWindowService.FindByTitle(config.CaptureTarget));
        var args = new List<string>
        {
            "-hide_banner", "-nostats", "-loglevel", "warning", "-stats_period", "1", "-progress", "pipe:2", "-filter_threads", "2", "-filter_complex_threads", "2"
        };
        if (hasExplicitCapture) args.AddRange(["-f", "lavfi", "-i", $"color=c=black:s={config.Width}x{config.Height}:r={config.Fps}"]);
        else args.AddRange(["-thread_queue_size", "8", "-f", "gdigrab", "-framerate", config.Fps.ToString(), "-draw_mouse", "1", "-i", captureInput]);
        var cameraInputs = new Dictionary<string, int>();
        var imageInputs = new Dictionary<string, int>();
        var phoneInputs = new Dictionary<string, int>();
        var captureInputs = new Dictionary<string, int>();
        var videoInputs = new Dictionary<string, int>();
        var nextInput = 1;
        foreach (var source in config.Scene.Sources.Where(source => source.Enabled))
        {
            if (source.Kind == "Camera")
            {
                cameraInputs[source.Id] = nextInput++;
                args.AddRange(["-thread_queue_size", "8", "-f", "dshow", "-i", $"video={config.CameraDevice}"]);
            }
            else if (source.Kind == "Image")
            {
                imageInputs[source.Id] = nextInput++;
                args.AddRange(["-loop", "1", "-framerate", config.Fps.ToString(), "-i", source.Content]);
            }
            else if (source.Kind == "Phone")
            {
                phoneInputs[source.Id] = nextInput++;
                args.AddRange(["-thread_queue_size", "8", "-f", "gdigrab", "-framerate", config.Fps.ToString(), "-draw_mouse", "0", "-i", config.PhoneCaptureInputs[source.Id]]);
            }
            else if (source.Kind is "Display" or "App")
            {
                var window = source.Kind == "Display" ? IntPtr.Zero : CaptureWindowService.FindBySource(source);
                captureInputs[source.Id] = nextInput++;
                args.AddRange(["-thread_queue_size", "8", "-f", "gdigrab", "-framerate", config.Fps.ToString(), "-draw_mouse", "0", "-i", window == IntPtr.Zero ? "desktop" : CaptureWindowService.ToFfmpegInput(window)]);
            }
            else if ((source.Kind is "Video" or "Background") && source.Content != "transparent")
            {
                videoInputs[source.Id] = nextInput++;
                if (IsVideo(source.Content))
                {
                    if (source.PlaybackPositionSeconds > 0) args.AddRange(["-ss", source.PlaybackPositionSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)]);
                    args.AddRange(["-stream_loop", "-1", "-i", source.Content]);
                }
                else args.AddRange(["-loop", "1", "-framerate", config.Fps.ToString(), "-i", source.Content]);
            }
        }
        int? audioInput = null;
        if (!string.IsNullOrWhiteSpace(config.AudioDevice))
        {
            audioInput = nextInput;
            args.AddRange(["-thread_queue_size", "64", "-f", "dshow", "-audio_buffer_size", "100", "-i", $"audio={config.AudioDevice}"]);
        }

        var graph = BuildSceneGraph(config, cameraInputs, imageInputs, phoneInputs, captureInputs, videoInputs);
        args.AddRange(["-filter_complex", graph, "-map", "[sceneout]"]);
        if (useHardware)
            args.AddRange(["-c:v", "h264_mf", "-hw_encoding", "1", "-scenario", "live_streaming", "-rate_control", "cbr"]);
        else
            args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-profile:v", "baseline"]);
        args.AddRange(["-b:v", config.Bitrate, "-maxrate", config.Bitrate, "-bufsize", $"{int.Parse(config.Bitrate[..^1]) * 2}k", "-g", (config.Fps * 2).ToString(), "-pix_fmt", "yuv420p"]);
        if (audioInput.HasValue)
            args.AddRange(["-map", $"{audioInput.Value}:a:0", "-c:a", "aac", "-b:a", "128k", "-ar", "44100"]);
        else
            args.Add("-an");

        var outputs = string.Join('|', config.Targets.Select(target => $"[f=flv:onfail=ignore]{EscapeTeeUrl(target.OutputUrl)}"));
        args.AddRange(["-use_fifo", "1", "-f", "tee", outputs]);
        return args;
    }

    private static string BuildSceneGraph(EngineConfig config, IReadOnlyDictionary<string, int> cameraInputs, IReadOnlyDictionary<string, int> imageInputs, IReadOnlyDictionary<string, int> phoneInputs, IReadOnlyDictionary<string, int> captureInputs, IReadOnlyDictionary<string, int> videoInputs)
    {
        var vertical = config.Scene.Format.StartsWith("Vertical", StringComparison.OrdinalIgnoreCase);
        var fit = vertical
            ? $"scale={config.Width}:{config.Height}:force_original_aspect_ratio=increase,crop={config.Width}:{config.Height}"
            : $"scale={config.Width}:{config.Height}:force_original_aspect_ratio=decrease,pad={config.Width}:{config.Height}:(ow-iw)/2:(oh-ih)/2";
        var filters = new List<string> { $"[0:v]fps={config.Fps},{fit}[layer0]" };
        var layer = 0;
        foreach (var source in config.Scene.Sources.Where(source => source.Enabled))
        {
            var nextLayer = layer + 1;
            if (source.Kind == "Text")
            {
                var fontFile = EscapeFilterPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "arial.ttf"));
                var textFile = EscapeFilterPath(config.TextFiles[source.Id]);
                var fontColor = "0x" + source.Color.TrimStart('#');
                var draw = $"drawtext=fontfile='{fontFile}':textfile='{textFile}':fontsize={source.FontSize}:fontcolor={fontColor}:x=w*{Percent(source.X)}:y=h*{Percent(source.Y)}:box=1:boxcolor=black@0.38:boxborderw=12";
                filters.Add($"[layer{layer}]{draw}[layer{nextLayer}]");
            }
            else if (source.Kind == "Image" && imageInputs.TryGetValue(source.Id, out var imageInput))
            {
                filters.Add($"[{imageInput}:v]{CropFilter(source)}{ScaleFilter(source, config.Width, config.Height)}[media{nextLayer}]");
                filters.Add($"[layer{layer}][media{nextLayer}]overlay=x=W*{Percent(source.X)}:y=H*{Percent(source.Y)}:shortest=1[layer{nextLayer}]");
            }
            else if (source.Kind == "Camera" && cameraInputs.TryGetValue(source.Id, out var cameraInput))
            {
                filters.Add($"[{cameraInput}:v]fps={config.Fps},{CropFilter(source)}{ScaleFilter(source, config.Width, config.Height)}[media{nextLayer}]");
                filters.Add($"[layer{layer}][media{nextLayer}]overlay=x=W*{Percent(source.X)}:y=H*{Percent(source.Y)}:shortest=1[layer{nextLayer}]");
            }
            else if (source.Kind == "Phone" && phoneInputs.TryGetValue(source.Id, out var phoneInput))
            {
                filters.Add($"[{phoneInput}:v]fps={config.Fps},{CropFilter(source)}{ScaleFilter(source, config.Width, config.Height)}[media{nextLayer}]");
                filters.Add($"[layer{layer}][media{nextLayer}]overlay=x=W*{Percent(source.X)}:y=H*{Percent(source.Y)}:shortest=1[layer{nextLayer}]");
            }
            else if (captureInputs.TryGetValue(source.Id, out var captureIndex))
            {
                filters.Add($"[{captureIndex}:v]fps={config.Fps},{CropFilter(source)}{ScaleFilter(source, config.Width, config.Height)}[media{nextLayer}]");
                filters.Add($"[layer{layer}][media{nextLayer}]overlay=x=W*{Percent(source.X)}:y=H*{Percent(source.Y)}:shortest=1[layer{nextLayer}]");
            }
            else if (videoInputs.TryGetValue(source.Id, out var videoIndex))
            {
                if (source.Kind == "Background")
                {
                    filters.Add($"[{videoIndex}:v]fps={config.Fps},{CropFilter(source)}scale={config.Width}:{config.Height}:force_original_aspect_ratio=increase,crop={config.Width}:{config.Height}[media{nextLayer}]");
                    filters.Add($"[media{nextLayer}]copy[layer{nextLayer}]");
                }
                else
                {
                    filters.Add($"[{videoIndex}:v]fps={config.Fps},{CropFilter(source)}{ScaleFilter(source, config.Width, config.Height)}[media{nextLayer}]");
                    filters.Add($"[layer{layer}][media{nextLayer}]overlay=x=W*{Percent(source.X)}:y=H*{Percent(source.Y)}:shortest=1[layer{nextLayer}]");
                }
            }
            else { continue; }
            layer = nextLayer;
        }
        filters.Add($"[layer{layer}]format=yuv420p[sceneout]");
        return string.Join(';', filters);
    }

    private static string EscapeFilterPath(string path) => Path.GetFullPath(path).Replace("\\", "/").Replace(":", "\\:").Replace("'", "\\'");

    private static string CropFilter(SceneSource source) => source.CropLeft + source.CropRight + source.CropTop + source.CropBottom <= 0 ? string.Empty : $"crop=iw*(1-{Percent(source.CropLeft + source.CropRight)}):ih*(1-{Percent(source.CropTop + source.CropBottom)}):iw*{Percent(source.CropLeft)}:ih*{Percent(source.CropTop)},";
    private static string ScaleFilter(SceneSource source, int canvasWidth, int canvasHeight)
    {
        var width = Math.Max(2, (int)(canvasWidth * source.Width / 100));
        if (source.Height <= 0) return $"scale={width}:-1";
        var height = Math.Max(2, (int)(canvasHeight * source.Height / 100));
        return $"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height}";
    }
    private static bool IsVideo(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi" or ".m4v";

    private static string Percent(double value) => (value / 100).ToString("0.####", CultureInfo.InvariantCulture);

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
        return string.Join(Environment.NewLine, lines);
    }

    private static SceneSource CloneSource(SceneSource source) => new() { Id = source.Id, Kind = source.Kind, Name = source.Name, Content = source.Content, Color = source.Color, X = source.X, Y = source.Y, Width = source.Width, Height = source.Height, CropLeft = source.CropLeft, CropTop = source.CropTop, CropRight = source.CropRight, CropBottom = source.CropBottom, FontSize = source.FontSize, Enabled = source.Enabled, PlaybackPositionSeconds = source.PlaybackPositionSeconds, WindowProcessId = source.WindowProcessId };

    private static void CleanupTextFiles(EngineConfig config)
    {
        foreach (var path in config.TextFiles.Values)
            try { File.Delete(path); } catch (IOException) { }
    }

    private void HandleStderr(string line, Process child, EngineConfig config)
    {
        var publish = false;
        lock (gate)
        {
            if (process != child) return;
            lastStderr = (lastStderr + line + "\n");
            if (lastStderr.Length > 10000) lastStderr = lastStderr[^10000..];
            if (line.StartsWith("frame=", StringComparison.Ordinal))
            {
                frameSeen = true;
                state = "Live";
                message = $"{config.Scene.Name} is composited and sending one shared encode to selected outputs.";
                foreach (var target in config.Targets)
                    if (destinationStates.TryGetValue(target.Id, out var status) && status == "Connecting") destinationStates[target.Id] = "Sending";
                publish = true;
            }
            if (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var target in config.Targets)
                {
                    if (line.Contains(target.ServerUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        destinationStates[target.Id] = "Output warning";
                        message = $"FFmpeg reported a problem with {target.Name}; other outputs may continue.";
                        publish = true;
                    }
                }
            }
        }
        if (publish) Publish();
    }

    private void HandleExit(Process child, EngineConfig config, bool usedHardware)
    {
        bool fallback;
        bool userStopped;
        string errorTail;
        lock (gate)
        {
            if (process != child) return;
            process = null;
            userStopped = stopRequested;
            errorTail = lastStderr;
            fallback = !userStopped && usedHardware && !frameSeen && LooksLikeHardwareFailure(errorTail) && config.SoftwareAvailable;
            if (userStopped)
            {
                state = "Idle";
                message = "Broadcast stopped.";
                foreach (var key in destinationStates.Keys.ToList()) destinationStates[key] = "Ready";
            }
            else if (fallback)
            {
                state = "Fallback";
                message = "Hardware encoding failed to initialize. Switching to x264.";
            }
            else
            {
                state = "Error";
                message = errorTail.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
                    ? "The ingest server refused the connection. Check the server URL and account stream key."
                    : "FFmpeg stopped. Check the server URL, stream key, microphone, or encoder.";
                foreach (var key in destinationStates.Keys.ToList()) destinationStates[key] = "Failed";
            }
        }
        child.Dispose();
        if (fallback) Launch(config, useHardware: false);
        else { CleanupTextFiles(config); Publish(); }
    }

    private static bool LooksLikeHardwareFailure(string text) =>
        Regex.IsMatch(text, "h264_mf|media.?foundation|d3d11|mft|hardware.{0,24}(fail|error|device)|error initializing output stream", RegexOptions.IgnoreCase);

    private static void ValidateDestination(Destination destination)
    {
        if (string.IsNullOrWhiteSpace(destination.Name)) throw new InvalidOperationException("A destination needs a name.");
        if (string.IsNullOrWhiteSpace(destination.StreamKey)) throw new InvalidOperationException($"Add the stream key for {destination.Name}.");
        if (!Uri.TryCreate(destination.ServerUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "rtmp" && uri.Scheme != "rtmps") || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException($"{destination.Name} needs a clean rtmp:// or rtmps:// ingest URL without the stream key.");
        if (destination.ServerUrl.Contains('|') || destination.ServerUrl.Contains('[') || destination.ServerUrl.Contains(']') ||
            destination.StreamKey.Contains('|') || destination.StreamKey.Contains('[') || destination.StreamKey.Contains(']') ||
            destination.StreamKey.Contains('\\') || destination.StreamKey.Contains('\r') || destination.StreamKey.Contains('\n'))
            throw new InvalidOperationException($"{destination.Name} contains a character not supported by the FFmpeg tee output syntax.");
    }

    private static string BuildOutputUrl(Destination destination) =>
        $"{destination.ServerUrl.TrimEnd('/')}/{Uri.EscapeDataString(destination.StreamKey)}";

    private static string EscapeTeeUrl(string url) => url.Replace("\\", "\\\\").Replace("|", "\\|");

    private static async Task<string> RunAndCollectAsync(IReadOnlyCollection<string> arguments, int timeoutSeconds)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveFfmpeg(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var child = new Process { StartInfo = startInfo };
        try
        {
            if (!child.Start()) throw new InvalidOperationException("FFmpeg could not start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("FFmpeg was not found. Add ffmpeg.exe to PATH or set FFMPEG_PATH.", ex);
        }
        var stderr = child.StandardError.ReadToEndAsync();
        var stdout = child.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { child.Kill(entireProcessTree: true); throw new InvalidOperationException("FFmpeg device discovery timed out."); }
        return (await stderr) + "\n" + (await stdout);
    }

    private static string ResolveFfmpeg()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe");
        if (File.Exists(bundled)) return bundled;
        var configured = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        return "ffmpeg";
    }

    private void Publish()
    {
        EngineUpdate snapshot;
        lock (gate) snapshot = new EngineUpdate(state, encoder, message, new Dictionary<string, string>(destinationStates));
        StatusChanged?.Invoke(snapshot);
    }

    private void PublishUnsafe()
    {
        var snapshot = new EngineUpdate(state, encoder, message, new Dictionary<string, string>(destinationStates));
        StatusChanged?.Invoke(snapshot);
    }

    public void Dispose() => Stop();

    private sealed record StreamTarget(string Id, string Name, string ServerUrl, string OutputUrl);
    private static async Task<bool> CanOpenCameraAsync(string device)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-thread_queue_size", "64", "-f", "dshow", "-i", $"video={device}", "-frames:v", "1", "-f", "null", "-" };
        try { return string.IsNullOrWhiteSpace(await RunAndCollectAsync(args, 5)); }
        catch (InvalidOperationException) { return false; }
    }

    private sealed record EngineConfig(List<StreamTarget> Targets, StudioScene Scene, string CaptureTarget, string? CameraDevice, string? AudioDevice, int Width, int Height, int Fps, string Bitrate, bool HardwareAvailable, bool SoftwareAvailable, IReadOnlyDictionary<string, string> TextFiles, IReadOnlyDictionary<string, string> PhoneCaptureInputs);
}

