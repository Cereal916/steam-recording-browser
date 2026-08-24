using System.IO;
using System.Globalization;
using System.Xml.Linq;
using System.Collections.Concurrent;

namespace SteamRecordingBrowser.Services;

public static class LiveRecordingService
{
    private static readonly TimeSpan ActiveWriteWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ActivityCacheLifetime = TimeSpan.FromSeconds(1);
    private static readonly ConcurrentDictionary<string, ActivityCacheEntry> ActivityCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool IsActivelyRecording(string manifestPath)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        var now = DateTime.UtcNow;
        var manifestWriteUtc = File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath) : DateTime.MinValue;
        if (ActivityCache.TryGetValue(fullPath, out var cached) &&
            cached.ManifestWriteUtc == manifestWriteUtc &&
            now - cached.CheckedAtUtc < ActivityCacheLifetime)
            return cached.IsActive;

        var isActive = DetectActiveRecording(fullPath, now);
        ActivityCache[fullPath] = new ActivityCacheEntry(now, manifestWriteUtc, isActive);
        return isActive;
    }

    private static bool DetectActiveRecording(string manifestPath, DateTime now)
    {
        try
        {
            var directory = Path.GetDirectoryName(manifestPath);
            if (directory is null || !File.Exists(manifestPath))
                return false;

            var root = XDocument.Load(manifestPath).Root;
            if (!string.Equals(root?.Attribute("type")?.Value, "dynamic", StringComparison.OrdinalIgnoreCase))
                return false;

            var newestWrite = DateTime.MinValue;
            foreach (var segment in Directory.EnumerateFiles(directory, "*.m4s", SearchOption.TopDirectoryOnly))
            {
                var write = File.GetLastWriteTimeUtc(segment);
                if (write > newestWrite)
                    newestWrite = write;
            }

            return newestWrite != DateTime.MinValue && now - newestWrite <= ActiveWriteWindow;
        }
        catch
        {
            return false;
        }
    }

    private sealed record ActivityCacheEntry(DateTime CheckedAtUtc, DateTime ManifestWriteUtc, bool IsActive);

    public static double GetDynamicDurationSeconds(string manifestPath)
    {
        try
        {
            var root = XDocument.Load(manifestPath).Root;
            if (!string.Equals(root?.Attribute("type")?.Value, "dynamic", StringComparison.OrdinalIgnoreCase) ||
                !DateTime.TryParse(root?.Attribute("availabilityStartTime")?.Value,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start))
                return 0;

            return Math.Clamp((DateTime.UtcNow - start.ToUniversalTime()).TotalSeconds, 0, 2 * 60 * 60);
        }
        catch
        {
            return 0;
        }
    }
}
