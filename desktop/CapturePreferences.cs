using System.IO;
using System.Text.Json;

namespace XStudio;

internal sealed class CapturePreferences
{
    private readonly string filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XStudio", "capture-settings.json");
    public string CaptureTarget { get; set; } = "desktop";
    public string CameraDevice { get; set; } = string.Empty;
    public string AudioDevice { get; set; } = string.Empty;
    public bool MicrophoneEnabled { get; set; } = true;

    public void Load()
    {
        if (!File.Exists(filePath)) return;
        var saved = JsonSerializer.Deserialize<CapturePreferences>(File.ReadAllText(filePath));
        if (saved is null) return;
        CaptureTarget = saved.CaptureTarget;
        CameraDevice = saved.CameraDevice;
        AudioDevice = saved.AudioDevice;
        MicrophoneEnabled = saved.MicrophoneEnabled;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        var temp = filePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, filePath, overwrite: true);
    }
}
