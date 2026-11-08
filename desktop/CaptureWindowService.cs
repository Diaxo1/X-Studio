using System.Runtime.InteropServices;
using System.Text;
using System.IO;
using System.Diagnostics;

namespace XStudio;

internal static class CaptureWindowService
{
    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr state);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    public static IntPtr FindByTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return IntPtr.Zero;
        var requested = Path.GetFileNameWithoutExtension(title.Trim());
        var exact = Find(handle => ReadTitle(handle).Equals(title.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact != IntPtr.Zero) return exact;
        // Window titles often gain a document name or status suffix while an app is running.
        // Prefer a stable app/title fragment when an exact saved title no longer exists.
        var byCaption = Find(handle =>
        {
            var current = ReadTitle(handle);
            return current.Contains(title.Trim(), StringComparison.OrdinalIgnoreCase)
                || requested.Length >= 3 && current.Contains(requested, StringComparison.OrdinalIgnoreCase);
        });
        if (byCaption != IntPtr.Zero) return byCaption;

        // Some apps change their document caption while open. If the stored caption
        // contains the app name, resolve its process main window as a final fallback.
        var appToken = requested.Split([' ', '—', '–', '-', '|', '·'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(token => token.Length >= 4);
        if (appToken is null) return IntPtr.Zero;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!process.ProcessName.Contains(appToken, StringComparison.OrdinalIgnoreCase)
                    && !process.MainWindowTitle.Contains(appToken, StringComparison.OrdinalIgnoreCase)) continue;
                var processWindow = FindByProcess(process.Id, title.Trim());
                if (processWindow != IntPtr.Zero) return processWindow;
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }
        return IntPtr.Zero;
    }

    public static IntPtr FindBySource(SceneSource source)
    {
        if (source.WindowProcessId > 0)
        {
            var processWindow = FindByProcess(source.WindowProcessId, source.Content);
            if (processWindow != IntPtr.Zero) return processWindow;
        }
        return FindByTitle(source.Content);
    }

    public static IReadOnlyList<string> GetVisibleWindowTitles()
    {
        var titles = new List<string>();
        EnumWindows((handle, _) =>
        {
            if (!IsUsable(handle)) return true;
            var title = ReadTitle(handle);
            if (title.Length > 0 && !titles.Contains(title, StringComparer.OrdinalIgnoreCase)) titles.Add(title);
            return true;
        }, IntPtr.Zero);
        return titles.OrderBy(title => title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<CaptureWindowChoice> GetVisibleWindows()
    {
        var windows = new List<CaptureWindowChoice>();
        EnumWindows((handle, _) =>
        {
            if (!IsUsable(handle)) return true;
            var title = ReadTitle(handle);
            if (title.Length == 0 || windows.Any(item => item.Title.Equals(title, StringComparison.OrdinalIgnoreCase))) return true;
            GetWindowThreadProcessId(handle, out var processId);
            windows.Add(new CaptureWindowChoice(title, unchecked((int)processId)));
            return true;
        }, IntPtr.Zero);
        return windows.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IntPtr FindByProcess(int processId, string? preferredTitle = null)
    {
        IntPtr first = IntPtr.Zero;
        var exact = IntPtr.Zero;
        long largestArea = 0;
        EnumWindows((handle, _) =>
        {
            if (!IsUsable(handle)) return true;
            GetWindowThreadProcessId(handle, out var owner);
            if (owner != (uint)processId) return true;
            var title = ReadTitle(handle);
            if (string.IsNullOrWhiteSpace(title)) return true;
            if (preferredTitle is not null && title.Equals(preferredTitle, StringComparison.OrdinalIgnoreCase))
            {
                exact = handle;
                return false;
            }
            if (preferredTitle is not null && title.Contains(preferredTitle, StringComparison.OrdinalIgnoreCase))
            {
                exact = handle;
                return false;
            }
            if (preferredTitle is not null && preferredTitle.Contains(title, StringComparison.OrdinalIgnoreCase) && title.Length >= 4)
            {
                exact = handle;
                return false;
            }
            if (GetWindowRect(handle, out var rectangle))
            {
                var area = (long)(rectangle.Right - rectangle.Left) * (rectangle.Bottom - rectangle.Top);
                if (area > largestArea) { largestArea = area; first = handle; }
            }
            return true;
        }, IntPtr.Zero);
        return exact != IntPtr.Zero ? exact : first;
    }

    public static string ToFfmpegInput(IntPtr handle) => $"hwnd=0x{handle.ToInt64():X}";

    public static bool IsUsable(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindowVisible(handle) || IsIconic(handle) || !GetWindowRect(handle, out var rect)) return false;
        return rect.Right - rect.Left > 16 && rect.Bottom - rect.Top > 16;
    }

    private static IntPtr Find(Func<IntPtr, bool> predicate)
    {
        IntPtr match = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            if (IsUsable(handle) && predicate(handle)) { match = handle; return false; }
            return true;
        }, IntPtr.Zero);
        return match;
    }

    private static string ReadTitle(IntPtr handle)
    {
        var text = new StringBuilder(512);
        _ = GetWindowText(handle, text, text.Capacity);
        return text.ToString().Trim();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
}

internal sealed record CaptureWindowChoice(string Title, int ProcessId)
{
    public override string ToString() => Title;
}

