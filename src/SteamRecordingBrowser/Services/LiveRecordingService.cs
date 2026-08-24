using System.IO;
using System.Globalization;
using System.Xml.Linq;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

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
            if (root is null ||
                !string.Equals(root.Attribute("type")?.Value, "dynamic", StringComparison.OrdinalIgnoreCase))
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
            if (root is null ||
                !string.Equals(root.Attribute("type")?.Value, "dynamic", StringComparison.OrdinalIgnoreCase))
                return 0;

            var retainedDuration = GetRetainedSegmentDuration(root, Path.GetDirectoryName(manifestPath)!);
            if (retainedDuration > 0)
                return Math.Min(retainedDuration, 2 * 60 * 60);

            if (!DateTime.TryParse(root.Attribute("availabilityStartTime")?.Value,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start))
                return 0;

            return Math.Clamp((DateTime.UtcNow - start.ToUniversalTime()).TotalSeconds, 0, 2 * 60 * 60);
        }
        catch
        {
            return 0;
        }
    }

    private static double GetRetainedSegmentDuration(XElement root, string directory)
    {
        var ns = root.Name.Namespace;
        var videoSet = root.Descendants(ns + "AdaptationSet")
            .FirstOrDefault(element => string.Equals(element.Attribute("contentType")?.Value,
                "video", StringComparison.OrdinalIgnoreCase));
        var representation = videoSet?.Elements(ns + "Representation").FirstOrDefault();
        var template = representation?.Element(ns + "SegmentTemplate")
                       ?? videoSet?.Element(ns + "SegmentTemplate");
        if (representation is null || template is null)
            return 0;

        var media = template.Attribute("media")?.Value;
        var representationId = representation.Attribute("id")?.Value ?? "";
        if (string.IsNullOrWhiteSpace(media))
            return 0;

        var fileTemplate = media.Replace("$RepresentationID$", representationId, StringComparison.Ordinal);
        var numberToken = Regex.Match(fileTemplate, @"\$Number(?:%0\d+d)?\$");
        if (!numberToken.Success)
            return 0;

        var prefix = fileTemplate[..numberToken.Index];
        var suffix = fileTemplate[(numberToken.Index + numberToken.Length)..];
        var segmentCount = Directory.EnumerateFiles(directory, prefix + "*" + suffix,
                SearchOption.TopDirectoryOnly)
            .Count();
        if (segmentCount == 0)
            return 0;

        var timescale = ReadPositiveLong(template.Attribute("timescale")?.Value, 1);
        var duration = ReadPositiveLong(template.Attribute("duration")?.Value, 0);
        return duration > 0 ? segmentCount * duration / (double)timescale : 0;
    }

    private static long ReadPositiveLong(string? value, long fallback) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;
}
