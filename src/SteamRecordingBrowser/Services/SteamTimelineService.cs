using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamRecordingBrowser.Models;

namespace SteamRecordingBrowser.Services;

public static class SteamTimelineService
{
    private static readonly Regex TimelineFilePattern = new(
        @"\Atimeline_(?<app>\d+)\d{8}_\d{6}\.json\z",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<SteamTimelineEvent> ReadForRecording(
        string recordingPath,
        string gameId,
        DateTime playbackStartTime,
        double durationSeconds,
        IReadOnlyDictionary<string, string>? achievementNames = null)
    {
        if (string.IsNullOrWhiteSpace(recordingPath) ||
            string.IsNullOrWhiteSpace(gameId) ||
            durationSeconds <= 0)
            return Array.Empty<SteamTimelineEvent>();

        var timelineDirectory = FindTimelineDirectory(recordingPath);
        if (timelineDirectory is null)
            return Array.Empty<SteamTimelineEvent>();

        try
        {
            var recordingEnd = playbackStartTime.AddSeconds(durationSeconds);
            return Directory.EnumerateFiles(timelineDirectory, "timeline_*.json", SearchOption.TopDirectoryOnly)
                .Where(path => IsTimelineForGame(path, gameId))
                .SelectMany(path => ReadTimelineFile(path, playbackStartTime, recordingEnd, achievementNames))
                .DistinctBy(value => new
                {
                    value.Type,
                    value.Title,
                    value.Timestamp,
                    value.DurationSeconds,
                    value.ClipPriority
                })
                .OrderBy(value => value.PositionSeconds)
                .ThenByDescending(value => value.Priority)
                .ToArray();
        }
        catch (Exception ex)
        {
            AppLogger.Write($"Could not read Steam timeline metadata for {recordingPath}: {ex.Message}", "DEBUG");
            return Array.Empty<SteamTimelineEvent>();
        }
    }

    private static IEnumerable<SteamTimelineEvent> ReadTimelineFile(
        string path,
        DateTime recordingStart,
        DateTime recordingEnd,
        IReadOnlyDictionary<string, string>? achievementNames)
    {
        JsonDocument document;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            document = JsonDocument.Parse(stream);
        }
        catch (Exception ex)
        {
            AppLogger.Write($"Could not parse Steam timeline {path}: {ex.Message}", "DEBUG");
            yield break;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!TryGetInt64(root, "daterecorded", out var recordedUnixSeconds) ||
                !root.TryGetProperty("entries", out var entriesElement) ||
                entriesElement.ValueKind != JsonValueKind.Array)
                yield break;

            DateTime timelineStart;
            try
            {
                timelineStart = DateTimeOffset.FromUnixTimeSeconds(recordedUnixSeconds).LocalDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                yield break;
            }

            TryGetDouble(root, "endtime", out var timelineEndMilliseconds);
            var entries = entriesElement.EnumerateArray()
                .Select(ParseEntry)
                .Where(entry => entry is not null)
                .Select(entry => entry!)
                .OrderBy(entry => entry.TimeMilliseconds)
                .ToArray();

            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                if (!TryDescribe(entry, achievementNames, out var typeLabel, out var title))
                    continue;

                var durationMilliseconds = Math.Max(0, entry.DurationMilliseconds);
                if (entry.Type.Equals("state_description", StringComparison.OrdinalIgnoreCase))
                {
                    var nextState = entries.Skip(index + 1)
                        .FirstOrDefault(candidate => candidate.Type.Equals(
                            "state_description", StringComparison.OrdinalIgnoreCase));
                    var endMilliseconds = nextState?.TimeMilliseconds ?? timelineEndMilliseconds;
                    durationMilliseconds = Math.Max(0, endMilliseconds - entry.TimeMilliseconds);
                }

                var eventStart = timelineStart.AddMilliseconds(entry.TimeMilliseconds);
                var eventEnd = eventStart.AddMilliseconds(durationMilliseconds);
                var isInstantaneous = durationMilliseconds <= 0;
                if (isInstantaneous
                        ? eventStart < recordingStart || eventStart > recordingEnd
                        : eventEnd <= recordingStart || eventStart >= recordingEnd)
                    continue;

                var clippedStart = eventStart < recordingStart ? recordingStart : eventStart;
                var clippedEnd = isInstantaneous
                    ? clippedStart
                    : eventEnd > recordingEnd ? recordingEnd : eventEnd;
                var possibleClip = Enum.IsDefined(typeof(SteamTimelineClipPriority), entry.PossibleClip)
                    ? (SteamTimelineClipPriority)entry.PossibleClip
                    : SteamTimelineClipPriority.None;

                yield return new SteamTimelineEvent
                {
                    Id = $"{Path.GetFileName(path)}:{entry.Id}",
                    Type = entry.Type,
                    TypeLabel = typeLabel,
                    Title = title,
                    Description = entry.Description,
                    Icon = entry.Icon,
                    Priority = Math.Max(0, entry.Priority),
                    ClipPriority = possibleClip,
                    Timestamp = clippedStart,
                    PositionSeconds = Math.Max(0, (clippedStart - recordingStart).TotalSeconds),
                    DurationSeconds = Math.Max(0, (clippedEnd - clippedStart).TotalSeconds)
                };
            }
        }
    }

    private static TimelineEntry? ParseEntry(JsonElement element)
    {
        var type = GetString(element, "type");
        if (string.IsNullOrWhiteSpace(type) || !TryGetDouble(element, "time", out var timeMilliseconds))
            return null;

        TryGetDouble(element, "duration", out var durationMilliseconds);
        TryGetInt32(element, "priority", out var priority);
        TryGetInt32(element, "possible_clip", out var possibleClip);

        return new TimelineEntry(
            GetString(element, "id"),
            type,
            timeMilliseconds,
            durationMilliseconds,
            GetString(element, "title"),
            GetString(element, "description"),
            GetString(element, "icon"),
            GetString(element, "achievement_name"),
            priority,
            possibleClip);
    }

    private static bool TryDescribe(
        TimelineEntry entry,
        IReadOnlyDictionary<string, string>? achievementNames,
        out string typeLabel,
        out string title)
    {
        typeLabel = "";
        title = "";
        switch (entry.Type.ToLowerInvariant())
        {
            case "event":
                typeLabel = "Event";
                title = entry.Title;
                break;
            case "achievement":
                typeLabel = "Achievement";
                title = string.IsNullOrWhiteSpace(entry.Title) ? entry.AchievementName : entry.Title;
                if (!string.IsNullOrWhiteSpace(entry.AchievementName) &&
                    achievementNames?.TryGetValue(entry.AchievementName, out var displayName) == true &&
                    (string.IsNullOrWhiteSpace(entry.Title) ||
                     entry.Title.Equals(entry.AchievementName, StringComparison.OrdinalIgnoreCase)))
                    title = displayName;
                if (string.IsNullOrWhiteSpace(title))
                    title = "Achievement";
                break;
            case "state_description":
                typeLabel = "Game state";
                title = entry.Title;
                break;
            case "phase":
                typeLabel = "Game phase";
                title = string.IsNullOrWhiteSpace(entry.Title) ? "Game phase" : entry.Title;
                break;
            case "usermarker":
                typeLabel = "User marker";
                title = string.IsNullOrWhiteSpace(entry.Title) ? "User marker" : entry.Title;
                break;
            case "screenshot":
                typeLabel = "Screenshot";
                title = string.IsNullOrWhiteSpace(entry.Title) ? "Screenshot" : entry.Title;
                break;
        }

        return !string.IsNullOrWhiteSpace(title);
    }

    private static string? FindTimelineDirectory(string recordingPath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(recordingPath)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "clip.pb")))
            {
                var clipTimelines = Path.Combine(directory.FullName, "timelines");
                return Directory.Exists(clipTimelines) ? clipTimelines : null;
            }

            if (directory.Name.Equals("video", StringComparison.OrdinalIgnoreCase))
            {
                var timelines = Path.Combine(directory.Parent?.FullName ?? "", "timelines");
                return Directory.Exists(timelines) ? timelines : null;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool IsTimelineForGame(string path, string gameId)
    {
        var match = TimelineFilePattern.Match(Path.GetFileName(path));
        return match.Success && string.Equals(
            match.Groups["app"].Value, gameId, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return "";
        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : property.ToString();
    }

    private static bool TryGetDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
            return false;
        return property.ValueKind == JsonValueKind.Number
            ? property.TryGetDouble(out value)
            : double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetInt32(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
            return false;
        return property.ValueKind == JsonValueKind.Number
            ? property.TryGetInt32(out value)
            : int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetInt64(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
            return false;
        return property.ValueKind == JsonValueKind.Number
            ? property.TryGetInt64(out value)
            : long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private sealed record TimelineEntry(
        string Id,
        string Type,
        double TimeMilliseconds,
        double DurationMilliseconds,
        string Title,
        string Description,
        string Icon,
        string AchievementName,
        int Priority,
        int PossibleClip);
}
