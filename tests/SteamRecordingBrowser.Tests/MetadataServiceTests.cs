using SteamRecordingBrowser.Services;
using Xunit;

namespace SteamRecordingBrowser.Tests;

public sealed class MetadataServiceTests
{
    [Theory]
    [InlineData("1808500", false, "steam://open/screenshots/1808500")]
    [InlineData("1808500", true, "steam://open/recording/1808500")]
    public void GetRecordingUri_UsesTheMatchingSteamMediaDestination(
        string appId,
        bool isAutoRecording,
        string expected)
    {
        var uri = SteamService.GetRecordingUri(appId, isAutoRecording);

        Assert.Equal(expected, uri.AbsoluteUri.TrimEnd('/'));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-app")]
    [InlineData("0")]
    public void GetRecordingUri_RejectsInvalidSteamAppIds(string appId)
    {
        Assert.Throws<ArgumentException>(() => SteamService.GetRecordingUri(appId, false));
    }

    [Fact]
    public void GetRecordingKey_UsesStableSteamBackgroundIdentity()
    {
        var path = @"C:\recordings\bg_1808500_20260819_025416\session.mpd";

        var key = MetadataService.GetRecordingKey(path);

        Assert.Equal("bg:1808500:20260819:025416", key);
    }

    [Fact]
    public void GetKnownTags_UsesAllStoredRecordingsAndReflectsEdits()
    {
        var metadata = new MetadataService();
        var first = metadata.ForRecording(@"C:\tag-test\first\session.mpd");
        var second = metadata.ForRecording(@"C:\tag-test\second\session.mpd");
        first.Tags = new List<string> { "Boss", " Funny " };
        second.Tags = new List<string> { "BOSS", "Action" };

        Assert.Equal(new[] { "Action", "Boss", "Funny" }, metadata.GetKnownTags());

        second.Tags = new List<string> { "New tag" };
        Assert.Equal(new[] { "Boss", "Funny", "New tag" }, metadata.GetKnownTags());
    }

    [Fact]
    public void NormalizeTags_SplitsDeduplicatesSortsAndTrims()
    {
        var tags = new[]
        {
            "boss, funny",
            "Funny",
            "  action  "
        };

        var normalized = MetadataService.NormalizeTags(tags);

        Assert.Equal(new[] { "action", "boss", "funny" }, normalized);
    }
}
