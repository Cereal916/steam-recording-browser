using SteamRecordingBrowser.Models;
using Xunit;

namespace SteamRecordingBrowser.Tests;

public sealed class RecordingItemAchievementTests
{
    [Fact]
    public void AchievementProperties_ExposeDistinctNamesAndEventCount()
    {
        var item = CreateItem(
            CreateEvent("achievement", "First win"),
            CreateEvent("Achievement", "First win"),
            CreateEvent("achievement", "ACH_PERFECT_ROUND"),
            CreateEvent("event", "Round started"));

        Assert.True(item.HasAchievements);
        Assert.Equal(3, item.AchievementCount);
        Assert.Equal("✓", item.AchievementCheckGlyph);
        Assert.Equal("🏆 3", item.AchievementIconText);
        Assert.Equal(new[] { "First win", "ACH_PERFECT_ROUND" }, item.AchievementNames);
        Assert.Equal("First win, ACH_PERFECT_ROUND", item.AchievementDisplayText);
        Assert.Contains("• First win", item.AchievementToolTip);
    }

    [Fact]
    public void AchievementProperties_FallBackToCountWhenNameIsUnavailable()
    {
        var item = CreateItem(CreateEvent("achievement", "Achievement"));

        Assert.Empty(item.AchievementNames);
        Assert.Equal("1 achievement", item.AchievementDisplayText);
        Assert.Equal("1 achievement", item.AchievementToolTip);
    }

    private static RecordingItem CreateItem(params SteamTimelineEvent[] timelineEvents) => new()
    {
        Path = "session.mpd",
        Folder = "recording",
        GameId = "42",
        GameName = "Test Game",
        Timestamp = new DateTime(2026, 1, 1),
        SizeBytes = 1,
        DurationSeconds = 60,
        TimelineEvents = timelineEvents
    };

    private static SteamTimelineEvent CreateEvent(string type, string title) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Type = type,
        TypeLabel = type.Equals("achievement", StringComparison.OrdinalIgnoreCase)
            ? "Achievement"
            : "Event",
        Title = title,
        Timestamp = new DateTime(2026, 1, 1),
        PositionSeconds = 10
    };
}
