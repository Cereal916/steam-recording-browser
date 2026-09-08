using SteamRecordingBrowser.Models;
using SteamRecordingBrowser.Services;
using Xunit;

namespace SteamRecordingBrowser.Tests;

public sealed class StorageDashboardServiceTests
{
    private static readonly DateTime SessionStart = new(2026, 9, 8, 10, 0, 0, DateTimeKind.Local);

    [Fact]
    public void Build_SummarizesStorageByTypeAndGame()
    {
        var rolling = CreateRecording(
            "Arc Raiders",
            "1808500",
            @"C:\recordings\video\bg_1808500_20260908_170000\session.mpd",
            SessionStart,
            durationSeconds: 120,
            sizeBytes: 1_200,
            isAutoRecording: true);
        var clip = CreateRecording(
            "Arc Raiders",
            "1808500",
            @"C:\recordings\clips\clip_1808500_20260908_171000\video\bg_1808500_20260908_170000\session.mpd",
            SessionStart.AddSeconds(20),
            durationSeconds: 40,
            sizeBytes: 400);
        var otherClip = CreateRecording(
            "Portal 2",
            "620",
            @"C:\recordings\clips\clip_620_20260908_180000\video\bg_620_20260908_173000\session.mpd",
            SessionStart.AddHours(1),
            durationSeconds: 20,
            sizeBytes: 200);

        var report = StorageDashboardService.Build([rolling, clip, otherClip]);

        Assert.Equal(1_800, report.TotalBytes);
        Assert.Equal(600, report.SavedClipBytes);
        Assert.Equal(1_200, report.RollingRecordingBytes);
        Assert.Equal(40, report.OverlappingDurationSeconds);
        Assert.Equal(2, report.SavedClipCount);
        Assert.Equal(1, report.RollingSessionCount);
        Assert.Equal(1, report.OverlappingClipCount);
        Assert.Equal(new[] { "Arc Raiders", "Portal 2" }, report.Games.Select(game => game.GameName));
    }

    [Fact]
    public void Build_CountsOnlyTheClipRangeRetainedByTheRollingRecording()
    {
        var rolling = CreateRecording(
            "Test game",
            "42",
            @"C:\recordings\video\bg_42_20260908_170000\session.mpd",
            SessionStart,
            durationSeconds: 100,
            sizeBytes: 1_000,
            isAutoRecording: true);
        var clip = CreateRecording(
            "Test game",
            "42",
            @"C:\recordings\clips\clip_42_20260908_171000\video\bg_42_20260908_170000\session.mpd",
            SessionStart.AddSeconds(80),
            durationSeconds: 40,
            sizeBytes: 400);

        var report = StorageDashboardService.Build([rolling, clip]);

        Assert.Equal(20, report.OverlappingDurationSeconds);
    }

    [Fact]
    public void Build_MergesSplitRollingRangesBeforeCountingOverlap()
    {
        var rolling = CreateRecording(
            "Test game",
            "42",
            @"C:\recordings\video\bg_42_20260908_170000_0\session.mpd",
            SessionStart,
            durationSeconds: 50,
            sizeBytes: 1_000,
            isAutoRecording: true,
            sessionPaths:
            [
                @"C:\recordings\video\bg_42_20260908_170000_0\session.mpd",
                @"C:\recordings\video\bg_42_20260908_170000_1\session.mpd"
            ],
            sessionStarts: [SessionStart, SessionStart.AddSeconds(20)],
            sessionDurations: [30, 30],
            sessionSizes: [500, 500]);
        var clip = CreateRecording(
            "Test game",
            "42",
            @"C:\recordings\clips\clip_42_20260908_171000\video\bg_42_20260908_170000\session.mpd",
            SessionStart,
            durationSeconds: 50,
            sizeBytes: 500);

        var report = StorageDashboardService.Build([rolling, clip]);

        Assert.Equal(50, report.OverlappingDurationSeconds);
        Assert.Equal(2, report.RollingSessionCount);
    }

    [Theory]
    [InlineData(@"C:\recordings\video\bg_42_20260908_180000\session.mpd", "42")]
    [InlineData(@"C:\recordings\video\bg_42_20260908_170000\session.mpd", "43")]
    [InlineData(@"C:\recordings\video\fg_42_20260908_170000\session.mpd", "42")]
    public void Build_DoesNotClaimOverlapForADifferentRecording(string rollingPath, string rollingGameId)
    {
        var rolling = CreateRecording(
            "Test game",
            rollingGameId,
            rollingPath,
            SessionStart,
            durationSeconds: 100,
            sizeBytes: 1_000,
            isAutoRecording: true);
        var clip = CreateRecording(
            "Test game",
            "42",
            @"C:\recordings\clips\clip_42_20260908_171000\video\bg_42_20260908_170000\session.mpd",
            SessionStart.AddSeconds(10),
            durationSeconds: 20,
            sizeBytes: 200);

        var report = StorageDashboardService.Build([rolling, clip]);

        Assert.Equal(0, report.OverlappingDurationSeconds);
        Assert.Equal(0, report.OverlappingClipCount);
    }

    [Fact]
    public void Build_OrdersOldestRecordingsFirst()
    {
        var newer = CreateRecording(
            "Newer",
            "2",
            @"C:\recordings\clips\clip_2_20260908_180000\video\bg_2_20260908_180000\session.mpd",
            SessionStart.AddHours(1),
            10,
            100);
        var older = CreateRecording(
            "Older",
            "1",
            @"C:\recordings\clips\clip_1_20260908_170000\video\bg_1_20260908_170000\session.mpd",
            SessionStart,
            10,
            100);

        var report = StorageDashboardService.Build([newer, older]);

        Assert.Equal(new[] { "Older", "Newer" }, report.OldestRecordings.Select(item => item.GameName));
    }

    [Fact]
    public void Build_RejectsNullCollections()
    {
        Assert.Throws<ArgumentNullException>(() => StorageDashboardService.Build(null!));
    }

    private static RecordingItem CreateRecording(
        string gameName,
        string gameId,
        string path,
        DateTime playbackStart,
        double durationSeconds,
        long sizeBytes,
        bool isAutoRecording = false,
        IReadOnlyList<string>? sessionPaths = null,
        IReadOnlyList<DateTime>? sessionStarts = null,
        IReadOnlyList<double>? sessionDurations = null,
        IReadOnlyList<long>? sessionSizes = null) => new()
    {
        Path = path,
        Folder = Path.GetFileName(Path.GetDirectoryName(path))!,
        GameId = gameId,
        GameName = gameName,
        Timestamp = playbackStart,
        PlaybackStartTime = playbackStart,
        SizeBytes = sizeBytes,
        DurationSeconds = durationSeconds,
        IsAutoRecording = isAutoRecording,
        SessionPaths = sessionPaths ?? [path],
        SessionPlaybackStartTimes = sessionStarts ?? [playbackStart],
        SessionDurationsSeconds = sessionDurations ?? [durationSeconds],
        SessionSizesBytes = sessionSizes ?? [sizeBytes]
    };
}
