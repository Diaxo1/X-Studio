using System.Runtime.InteropServices;
using System.Text;

namespace XStudio;

internal static class CaptureWindowService
{
    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr state);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    public static IntPtr FindByTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return IntPtr.Zero;
        return Find(handle => ReadTitle(handle).Equals(title, StringComparison.OrdinalIgnoreCase));
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

    public static IntPtr FindByProcess(int processId, string? preferredTitle = null)
    {
        IntPtr first = IntPtr.Zero;
        var exact = IntPtr.Zero;
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
            first = handle;
            return true;
        }, IntPtr.Zero);
        return exact != IntPtr.Zero ? exact : first;
    }

    public static string ToFfmpegInput(IntPtr handle) => $"hwnd=0x{handle.ToInt64():X}";

    public static bool IsUsable(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindowVisible(handle) || !GetWindowRect(handle, out var rect)) return false;
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
