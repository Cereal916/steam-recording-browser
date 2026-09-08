using System.Text.Json;
using SteamRecordingBrowser.Models;
using SteamRecordingBrowser.Services;
using Xunit;

namespace SteamRecordingBrowser.Tests;

public sealed class SteamTimelineServiceTests
{
    private static readonly DateTimeOffset TimelineStart = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);

    [Fact]
    public void ReadForRecording_MapsAndClipsSupportedTimelineEntries()
    {
        using var fixture = new TimelineFixture();
        fixture.WriteTimeline("42", new
        {
            daterecorded = TimelineStart.ToUnixTimeSeconds().ToString(),
            starttime = "0",
            entries = new object[]
            {
                new { id = "1", time = "10000", type = "event", title = "Round won", description = "Blue team", icon = "steam_flag", priority = 50, duration = "0", possible_clip = 2 },
                new { id = "2", time = 20000, type = "state_description", title = "Round 2" },
                new { id = "3", time = "40000", type = "state_description", title = "Round 3" },
                new { id = "4", time = "60000", type = "achievement", achievement_name = "ACH_WIN" },
                new { id = "5", time = "70000", type = "phase", duration = "10000" },
                new { id = "6", time = "80000", type = "gamemode", mode = 1 },
                new { id = "7", time = "200000", type = "screenshot", priority = 1000 }
            },
            endtime = "120000"
        });

        var events = SteamTimelineService.ReadForRecording(
            fixture.ManifestPath,
            "42",
            TimelineStart.LocalDateTime.AddSeconds(5),
            100);

        Assert.Collection(events,
            value =>
            {
                Assert.Equal("Round won", value.Title);
                Assert.Equal(5, value.PositionSeconds);
                Assert.Equal(SteamTimelineClipPriority.Standard, value.ClipPriority);
                Assert.True(value.IsSuggestedClip);
                Assert.Equal(50, value.Priority);
            },
            value =>
            {
                Assert.Equal("Round 2", value.Title);
                Assert.Equal(15, value.PositionSeconds);
                Assert.Equal(20, value.DurationSeconds);
            },
            value =>
            {
                Assert.Equal("Round 3", value.Title);
                Assert.Equal(35, value.PositionSeconds);
                Assert.Equal(65, value.DurationSeconds);
            },
            value =>
            {
                Assert.Equal("ACH_WIN", value.Title);
                Assert.Equal("Achievement", value.TypeLabel);
                Assert.Equal(55, value.PositionSeconds);
            },
            value =>
            {
                Assert.Equal("Game phase", value.Title);
                Assert.Equal(65, value.PositionSeconds);
                Assert.Equal(10, value.DurationSeconds);
            });
    }

    [Fact]
    public void ReadForRecording_UsesTheSavedClipsTimelineCopy()
    {
        using var fixture = new TimelineFixture(savedClip: true);
        fixture.WriteTimeline("42", CreateSingleEvent("Clip event"), useClipDirectory: true);
        fixture.WriteTimeline("42", CreateSingleEvent("Unrelated root event"));

        var events = SteamTimelineService.ReadForRecording(
            fixture.ManifestPath,
            "42",
            TimelineStart.LocalDateTime,
            60);

        Assert.Equal("Clip event", Assert.Single(events).Title);
    }

    [Fact]
    public void ReadForRecording_IgnoresOtherGamesAndMalformedFiles()
    {
        using var fixture = new TimelineFixture();
        fixture.WriteTimeline("7", CreateSingleEvent("Other game"));
        File.WriteAllText(Path.Combine(fixture.RootTimelineDirectory, "timeline_4220330518_033320.json"), "{");

        var events = SteamTimelineService.ReadForRecording(
            fixture.ManifestPath,
            "42",
            TimelineStart.LocalDateTime,
            60);

        Assert.Empty(events);
    }

    [Fact]
    public void ReadForRecording_KeepsUnnamedAchievementsForCounting()
    {
        using var fixture = new TimelineFixture();
        fixture.WriteTimeline("42", new
        {
            daterecorded = TimelineStart.ToUnixTimeSeconds(),
            entries = new[]
            {
                new { id = "1", time = "10000", type = "achievement" }
            },
            endtime = "60000"
        });

        var timelineEvent = Assert.Single(SteamTimelineService.ReadForRecording(
            fixture.ManifestPath,
            "42",
            TimelineStart.LocalDateTime,
            60));

        Assert.Equal("Achievement", timelineEvent.Title);
        Assert.Equal("Achievement", timelineEvent.TypeLabel);
    }

    [Fact]
    public void ReadForRecording_ResolvesAchievementApiNames()
    {
        using var fixture = new TimelineFixture();
        fixture.WriteTimeline("42", new
        {
            daterecorded = TimelineStart.ToUnixTimeSeconds(),
            entries = new[]
            {
                new { id = "1", time = "10000", type = "achievement", achievement_name = "PvE04" }
            },
            endtime = "60000"
        });

        var timelineEvent = Assert.Single(SteamTimelineService.ReadForRecording(
            fixture.ManifestPath,
            "42",
            TimelineStart.LocalDateTime,
            60,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PvE04"] = "Death From Above"
            }));

        Assert.Equal("Death From Above", timelineEvent.Title);
    }

    private static object CreateSingleEvent(string title) => new
    {
        daterecorded = TimelineStart.ToUnixTimeSeconds(),
        entries = new[]
        {
            new { id = "1", time = "10000", type = "event", title, duration = "0" }
        },
        endtime = "60000"
    };

    private sealed class TimelineFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "SteamTimelineServiceTests", Guid.NewGuid().ToString("N"));
        public string RootTimelineDirectory => Path.Combine(Root, "timelines");
        public string ClipTimelineDirectory { get; }
        public string ManifestPath { get; }

        public TimelineFixture(bool savedClip = false)
        {
            var videoRoot = savedClip
                ? Path.Combine(Root, "clips", "clip_42_20330518_033330", "video")
                : Path.Combine(Root, "video");
            var recordingDirectory = Path.Combine(videoRoot, "bg_42_20330518_033325");
            Directory.CreateDirectory(recordingDirectory);
            Directory.CreateDirectory(RootTimelineDirectory);
            ManifestPath = Path.Combine(recordingDirectory, "session.mpd");
            File.WriteAllText(ManifestPath, "<MPD />");

            var clipRoot = Directory.GetParent(videoRoot)!.FullName;
            ClipTimelineDirectory = Path.Combine(clipRoot, "timelines");
            if (savedClip)
            {
                Directory.CreateDirectory(ClipTimelineDirectory);
                File.WriteAllBytes(Path.Combine(clipRoot, "clip.pb"), []);
            }
        }

        public void WriteTimeline(string gameId, object value, bool useClipDirectory = false)
        {
            var directory = useClipDirectory ? ClipTimelineDirectory : RootTimelineDirectory;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"timeline_{gameId}20330518_033320.json");
            File.WriteAllText(path, JsonSerializer.Serialize(value));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
