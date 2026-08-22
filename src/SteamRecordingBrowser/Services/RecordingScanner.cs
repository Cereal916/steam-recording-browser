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

        return results.OrderByDescending(x => x.Timestamp).ToList();
    }

    private RecordingItem BuildItem(string mpdPath, IReadOnlyDictionary<string, string> appNames)
    {
        var directory = new DirectoryInfo(System.IO.Path.GetDirectoryName(mpdPath)!);
        var folder = directory.Name;

        var gameId = "";
        var timestamp = File.GetLastWriteTime(mpdPath);

        var match = Regex.Match(folder, @"^bg_(\d+)_(\d{8})_(\d{6})$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            gameId = match.Groups[1].Value;
            if (DateTime.TryParseExact(
                match.Groups[2].Value + match.Groups[3].Value,
                "yyyyMMddHHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var parsed))
            {
                timestamp = parsed;
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

        return new RecordingItem
        {
            Path = mpdPath,
            Folder = folder,
            GameId = gameId,
            GameName = gameName,
            Timestamp = timestamp,
            SizeBytes = size,
            DurationSeconds = _dash.GetDurationSeconds(mpdPath),
            ThumbnailPath = FindSteamThumbnail(mpdPath)
        };
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
