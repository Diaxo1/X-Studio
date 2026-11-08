using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace XStudio;

internal sealed class DestinationStore
{
    private readonly string filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XStudio", "destinations.json");

    public List<Destination> Load()
    {
        if (!File.Exists(filePath)) return [];
        var saved = JsonSerializer.Deserialize<List<SavedDestination>>(File.ReadAllText(filePath)) ?? [];
        return saved.Select(item => new Destination
        {
            Id = item.Id,
            Platform = item.Platform,
            Name = item.Name,
            ServerUrl = item.ServerUrl,
            StreamKey = item.ProtectedStreamKey.Length == 0
                ? string.Empty
                : Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(item.ProtectedStreamKey))),
            UseForStream = item.UseForStream
        }).ToList();
    }

    public void Save(IEnumerable<Destination> destinations)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var saved = destinations.Select(item => new SavedDestination
        {
            Id = item.Id,
            Platform = item.Platform,
            Name = item.Name,
            ServerUrl = item.ServerUrl,
            ProtectedStreamKey = Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(item.StreamKey))),
            UseForStream = item.UseForStream
        }).ToList();

        var temporaryPath = filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, filePath, overwrite: true);
    }
}

internal static class Dpapi
{
    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Protect(byte[] bytes) => Transform(bytes, protect: true);
    public static byte[] Unprotect(byte[] bytes) => Transform(bytes, protect: false);

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, input.Data, bytes.Length);
        DataBlob output = default;
        try
        {
            var succeeded = protect
                ? CryptProtectData(ref input, "X Studio destination", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            if (!succeeded) throw new CryptographicException(Marshal.GetLastWin32Error());
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero) Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
}
