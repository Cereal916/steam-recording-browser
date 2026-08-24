using System.IO;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SteamRecordingBrowser.Services;

public sealed class DashCompatibilityService
{
    private const double GrowingSnapshotTailMarginSeconds = 3;
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

    public MediaTechnicalInfo GetMediaTechnicalInfo(string mpdPath)
    {
        try
        {
            var root = XDocument.Load(mpdPath).Root;
            if (root is null) return MediaTechnicalInfo.Unknown;
            var ns = root.Name.Namespace;
            var representations = root.Descendants(ns + "Representation").ToList();
            var video = representations.FirstOrDefault(rep => IsMediaType(rep, "video"));
            var audio = representations.FirstOrDefault(rep => IsMediaType(rep, "audio"));
            var videoCodec = FormatCodec(video?.Attribute("codecs")?.Value ?? video?.Parent?.Attribute("codecs")?.Value);
            var audioCodec = FormatCodec(audio?.Attribute("codecs")?.Value ?? audio?.Parent?.Attribute("codecs")?.Value);
            var width = video?.Attribute("width")?.Value;
            var height = video?.Attribute("height")?.Value;
            var resolution = !string.IsNullOrWhiteSpace(width) && !string.IsNullOrWhiteSpace(height)
                ? $"{width}×{height}" : "Unknown";
            var frameRate = FormatFrameRate(video?.Attribute("frameRate")?.Value ?? video?.Parent?.Attribute("frameRate")?.Value);
            var bitrate = long.TryParse(video?.Attribute("bandwidth")?.Value, CultureInfo.InvariantCulture, out var bandwidth)
                ? $"{bandwidth / 1_000_000d:0.##} Mbps" : "Unknown";
            return new MediaTechnicalInfo(videoCodec, audioCodec, resolution, frameRate, bitrate);
        }
        catch (Exception ex)
        {
            AppLogger.Write($"Could not read media details for {mpdPath}: {ex.Message}", "WARN");
            return MediaTechnicalInfo.Unknown;
        }
    }

    private static bool IsMediaType(XElement representation, string type)
    {
        var contentType = representation.Attribute("contentType")?.Value ?? representation.Parent?.Attribute("contentType")?.Value;
        var mimeType = representation.Attribute("mimeType")?.Value ?? representation.Parent?.Attribute("mimeType")?.Value;
        return string.Equals(contentType, type, StringComparison.OrdinalIgnoreCase) ||
               mimeType?.StartsWith(type + "/", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string FormatCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return "Unknown";
        var normalized = codec.Trim().ToLowerInvariant();
        if (normalized.StartsWith("avc1") || normalized.StartsWith("avc3")) return $"H.264 ({codec})";
        if (normalized.StartsWith("hvc1") || normalized.StartsWith("hev1")) return $"HEVC / H.265 ({codec})";
        if (normalized.StartsWith("av01")) return $"AV1 ({codec})";
        if (normalized.StartsWith("mp4a")) return $"AAC ({codec})";
        if (normalized.StartsWith("opus")) return "Opus";
        return codec;
    }

    private static string FormatFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown";
        var parts = value.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], CultureInfo.InvariantCulture, out var denominator) && denominator > 0)
            return $"{numerator / denominator:0.##} fps";
        return double.TryParse(value, CultureInfo.InvariantCulture, out var fps) ? $"{fps:0.##} fps" : value;
    }

    public double GetDurationSeconds(string mpdPath)
    {
        try
        {
            var doc = XDocument.Load(mpdPath);
            var mpd = doc.Root;
            if (string.Equals(mpd?.Attribute("type")?.Value, "dynamic", StringComparison.OrdinalIgnoreCase) &&
                LiveRecordingService.IsActivelyRecording(mpdPath))
            {
                var liveDuration = LiveRecordingService.GetDynamicDurationSeconds(mpdPath);
                if (liveDuration > 0)
                    return liveDuration;
            }
            var duration = mpd?.Attribute("mediaPresentationDuration")?.Value;
            var totalSeconds = ParseIsoDuration(duration);
            var ns = mpd?.Name.Namespace ?? XNamespace.None;
            var periodStart = ParseIsoDuration(mpd?.Elements(ns + "Period").FirstOrDefault()
                ?.Attribute("start")?.Value);
            return GetPlayableDurationSeconds(totalSeconds, periodStart);
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

    public string CreateLiveManifest(string sourceMpd)
    {
        var doc = XDocument.Load(sourceMpd, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidDataException("MPD has no root element.");
        var ns = root.Name.Namespace;
        var period = root.Elements(ns + "Period").FirstOrDefault()
                     ?? throw new InvalidDataException("MPD has no Period element.");
        var sourceDurationSeconds = ParseIsoDuration(root.Attribute("mediaPresentationDuration")?.Value);
        var periodStart = ParseIsoDuration(period.Attribute("start")?.Value);
        var durationSeconds = GetPlayableDurationSeconds(sourceDurationSeconds, periodStart);
        var durationText = durationSeconds > 0
            ? System.Xml.XmlConvert.ToString(TimeSpan.FromSeconds(durationSeconds))
            : null;
        var isActive = LiveRecordingService.IsActivelyRecording(sourceMpd);
        var sourceAvailabilityStart = root.Attribute("availabilityStartTime")?.Value;
        var retainedStartShiftSeconds = 0d;

        period.SetAttributeValue("start", "PT0S");
        if (isActive)
        {
            period.Attribute("duration")?.Remove();
            root.SetAttributeValue("type", "dynamic");
            root.SetAttributeValue("minimumUpdatePeriod", "PT1S");
            root.SetAttributeValue("suggestedPresentationDelay", "PT3S");
            root.SetAttributeValue("timeShiftBufferDepth", "PT2H");
            root.SetAttributeValue("publishTime", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            if (string.IsNullOrWhiteSpace(sourceAvailabilityStart))
            {
                root.SetAttributeValue("availabilityStartTime",
                    DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(Math.Max(1, durationSeconds)))
                        .ToString("O", CultureInfo.InvariantCulture));
            }
            root.Attribute("mediaPresentationDuration")?.Remove();
        }
        else
        {
            root.SetAttributeValue("type", "static");
            if (!string.IsNullOrWhiteSpace(durationText))
                period.SetAttributeValue("duration", durationText);
            root.Attribute("minimumUpdatePeriod")?.Remove();
            root.Attribute("suggestedPresentationDelay")?.Remove();
            root.Attribute("publishTime")?.Remove();
            root.Attribute("availabilityStartTime")?.Remove();
        }

        var representations = period.Descendants(ns + "Representation").ToArray();
        long? alignedActiveStartNumber = null;
        if (isActive)
        {
            var availableStarts = new List<long>();
            foreach (var representation in representations)
            {
                var template = representation.Element(ns + "SegmentTemplate")
                    ?? representation.Parent?.Element(ns + "SegmentTemplate");
                if (template is null) continue;

                var declaredStart = ReadLong(template.Attribute("startNumber")?.Value, 1);
                var mediaTemplate = template.Attribute("media")?.Value;
                var repId = representation.Attribute("id")?.Value ?? "";
                var directory = System.IO.Path.GetDirectoryName(sourceMpd)!;
                var availableStart = ResolveFirstSegmentPath(
                    directory, mediaTemplate, repId, declaredStart) is not null
                    ? declaredStart
                    : FindFirstAvailableSegmentNumber(directory, mediaTemplate, repId, declaredStart);
                if (availableStart.HasValue)
                    availableStarts.Add(availableStart.Value);
            }

            if (availableStarts.Count > 0)
            {
                var commonStart = availableStarts.Max();
                // Leave enough distance from Steam's rolling deletion edge for
                // libVLC to initialize both tracks before fragments disappear.
                const long rollingPruneSafetySegments = 20;
                var safeStart = commonStart + rollingPruneSafetySegments;
                var directory = System.IO.Path.GetDirectoryName(sourceMpd)!;
                var allHaveSafeStart = representations.All(representation =>
                {
                    var template = representation.Element(ns + "SegmentTemplate")
                        ?? representation.Parent?.Element(ns + "SegmentTemplate");
                    if (template is null) return true;
                    return ResolveFirstSegmentPath(directory, template.Attribute("media")?.Value,
                        representation.Attribute("id")?.Value ?? "", safeStart) is not null;
                });
                alignedActiveStartNumber = allHaveSafeStart ? safeStart : commonStart;
            }
        }

        foreach (var representation in representations)
        {
            var template = representation.Element(ns + "SegmentTemplate")
                ?? representation.Parent?.Element(ns + "SegmentTemplate");
            if (template is null) continue;

            var timescale = ReadLong(template.Attribute("timescale")?.Value, 1);
            var startNumber = ReadLong(template.Attribute("startNumber")?.Value, 1);
            var declaredStartNumber = startNumber;
            var mediaTemplate = template.Attribute("media")?.Value;
            var repId = representation.Attribute("id")?.Value ?? "";
            var directory = System.IO.Path.GetDirectoryName(sourceMpd)!;
            if (alignedActiveStartNumber.HasValue && startNumber < alignedActiveStartNumber.Value)
            {
                startNumber = alignedActiveStartNumber.Value;
                template.SetAttributeValue("startNumber", startNumber.ToString(CultureInfo.InvariantCulture));
                var segmentDuration = ReadLong(template.Attribute("duration")?.Value, 0);
                if (segmentDuration > 0 && timescale > 0)
                {
                    retainedStartShiftSeconds = Math.Max(retainedStartShiftSeconds,
                        (startNumber - declaredStartNumber) * segmentDuration / (double)timescale);
                }
                AppLogger.Write(
                    $"Aligned live DASH representation {repId} to common startNumber={startNumber}. " +
                    $"declared={declaredStartNumber} source={sourceMpd}",
                    "DEBUG");
            }
            var declaredFirstSegment = ResolveFirstSegmentPath(
                directory, mediaTemplate, repId, startNumber);
            if (declaredFirstSegment is null)
            {
                var availableStart = FindFirstAvailableSegmentNumber(
                    directory, mediaTemplate, repId, startNumber);
                if (availableStart.HasValue)
                {
                    var safeStart = availableStart.Value;
                    if (isActive && !alignedActiveStartNumber.HasValue)
                    {
                        // Steam deletes the oldest fragments while the bridge
                        // is serving its manifest. Stay several segments ahead
                        // of the pruning edge so the advertised first file is
                        // still present when libVLC asks for it.
                        const long rollingPruneSafetySegments = 20;
                        var candidate = availableStart.Value + rollingPruneSafetySegments;
                        if (ResolveFirstSegmentPath(directory, mediaTemplate, repId, candidate) is not null)
                            safeStart = candidate;
                    }
                    AppLogger.Write(
                        $"Adjusted stale DASH startNumber for representation {repId}: " +
                        $"declared={startNumber} available={availableStart.Value} safe={safeStart} source={sourceMpd}",
                        "DEBUG");
                    startNumber = safeStart;
                    template.SetAttributeValue("startNumber",
                        startNumber.ToString(CultureInfo.InvariantCulture));
                    var segmentDuration = ReadLong(template.Attribute("duration")?.Value, 0);
                    if (segmentDuration > 0 && timescale > 0)
                    {
                        retainedStartShiftSeconds = Math.Max(retainedStartShiftSeconds,
                            (startNumber - declaredStartNumber) * segmentDuration / (double)timescale);
                    }
                }
            }
            var firstSegment = ResolveFirstSegmentPath(
                directory, mediaTemplate, repId, startNumber);
            var tfdt = firstSegment is not null ? TryReadTfdt(firstSegment) : null;
            var presentationOffset = tfdt ?? (long)Math.Round(periodStart * timescale);
            template.SetAttributeValue("presentationTimeOffset",
                presentationOffset.ToString(CultureInfo.InvariantCulture));
        }

        if (isActive && retainedStartShiftSeconds > 0 &&
            DateTime.TryParse(sourceAvailabilityStart, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var availabilityStart))
        {
            var adjustedAvailability = availabilityStart.ToUniversalTime()
                .AddSeconds(retainedStartShiftSeconds);
            root.SetAttributeValue("availabilityStartTime",
                adjustedAvailability.ToString("O", CultureInfo.InvariantCulture));
            AppLogger.Write(
                $"Shifted live DASH availability by {retainedStartShiftSeconds:F3}s to match retained segments. " +
                $"source={sourceMpd}",
                "DEBUG");
        }

        using var writer = new Utf8StringWriter();
        doc.Save(writer, SaveOptions.DisableFormatting);
        return writer.ToString();
    }

    private static long? FindFirstAvailableSegmentNumber(
        string directory,
        string? template,
        string representationId,
        long minimumNumber)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;

        var name = template.Replace("$RepresentationID$", representationId, StringComparison.Ordinal);
        var numberToken = Regex.Match(name, @"\$Number(?:%0\d+d)?\$");
        if (!numberToken.Success) return null;

        var prefix = name[..numberToken.Index];
        var suffix = name[(numberToken.Index + numberToken.Length)..];
        var pattern = "^" + Regex.Escape(prefix) + @"(?<number>\d+)" + Regex.Escape(suffix) + "$";
        var filePattern = prefix + "*" + suffix;

        return Directory.EnumerateFiles(directory, filePattern, SearchOption.TopDirectoryOnly)
            .Select(System.IO.Path.GetFileName)
            .Where(fileName => fileName is not null)
            .Select(fileName => Regex.Match(fileName!, pattern))
            .Where(match => match.Success && long.TryParse(match.Groups["number"].Value,
                NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(match => long.Parse(match.Groups["number"].Value,
                NumberStyles.None, CultureInfo.InvariantCulture))
            .Where(number => number >= minimumNumber)
            .DefaultIfEmpty()
            .Min() is var first && first > 0 ? first : null;
    }

    public string CreateCombinedManifest(IReadOnlyList<string> sourceMpds, bool staticSnapshot = false)
    {
        if (sourceMpds.Count == 0)
            throw new ArgumentException("At least one recording session is required.", nameof(sourceMpds));

        var sessionDocuments = sourceMpds
            .Select(path => XDocument.Parse(CreateLiveManifest(path)))
            .ToArray();
        var root = new XElement(sessionDocuments[0].Root
                                ?? throw new InvalidDataException("MPD has no root element."));
        var ns = root.Name.Namespace;
        root.Elements(ns + "Period").Remove();

        var durations = sourceMpds.Select(GetDurationSeconds).ToArray();
        var totalDuration = durations.Sum();
        var activeIndex = Array.FindLastIndex(sourceMpds.ToArray(), LiveRecordingService.IsActivelyRecording);
        var anyActive = activeIndex >= 0;
        var emitDynamic = anyActive && !staticSnapshot;
        if (staticSnapshot && activeIndex >= 0)
        {
            // Steam may still be writing the newest three-second fragment.
            // Keep it outside the finite snapshot so every advertised point
            // on libVLC's timeline maps to a complete segment.
            durations[activeIndex] = Math.Max(0,
                durations[activeIndex] - GrowingSnapshotTailMarginSeconds);
            totalDuration = durations.Sum();
        }
        var start = 0d;

        for (var index = 0; index < sessionDocuments.Length; index++)
        {
            var sourcePeriod = sessionDocuments[index].Root?.Elements(ns + "Period").FirstOrDefault()
                               ?? throw new InvalidDataException("MPD has no Period element.");
            var period = new XElement(sourcePeriod);
            period.SetAttributeValue("id", $"session-{index}");
            period.SetAttributeValue("start", System.Xml.XmlConvert.ToString(TimeSpan.FromSeconds(start)));
            if (durations[index] > 0 && (!emitDynamic || index != activeIndex))
                period.SetAttributeValue("duration", System.Xml.XmlConvert.ToString(TimeSpan.FromSeconds(durations[index])));
            else if (emitDynamic && index == activeIndex)
                period.Attribute("duration")?.Remove();

            foreach (var template in period.Descendants(ns + "SegmentTemplate"))
            {
                foreach (var attributeName in new[] { "initialization", "media" })
                {
                    var attribute = template.Attribute(attributeName);
                    if (attribute is not null)
                        attribute.Value = $"s{index}/{attribute.Value}";
                }
            }

            root.Add(period);
            start += durations[index];
        }

        if (emitDynamic)
        {
            root.SetAttributeValue("type", "dynamic");
            root.SetAttributeValue("minimumUpdatePeriod", "PT1S");
            root.SetAttributeValue("suggestedPresentationDelay", "PT3S");
            root.SetAttributeValue("timeShiftBufferDepth", System.Xml.XmlConvert.ToString(TimeSpan.FromSeconds(Math.Max(totalDuration, 1))));
            root.SetAttributeValue("publishTime", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            var activeRoot = sessionDocuments[activeIndex].Root;
            var activeAvailabilityText = activeRoot?.Attribute("availabilityStartTime")?.Value;
            var precedingDuration = durations.Take(activeIndex).Sum();
            var combinedAvailability = DateTime.TryParse(activeAvailabilityText, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var activeAvailability)
                ? activeAvailability.ToUniversalTime().Subtract(TimeSpan.FromSeconds(precedingDuration))
                : DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(Math.Max(totalDuration, 1)));
            root.SetAttributeValue("availabilityStartTime",
                combinedAvailability.ToString("O", CultureInfo.InvariantCulture));
            root.Attribute("mediaPresentationDuration")?.Remove();
        }
        else
        {
            root.SetAttributeValue("type", "static");
            root.SetAttributeValue("mediaPresentationDuration", System.Xml.XmlConvert.ToString(TimeSpan.FromSeconds(totalDuration)));
            root.Attribute("minimumUpdatePeriod")?.Remove();
            root.Attribute("suggestedPresentationDelay")?.Remove();
            root.Attribute("publishTime")?.Remove();
            root.Attribute("availabilityStartTime")?.Remove();
            root.Attribute("timeShiftBufferDepth")?.Remove();
        }

        using var writer = new Utf8StringWriter();
        new XDocument(root).Save(writer, SaveOptions.DisableFormatting);
        return writer.ToString();
    }

    public string CreateBridgedLiveManifest(string sourceMpd, int routeIndex)
    {
        var doc = XDocument.Parse(CreateLiveManifest(sourceMpd));
        var root = doc.Root ?? throw new InvalidDataException("MPD has no root element.");
        var ns = root.Name.Namespace;
        foreach (var template in root.Descendants(ns + "SegmentTemplate"))
        {
            foreach (var attributeName in new[] { "initialization", "media" })
            {
                var attribute = template.Attribute(attributeName);
                if (attribute is not null)
                    attribute.Value = $"s{routeIndex}/{attribute.Value}";
            }
        }

        using var writer = new Utf8StringWriter();
        doc.Save(writer, SaveOptions.DisableFormatting);
        return writer.ToString();
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

    private static double GetPlayableDurationSeconds(double presentationDuration, double periodStart)
    {
        if (presentationDuration <= 0)
            return 0;

        // Steam's finalized rolling manifests retain the original presentation
        // offset even though mediaPresentationDuration already describes only
        // the playable retained window. Subtract only for conventional MPDs
        // where the period actually fits inside the presentation duration.
        return periodStart > 0 && periodStart < presentationDuration
            ? presentationDuration - periodStart
            : presentationDuration;
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}

public sealed record VideoCodecInfo(string DisplayName, ExportVideoCodec? ExportCodec)
{
    public static VideoCodecInfo Unknown { get; } = new("Unknown codec", null);
}

public sealed record MediaTechnicalInfo(string VideoCodec, string AudioCodec, string Resolution, string FrameRate, string Bitrate)
{
    public static MediaTechnicalInfo Unknown { get; } = new("Unknown", "Unknown", "Unknown", "Unknown", "Unknown");
}
