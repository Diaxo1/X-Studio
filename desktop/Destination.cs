namespace XStudio;

public sealed class Destination
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Platform { get; set; } = "TikTok LIVE";
    public string Name { get; set; } = "TikTok LIVE";
    public string ServerUrl { get; set; } = string.Empty;
    public string StreamKey { get; set; } = string.Empty;
    public bool UseForStream { get; set; }
}

internal sealed class SavedDestination
{
    public string Id { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty;
    public string ProtectedStreamKey { get; set; } = string.Empty;
    public bool UseForStream { get; set; }
}
