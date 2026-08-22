namespace SteamRecordingBrowser.Models;

public sealed class MetadataEntry
{
    public string RecordingKey { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Favorite { get; set; }
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = new();
}

public sealed class MetadataDocument
{
    public int SchemaVersion { get; set; } = 2;
    public string AppVersion { get; set; } = AppInfo.Version;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;
    public List<MetadataEntry> Entries { get; set; } = new();
}

public static class AppInfo
{
    public const string Version = "1.0.2";
}
