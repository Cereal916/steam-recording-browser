using System.IO;
using System.Text.RegularExpressions;
using SteamRecordingBrowser.Models;

namespace SteamRecordingBrowser.Services;

public static class StorageDashboardService
{
    private static readonly Regex RecordingFolderPattern = new(
        @"\A(?<type>[bf]g)_(?<app>\d+)_(?<date>\d{8})_(?<time>\d{6})(?:_\d+)?\z",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static StorageDashboardReport Build(IEnumerable<RecordingItem> recordings)
    {
        ArgumentNullException.ThrowIfNull(recordings);

        var items = recordings.ToArray();
        var overlapByItem = CalculateOverlaps(items);
        var gameRows = items
            .GroupBy(item => item.GameId ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildGameSummary(group, overlapByItem))
            .OrderByDescending(row => row.TotalBytes)
            .ThenBy(row => row.GameName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var saved = items.Where(item => item.IsSavedClip).ToArray();
        var rolling = items.Where(item => item.IsAutoRecording).ToArray();
        var overlapRows = overlapByItem.Values.Where(overlap => overlap.DurationSeconds > 0).ToArray();

        return new StorageDashboardReport
        {
            TotalBytes = items.Sum(item => Math.Max(0, item.SizeBytes)),
            SavedClipBytes = saved.Sum(item => Math.Max(0, item.SizeBytes)),
            RollingRecordingBytes = rolling.Sum(item => Math.Max(0, item.SizeBytes)),
            OverlappingDurationSeconds = overlapRows.Sum(overlap => overlap.DurationSeconds),
            SavedClipCount = saved.Length,
            RollingSessionCount = rolling.Sum(GetSessionCount),
            OverlappingClipCount = overlapRows.Length,
            Games = gameRows,
            OldestRecordings = items
                .OrderBy(item => item.Timestamp)
                .ThenBy(item => item.GameName, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => new StorageRecordingSummary
                {
                    GameName = item.GameName,
                    RecordingType = item.IsAutoRecording ? "Rolling recording" : "Saved clip",
                    Timestamp = item.Timestamp,
                    SizeBytes = Math.Max(0, item.SizeBytes),
                    DurationSeconds = Math.Max(0, item.DurationSeconds)
                })
                .ToArray()
        };
    }

    private static StorageGameSummary BuildGameSummary(
        IGrouping<string, RecordingItem> group,
        IReadOnlyDictionary<RecordingItem, StorageOverlap> overlapByItem)
    {
        var items = group.ToArray();
        var saved = items.Where(item => item.IsSavedClip).ToArray();
        var rolling = items.Where(item => item.IsAutoRecording).ToArray();
        var overlaps = saved
            .Select(item => overlapByItem.GetValueOrDefault(item))
            .Where(overlap => overlap.DurationSeconds > 0)
            .ToArray();

        return new StorageGameSummary
        {
            GameId = group.Key,
            GameName = items.Select(item => item.GameName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "Unknown game",
            TotalBytes = items.Sum(item => Math.Max(0, item.SizeBytes)),
            SavedClipBytes = saved.Sum(item => Math.Max(0, item.SizeBytes)),
            RollingRecordingBytes = rolling.Sum(item => Math.Max(0, item.SizeBytes)),
            OverlappingDurationSeconds = overlaps.Sum(overlap => overlap.DurationSeconds),
            SavedClipCount = saved.Length,
            RollingSessionCount = rolling.Sum(GetSessionCount),
            OverlappingClipCount = overlaps.Length,
            OldestTimestamp = items.Min(item => item.Timestamp)
        };
    }

    private static Dictionary<RecordingItem, StorageOverlap> CalculateOverlaps(
        IReadOnlyCollection<RecordingItem> items)
    {
        var rollingSessions = items
            .Where(item => item.IsAutoRecording)
            .SelectMany(ExpandRollingSessions)
            .Where(session => session.Identity is not null && session.DurationSeconds > 0)
            .GroupBy(session => new StorageSessionKey(session.GameId, session.Identity!), StorageSessionKeyComparer.Instance)
            .ToDictionary(group => group.Key, group => group.ToArray(), StorageSessionKeyComparer.Instance);

        var result = new Dictionary<RecordingItem, StorageOverlap>();
        foreach (var clip in items.Where(item => item.IsSavedClip))
        {
            var identity = GetRecordingIdentity(clip.Path);
            if (identity is null || clip.DurationSeconds <= 0)
            {
                result[clip] = default;
                continue;
            }

            var key = new StorageSessionKey(clip.GameId ?? "", identity);
            if (!rollingSessions.TryGetValue(key, out var sessions))
            {
                result[clip] = default;
                continue;
            }

            var clipStart = GetPlaybackStart(clip);
            var clipEnd = clipStart.AddSeconds(clip.DurationSeconds);
            var intersections = sessions
                .Select(session => Intersect(clipStart, clipEnd, session.Start, session.End))
                .Where(interval => interval.End > interval.Start)
                .OrderBy(interval => interval.Start)
                .ToArray();
            var overlapSeconds = GetUnionDurationSeconds(intersections);
            result[clip] = new StorageOverlap(overlapSeconds);
        }

        return result;
    }

    private static IEnumerable<StorageSession> ExpandRollingSessions(RecordingItem item)
    {
        var count = GetSessionCount(item);
        var fallbackDuration = count > 0 ? Math.Max(0, item.DurationSeconds) / count : 0;
        var cumulativeDuration = 0d;

        for (var index = 0; index < count; index++)
        {
            var path = GetAt(item.SessionPaths, index) ?? (index == 0 ? item.Path : "");
            var duration = GetAt(item.SessionDurationsSeconds, index) ?? fallbackDuration;
            var start = GetAt(item.SessionPlaybackStartTimes, index);
            if (start is null || start == default)
            {
                var recordedStart = GetAt(item.SessionStartTimes, index);
                start = recordedStart is not null && recordedStart != default
                    ? recordedStart
                    : GetPlaybackStart(item).AddSeconds(cumulativeDuration);
            }

            yield return new StorageSession(
                item.GameId ?? "",
                GetRecordingIdentity(path),
                start.Value,
                start.Value.AddSeconds(Math.Max(0, duration)));
            cumulativeDuration += Math.Max(0, duration);
        }
    }

    private static int GetSessionCount(RecordingItem item) =>
        Math.Max(1, item.SessionPaths.Count);

    private static DateTime GetPlaybackStart(RecordingItem item) =>
        item.PlaybackStartTime == default ? item.Timestamp : item.PlaybackStartTime;

    private static string? GetRecordingIdentity(string path)
    {
        try
        {
            var folder = Path.GetFileName(Path.GetDirectoryName(path));
            var match = RecordingFolderPattern.Match(folder ?? "");
            if (!match.Success) return null;
            return $"{match.Groups["type"].Value.ToLowerInvariant()}:" +
                   $"{match.Groups["app"].Value}:" +
                   $"{match.Groups["date"].Value}:" +
                   match.Groups["time"].Value;
        }
        catch
        {
            return null;
        }
    }

    private static StorageInterval Intersect(
        DateTime firstStart,
        DateTime firstEnd,
        DateTime secondStart,
        DateTime secondEnd) =>
        new(firstStart > secondStart ? firstStart : secondStart,
            firstEnd < secondEnd ? firstEnd : secondEnd);

    private static double GetUnionDurationSeconds(IReadOnlyList<StorageInterval> intervals)
    {
        if (intervals.Count == 0) return 0;

        var total = 0d;
        var start = intervals[0].Start;
        var end = intervals[0].End;
        foreach (var interval in intervals.Skip(1))
        {
            if (interval.Start <= end)
            {
                if (interval.End > end) end = interval.End;
                continue;
            }

            total += (end - start).TotalSeconds;
            start = interval.Start;
            end = interval.End;
        }

        return total + (end - start).TotalSeconds;
    }

    private static T? GetAt<T>(IReadOnlyList<T> values, int index) where T : struct =>
        index >= 0 && index < values.Count ? values[index] : null;

    private static string? GetAt(IReadOnlyList<string> values, int index) =>
        index >= 0 && index < values.Count ? values[index] : null;

    private readonly record struct StorageOverlap(double DurationSeconds);
    private readonly record struct StorageSessionKey(string GameId, string Identity);
    private readonly record struct StorageSession(string GameId, string? Identity, DateTime Start, DateTime End)
    {
        public double DurationSeconds => Math.Max(0, (End - Start).TotalSeconds);
    }
    private readonly record struct StorageInterval(DateTime Start, DateTime End);

    private sealed class StorageSessionKeyComparer : IEqualityComparer<StorageSessionKey>
    {
        public static StorageSessionKeyComparer Instance { get; } = new();

        public bool Equals(StorageSessionKey x, StorageSessionKey y) =>
            string.Equals(x.GameId, y.GameId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Identity, y.Identity, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(StorageSessionKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.GameId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Identity));
    }
}
