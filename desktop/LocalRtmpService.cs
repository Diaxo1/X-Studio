using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace XStudio;

internal sealed class LocalRtmpService : IDisposable
{
    private Process? server;
    public int Port { get; private set; }
    public bool IsRunning => server is { HasExited: false } || (Port > 0 && IsPortOpen());

    public async Task<string> StartAsync()
    {
        if (server is { HasExited: false }) return $"Local RTMP is already listening at 127.0.0.1:{Port}.";
        var executable = FindMediaMtx();
        Port = FindAvailablePort();
        var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XStudio", "mediamtx-local.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, $"logLevel: warn\nrtsp: false\nrtmp: true\nrtmpAddress: 127.0.0.1:{Port}\nhls: false\nwebrtc: false\nsrt: false\nmoq: false\napi: false\nmetrics: false\npprof: false\nplayback: false\npaths:\n  all_others:\n");
        server = Process.Start(new ProcessStartInfo { FileName = executable, Arguments = $"\"{configPath}\"", UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true })
            ?? throw new InvalidOperationException("MediaMTX did not start.");
        _ = server.StandardError.ReadToEndAsync();
        _ = server.StandardOutput.ReadToEndAsync();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (IsPortOpen()) return $"Local RTMP server is ready at 127.0.0.1:{Port}.";
            if (server.HasExited) throw new InvalidOperationException("MediaMTX exited while starting. Check the local RTMP configuration.");
            await Task.Delay(200);
        }
        throw new InvalidOperationException($"MediaMTX started but port {Port} did not open.");
    }

    private bool IsPortOpen()
    {
        try { using var client = new TcpClient(); client.Connect("127.0.0.1", Port); return true; }
        catch (SocketException) { return false; }
    }

    private static int FindAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindMediaMtx()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "mediamtx", "mediamtx.exe");
        if (File.Exists(bundled)) return bundled;
        var localPackages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Microsoft", "WinGet", "Packages");
        var pathMatch = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator).Select(path => Path.Combine(path, "mediamtx.exe")).FirstOrDefault(File.Exists);
        if (pathMatch is not null) return pathMatch;
        if (Directory.Exists(localPackages))
        {
            var package = Directory.GetDirectories(localPackages, "bluenviron.mediamtx*").OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (package is not null)
            {
                var executable = Directory.GetFiles(package, "mediamtx.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (executable is not null) return executable;
            }
        }
        throw new FileNotFoundException("MediaMTX is not installed. Install the X Studio local RTMP component first.");
    }

    public void Dispose()
    {
        try { if (server is { HasExited: false }) server.Kill(entireProcessTree: true); } catch { }
        server?.Dispose(); server = null;
    }
}
