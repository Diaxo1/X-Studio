using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace XStudio;

internal sealed class PhoneCastService : IDisposable
{
    public const string AndroidWindowTitle = "X Studio Android Cast";
    public const string IPhoneWindowTitle = "UxPlay";
    private Process? androidProcess;
    private Process? airPlayProcess;

    public string AndroidStatus => androidProcess is { HasExited: false } ? "Android receiver running" : "Android receiver stopped";
    public string IPhoneStatus => airPlayProcess is { HasExited: false } ? "AirPlay receiver running" : "AirPlay receiver stopped";

    public string? GetWindowCaptureInput(string phoneType)
    {
        var process = phoneType == "iPhone" ? airPlayProcess : androidProcess;
        if (process is null) return null;
        try
        {
            if (process.HasExited) return null;
            var expected = phoneType == "iPhone" ? IPhoneWindowTitle : AndroidWindowTitle;
            var handle = CaptureWindowService.FindByProcess(process.Id, expected);
            if (handle == IntPtr.Zero) handle = CaptureWindowService.FindByTitle(expected);
            // UxPlay renames its video window in newer releases. Its renderer can
            // also expose the stream in a separate process from the tray host.
            if (handle == IntPtr.Zero)
            {
                var markers = phoneType == "iPhone"
                    ? new[] { "uxplay", "airplay", "iphone", "ipad" }
                    : new[] { "scrcpy", "android cast" };
                foreach (var candidate in CaptureWindowService.GetVisibleWindows())
                {
                    if (!markers.Any(marker => candidate.Title.Contains(marker, StringComparison.OrdinalIgnoreCase))) continue;
                    handle = CaptureWindowService.FindByProcess(candidate.ProcessId, candidate.Title);
                    if (handle != IntPtr.Zero) break;
                }
            }
            return handle == IntPtr.Zero ? null : CaptureWindowService.ToFfmpegInput(handle);
        }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    public async Task<string> StartAndroidAsync()
    {
        StopAndroid();
        var scrcpy = FindPackageExecutable("Genymobile.scrcpy", "scrcpy.exe");
        var adb = Path.Combine(Path.GetDirectoryName(scrcpy)!, "adb.exe");
        using (var probe = new Process { StartInfo = Hidden(adb, ["devices"], captureOutput: true) })
        {
            probe.Start();
            var outputTask = probe.StandardOutput.ReadToEndAsync();
            var errorTask = probe.StandardError.ReadToEndAsync();
            await probe.WaitForExitAsync();
            _ = await errorTask;
            var output = await outputTask;
            var devices = output.Split('\n').Count(line => line.Contains("\tdevice", StringComparison.Ordinal));
            if (devices == 0) throw new InvalidOperationException("No Android phone is authorized. Connect it by USB, enable Developer options → USB debugging, then approve the RSA prompt on the phone.");
            if (devices > 1) throw new InvalidOperationException("More than one Android device is connected. Disconnect extras and retry for now.");
        }
        var info = Hidden(scrcpy, ["--no-audio", "--no-control", "--max-size", "1280", "--max-fps", "30", "--window-title", AndroidWindowTitle, "--window-width", "360", "--window-height", "640"]);
        androidProcess = Process.Start(info) ?? throw new InvalidOperationException("scrcpy could not start.");
        await Task.Delay(1200);
        if (androidProcess.HasExited) throw new InvalidOperationException("Android screen mirroring exited. Check USB debugging authorization and try again.");
        return "Android mirror opened. Keep its window open while streaming.";
    }

    public async Task<string> StartIPhoneAsync()
    {
        StopIPhone();
        var receiver = FindPackageExecutable("XStudio.UxPlay", "uxplay.exe");
        airPlayProcess = Process.Start(Hidden(receiver, [])) ?? throw new InvalidOperationException("The AirPlay receiver could not start.");
        await Task.Delay(1200);
        if (airPlayProcess.HasExited) throw new InvalidOperationException("The AirPlay receiver exited while starting. Check Windows Security and the bundled UxPlay runtime, then try again.");
        return "AirPlay receiver opened. Connect your iPhone and choose this PC from Screen Mirroring.";
    }

    public void StopAndroid() { Stop(ref androidProcess); }
    public void StopIPhone() { Stop(ref airPlayProcess); }

    private static void Stop(ref Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose(); process = null;
    }

    private static ProcessStartInfo Hidden(string file, IEnumerable<string> args, bool captureOutput = false)
    {
        var info = new ProcessStartInfo { FileName = file, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = captureOutput, RedirectStandardError = captureOutput };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    private static string FindPackageExecutable(string packagePrefix, string executable)
    {
        var bundledName = executable == "uxplay.exe" ? "uxplay-windows.exe" : executable;
        var runtimeFolder = executable == "scrcpy.exe" ? "scrcpy" : "uxplay";
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XStudio", "tools", runtimeFolder, bundledName),
            Path.Combine(AppContext.BaseDirectory, "tools", runtimeFolder, bundledName)
        };
        for (DirectoryInfo? folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            candidates.Add(Path.Combine(folder.FullName, "tools", runtimeFolder, bundledName));
        if (executable == "uxplay.exe") candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools", "uxplay", "uxplay.exe"));
        var packagedReceiver = candidates.FirstOrDefault(File.Exists);
        if (packagedReceiver is not null) return packagedReceiver;
        var localPackages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Microsoft", "WinGet", "Packages");
        var fromPath = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Select(path => Path.Combine(path, executable)).FirstOrDefault(File.Exists);
        if (fromPath is not null) return fromPath;
        if (Directory.Exists(localPackages))
        {
            var package = Directory.GetDirectories(localPackages, packagePrefix + "*").OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (package is not null)
            {
                var found = Directory.GetFiles(package, executable, SearchOption.AllDirectories).FirstOrDefault();
                if (found is not null) return found;
            }
        }
        throw new FileNotFoundException(executable == "uxplay.exe"
            ? "The iPhone AirPlay receiver is not installed. Download the official x64 portable release from https://github.com/leapbtw/uxplay-windows/releases/latest, extract it to %LOCALAPPDATA%\\XStudio\\tools\\uxplay, and try again."
            : $"{executable} is missing. Install the X Studio {packagePrefix} component first.");
    }

    public void Dispose() { StopAndroid(); StopIPhone(); }
}

