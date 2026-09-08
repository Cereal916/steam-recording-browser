namespace SteamRecordingBrowser.Models;

public sealed class StorageDashboardReport
{
    public required long TotalBytes { get; init; }
    public required long SavedClipBytes { get; init; }
    public required long RollingRecordingBytes { get; init; }
    public required double OverlappingDurationSeconds { get; init; }
    public required int SavedClipCount { get; init; }
    public required int RollingSessionCount { get; init; }
    public required int OverlappingClipCount { get; init; }
    public required IReadOnlyList<StorageGameSummary> Games { get; init; }
    public required IReadOnlyList<StorageRecordingSummary> OldestRecordings { get; init; }

    public string TotalSizeText => RecordingItem.FormatBytes(TotalBytes);
    public string GameCountText => FormatCount(Games.Count, "game", "games");
    public string SavedClipSizeText => RecordingItem.FormatBytes(SavedClipBytes);
    public string RollingRecordingSizeText => RecordingItem.FormatBytes(RollingRecordingBytes);
    public string SavedClipCountText => FormatCount(SavedClipCount, "saved clip", "saved clips");
    public string RollingSessionCountText => FormatCount(RollingSessionCount, "rolling session", "rolling sessions");
    public string OverlapDurationText => RecordingItem.FormatDuration(OverlappingDurationSeconds);
    public string OverlapSummaryText => OverlappingClipCount == 0
        ? "No saved clips overlap retained rolling footage"
        : $"{RecordingItem.FormatDuration(OverlappingDurationSeconds)} across " +
          FormatCount(OverlappingClipCount, "saved clip", "saved clips");

    private static string FormatCount(int count, string singular, string plural) =>
        $"{count:N0} {(count == 1 ? singular : plural)}";
}

public sealed class StorageGameSummary
{
    public required string GameId { get; init; }
    public required string GameName { get; init; }
    public required long TotalBytes { get; init; }
    public required long SavedClipBytes { get; init; }
    public required long RollingRecordingBytes { get; init; }
    public required double OverlappingDurationSeconds { get; init; }
    public required int SavedClipCount { get; init; }
    public required int RollingSessionCount { get; init; }
    public required int OverlappingClipCount { get; init; }
    public required DateTime OldestTimestamp { get; init; }

    public string TotalSizeText => RecordingItem.FormatBytes(TotalBytes);
    public string SavedClipSizeText => RecordingItem.FormatBytes(SavedClipBytes);
    public string RollingRecordingSizeText => RecordingItem.FormatBytes(RollingRecordingBytes);
    public string OverlapDurationText => OverlappingDurationSeconds > 0
        ? RecordingItem.FormatDuration(OverlappingDurationSeconds)
        : "—";
    public string RecordingCountText => $"{SavedClipCount:N0} saved • {RollingSessionCount:N0} rolling";
    public string OldestDateText => OldestTimestamp == default ? "Unknown" : OldestTimestamp.ToString("MMM d, yyyy");
}

public sealed class StorageRecordingSummary
{
    public required string GameName { get; init; }
    public required string RecordingType { get; init; }
    public required DateTime Timestamp { get; init; }
    public required long SizeBytes { get; init; }
    public required double DurationSeconds { get; init; }

    public string TimestampText => Timestamp == default ? "Unknown date" : Timestamp.ToString("MMM d, yyyy  h:mm tt");
    public string SizeText => RecordingItem.FormatBytes(SizeBytes);
    public string DurationText => RecordingItem.FormatDuration(DurationSeconds);
}
