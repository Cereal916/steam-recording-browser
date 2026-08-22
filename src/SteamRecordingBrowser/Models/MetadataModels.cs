using System.Reflection;

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
    public static string Version { get; } = GetVersion();

    private static string GetVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
            return informationalVersion.Split('+')[0];

        return assembly.GetName().Version?.ToString(3) ?? "Unknown";
    }
}
