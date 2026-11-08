using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Net.Http;
using Microsoft.Win32;

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
        await EnsureBonjourAsync();
        var receiver = FindPackageExecutable("XStudio.UxPlay", "uxplay.exe");
        airPlayProcess = Process.Start(Hidden(receiver, [])) ?? throw new InvalidOperationException("The AirPlay receiver could not start.");
        return "AirPlay receiver opened. Connect your iPhone and choose this PC from Screen Mirroring.";
    }

    private static async Task EnsureBonjourAsync()
    {
        using (var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Bonjour Service"))
            if (service is not null) return;

        var installer = Path.Combine(Path.GetTempPath(), "bonjour-xstudio-3.1.0.1.msi");
        const string url = "https://swcdn.apple.com/content/downloads/52/06/071-03198/djcqm50b49h4o03eetqwowrdpf4o9sx71z/Bonjour64.msi";
        const string expectedSha256 = "46E31E284DA64D6C2D366352B8A8ABCF7DB28D3E2A870D8FCF15C4A6FE0A6DD1";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var bytes = await client.GetByteArrayAsync(url);
            var digest = Convert.ToHexString(SHA256.HashData(bytes));
            if (!digest.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Apple Bonjour download failed its integrity check.");
            await File.WriteAllBytesAsync(installer, bytes);
            using var install = Process.Start(new ProcessStartInfo
            {
                FileName = "msiexec.exe", Arguments = $"/i \"{installer}\"", UseShellExecute = true, Verb = "runas"
            }) ?? throw new InvalidOperationException("The Bonjour installer could not start.");
            await install.WaitForExitAsync();
            if (install.ExitCode == 3010) throw new InvalidOperationException("Apple Bonjour was installed and Windows needs a restart before iPhone casting can start.");
            if (install.ExitCode != 0) throw new InvalidOperationException($"Apple Bonjour setup returned code {install.ExitCode}.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("iPhone screen mirroring needs Apple's Bonjour service. Approve the Windows administrator prompt to install it, then retry.", ex);
        }
        finally { try { File.Delete(installer); } catch (IOException) { } }
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
