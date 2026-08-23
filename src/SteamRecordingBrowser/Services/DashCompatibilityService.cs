using System.IO;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SteamRecordingBrowser.Services;

public sealed class DashCompatibilityService
{
    public VideoCodecInfo GetVideoCodec(string mpdPath)
    {
        try
        {
            var doc = XDocument.Load(mpdPath);
            var root = doc.Root;
            if (root is null)
                return VideoCodecInfo.Unknown;

            var ns = root.Name.Namespace;
            var representation = root
                .Descendants(ns + "Representation")
                .FirstOrDefault(element =>
                {
                    var parent = element.Parent;
                    var contentType = parent?.Attribute("contentType")?.Value;
                    var mimeType = element.Attribute("mimeType")?.Value
                                   ?? parent?.Attribute("mimeType")?.Value;
                    return contentType?.Equals("video", StringComparison.OrdinalIgnoreCase) == true ||
                           mimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;
                });

            var codecValue = representation?.Attribute("codecs")?.Value
                             ?? representation?.Parent?.Attribute("codecs")?.Value;
            if (string.IsNullOrWhiteSpace(codecValue))
                return VideoCodecInfo.Unknown;

            var normalized = codecValue.Trim().ToLowerInvariant();
            if (normalized.StartsWith("avc1") || normalized.StartsWith("avc3") || normalized.Contains("h264"))
                return new VideoCodecInfo("H.264", ExportVideoCodec.H264);
            if (normalized.StartsWith("hvc1") || normalized.StartsWith("hev1") || normalized.Contains("hevc") || normalized.Contains("h265"))
                return new VideoCodecInfo("HEVC / H.265", ExportVideoCodec.Hevc);
            if (normalized.StartsWith("av01") || normalized.Contains("av1"))
                return new VideoCodecInfo("AV1", ExportVideoCodec.Av1);

            return new VideoCodecInfo(codecValue, null);
        }
        catch (Exception ex)
        {
            AppLogger.Write($"Could not detect MPD video codec for {mpdPath}: {ex.Message}", "WARN");
            return VideoCodecInfo.Unknown;
        }
    }

    public double GetDurationSeconds(string mpdPath)
    {
        try
        {
            var doc = XDocument.Load(mpdPath);
            var mpd = doc.Root;
            var duration = mpd?.Attribute("mediaPresentationDuration")?.Value;
            return ParseIsoDuration(duration);
        }
        catch (Exception ex)
        {
            AppLogger.Write($"Could not read MPD duration for {mpdPath}: {ex.Message}", "WARN");
            return 0;
        }
    }

    public string GetPlaybackManifest(string sourceMpd)
    {
        try
        {
            var doc = XDocument.Load(sourceMpd, LoadOptions.PreserveWhitespace);
            var root = doc.Root ?? throw new InvalidDataException("MPD has no root element.");
            var ns = root.Name.Namespace;

            var period = root.Elements(ns + "Period").FirstOrDefault();
            if (period is null) return sourceMpd;

            var periodStart = ParseIsoDuration(period.Attribute("start")?.Value);
            if (periodStart <= 0.0001)
                return sourceMpd;

            var duration = root.Attribute("mediaPresentationDuration")?.Value;
            period.SetAttributeValue("start", "PT0S");
            if (!string.IsNullOrWhiteSpace(duration))
                period.SetAttributeValue("duration", duration);

            root.Attribute("timeShiftBufferDepth")?.Remove();

            foreach (var representation in period.Descendants(ns + "Representation"))
            {
                var template = representation.Element(ns + "SegmentTemplate")
                    ?? representation.Parent?.Element(ns + "SegmentTemplate");
                if (template is null) continue;

                var timescale = ReadLong(template.Attribute("timescale")?.Value, 1);
                var startNumber = ReadLong(template.Attribute("startNumber")?.Value, 1);
                var mediaTemplate = template.Attribute("media")?.Value;
                var repId = representation.Attribute("id")?.Value ?? "";

                long presentationOffset;
                var firstSegment = ResolveFirstSegmentPath(
                    System.IO.Path.GetDirectoryName(sourceMpd)!,
                    mediaTemplate,
                    repId,
                    startNumber);

                var tfdt = firstSegment is not null ? TryReadTfdt(firstSegment) : null;
                if (tfdt.HasValue)
                    presentationOffset = tfdt.Value;
                else
                    presentationOffset = (long)Math.Round(periodStart * timescale);

                template.SetAttributeValue("presentationTimeOffset", presentationOffset.ToString(CultureInfo.InvariantCulture));
            }

            var compatibilityPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(sourceMpd)!,
                ".SteamRecordingBrowser_playback.mpd");

            using var writer = new StreamWriter(compatibilityPath, false, new UTF8Encoding(false));
            doc.Save(writer, SaveOptions.DisableFormatting);
            AppLogger.Write($"Created compatibility MPD: {compatibilityPath}");
            return compatibilityPath;
        }
        catch (Exception ex)
        {
            AppLogger.WriteException($"Compatibility MPD generation failed for {sourceMpd}; using original", ex);
            return sourceMpd;
        }
    }

    private static string? ResolveFirstSegmentPath(
        string directory,
        string? template,
        string representationId,
        long startNumber)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;

        var name = template.Replace("$RepresentationID$", representationId, StringComparison.Ordinal);

        name = Regex.Replace(
            name,
            @"\$Number(?:%0(\d+)d)?\$",
            m =>
            {
                if (m.Groups[1].Success &&
                    int.TryParse(m.Groups[1].Value, out var width))
                    return startNumber.ToString("D" + width, CultureInfo.InvariantCulture);

                return startNumber.ToString(CultureInfo.InvariantCulture);
            });

        var path = System.IO.Path.Combine(directory, name);
        return File.Exists(path) ? path : null;
    }

    private static long? TryReadTfdt(string fragment)
    {
        try
        {
            var bytes = File.ReadAllBytes(fragment);

            for (var i = 4; i + 12 <= bytes.Length; i++)
            {
                if (bytes[i] != (byte)'t' || bytes[i + 1] != (byte)'f' ||
                    bytes[i + 2] != (byte)'d' || bytes[i + 3] != (byte)'t')
                    continue;

                var versionOffset = i + 4;
                var dataOffset = versionOffset + 4; // version/flags

                if (bytes[versionOffset] == 1)
                {
                    if (dataOffset + 8 > bytes.Length) return null;
                    return checked((long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(dataOffset, 8)));
                }

                if (dataOffset + 4 > bytes.Length) return null;
                return BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(dataOffset, 4));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Write($"tfdt probe failed for {fragment}: {ex.Message}", "WARN");
        }

        return null;
    }

    private static long ReadLong(string? value, long fallback) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    private static double ParseIsoDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;

        try { return System.Xml.XmlConvert.ToTimeSpan(value).TotalSeconds; }
        catch { return 0; }
    }
}

public sealed record VideoCodecInfo(string DisplayName, ExportVideoCodec? ExportCodec)
{
    public static VideoCodecInfo Unknown { get; } = new("Unknown codec", null);
}
