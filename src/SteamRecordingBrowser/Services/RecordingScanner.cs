using System.IO;
using System.Text.RegularExpressions;
using SteamRecordingBrowser.Models;

namespace SteamRecordingBrowser.Services;

public sealed class RecordingScanner
{
    private readonly SteamService _steam;
    private readonly DashCompatibilityService _dash;
    private readonly MetadataService _metadata;

    public RecordingScanner(SteamService steam, DashCompatibilityService dash, MetadataService metadata)
    {
        _steam = steam;
        _dash = dash;
        _metadata = metadata;
    }

    public async Task<List<RecordingItem>> ScanAsync(
        string root,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Recording folder not found: {root}");

        progress?.Report(new ScanProgress(0, 0, "Resolving Steam game names…"));
        var appNames = _steam.GetInstalledAppNames();

        progress?.Report(new ScanProgress(0, 0, "Finding Steam recordings…"));
        var files = await Task.Run(
            () => Directory
                .EnumerateFiles(root, "session.mpd", SearchOption.AllDirectories)
                .ToList(),
            cancellationToken);

        if (files.Count == 0)
            progress?.Report(new ScanProgress(0, 0, "No recordings found."));

        var results = new List<RecordingItem>(files.Count);

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];

            var item = await Task.Run(() => BuildItem(file, appNames), cancellationToken);
            _metadata.ApplyTo(item);
            results.Add(item);

            progress?.Report(new ScanProgress(index + 1, files.Count, item.GameName));
        }

        return CollapseAutomaticRecordings(results)
            .OrderByDescending(x => x.Timestamp)
            .ToList();
    }

    private RecordingItem BuildItem(string mpdPath, IReadOnlyDictionary<string, string> appNames)
    {
        var directory = new DirectoryInfo(System.IO.Path.GetDirectoryName(mpdPath)!);
        var folder = directory.Name;

        var gameId = "";
        var timestamp = File.GetLastWriteTime(mpdPath);
        var isSavedClip = HasClipMetadata(mpdPath);

        var match = Regex.Match(folder, @"^bg_(\d+)_(\d{8})_(\d{6})$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            gameId = match.Groups[1].Value;
            if (DateTime.TryParseExact(
                match.Groups[2].Value + match.Groups[3].Value,
                "yyyyMMddHHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
            {
                // Steam encodes background-recording folder timestamps in UTC.
                // RecordingItem timestamps are displayed as local wall-clock time.
                timestamp = parsed.ToLocalTime();
            }
        }

        long size = 0;
        try
        {
            size = directory
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (Exception ex)
        {
            AppLogger.Write($"Could not calculate size for {directory.FullName}: {ex.Message}", "WARN");
        }

        var gameName = gameId.Length > 0 && appNames.TryGetValue(gameId, out var name)
            ? name
            : gameId.Length > 0 ? $"App {gameId}" : "Unknown game";

        var durationSeconds = _dash.GetDurationSeconds(mpdPath);
        if (durationSeconds <= 0 && match.Success && !isSavedClip &&
            LiveRecordingService.IsActivelyRecording(mpdPath))
            durationSeconds = LiveRecordingService.GetDynamicDurationSeconds(mpdPath);

        return new RecordingItem
        {
            Path = mpdPath,
            Folder = folder,
            GameId = gameId,
            GameName = gameName,
            Timestamp = timestamp,
            SizeBytes = size,
            DurationSeconds = durationSeconds,
            ThumbnailPath = FindSteamThumbnail(mpdPath),
            CoverArtPath = _steam.FindCachedCoverArt(gameId),
            IsAutoRecording = match.Success && !isSavedClip,
            IsLive = match.Success && !isSavedClip && LiveRecordingService.IsActivelyRecording(mpdPath),
            SessionPaths = new[] { mpdPath },
            SessionStartOffsetsSeconds = new[] { 0d },
            SessionStartTimes = new[] { timestamp }
        };
    }

    private static IEnumerable<RecordingItem> CollapseAutomaticRecordings(IReadOnlyCollection<RecordingItem> items)
    {
        foreach (var clip in items.Where(item => !item.IsAutoRecording))
            yield return clip;

        foreach (var group in items.Where(item => item.IsAutoRecording).GroupBy(item => item.GameId))
        {
            var sessions = group.OrderBy(item => item.Timestamp).ToList();
            var primary = sessions.LastOrDefault(item => item.IsLive) ?? sessions[^1];
            var offsets = new List<double>(sessions.Count);
            var duration = 0d;
            foreach (var session in sessions)
            {
                offsets.Add(duration);
                duration += Math.Max(0, session.DurationSeconds);
            }

            yield return new RecordingItem
            {
                Path = primary.Path,
                Folder = primary.Folder,
                GameId = primary.GameId,
                GameName = primary.GameName,
                Timestamp = sessions[^1].Timestamp,
                SizeBytes = sessions.Sum(session => session.SizeBytes),
                DurationSeconds = duration,
                ThumbnailPath = primary.ThumbnailPath,
                CoverArtPath = primary.CoverArtPath,
                IsAutoRecording = true,
                IsLive = sessions.Any(session => session.IsLive),
                SessionPaths = sessions.Select(session => session.Path).ToArray(),
                SessionStartOffsetsSeconds = offsets,
                SessionStartTimes = sessions.Select(session => session.Timestamp).ToArray(),
                IsFavorite = sessions.Any(session => session.IsFavorite),
                Description = primary.Description,
                Tags = sessions.SelectMany(session => session.Tags)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
        }
    }

    private static bool HasClipMetadata(string recordingPath)
    {
        var directory = new DirectoryInfo(System.IO.Path.GetDirectoryName(recordingPath)!);
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "clip.pb")))
                return true;
            directory = directory.Parent;
        }
        return false;
    }

    public static string? FindSteamThumbnail(string recordingPath)
    {
        try
        {
            var dir = new DirectoryInfo(System.IO.Path.GetDirectoryName(recordingPath)!);

            while (dir is not null)
            {
                var clipPb = System.IO.Path.Combine(dir.FullName, "clip.pb");
                var thumbnail = System.IO.Path.Combine(dir.FullName, "thumbnail.jpg");

                if (File.Exists(clipPb))
                    return File.Exists(thumbnail) ? thumbnail : null;

                dir = dir.Parent;
            }
        }
        catch { }

        return null;
    }
}

public readonly record struct ScanProgress(int Current, int Total, string CurrentGame);
