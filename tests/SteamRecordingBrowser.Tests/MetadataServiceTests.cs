using SteamRecordingBrowser.Services;
using Xunit;

namespace SteamRecordingBrowser.Tests;

public sealed class MetadataServiceTests
{
    [Fact]
    public void GetRecordingKey_UsesStableSteamBackgroundIdentity()
    {
        var path = @"C:\recordings\bg_1808500_20260819_025416\session.mpd";

        var key = MetadataService.GetRecordingKey(path);

        Assert.Equal("bg:1808500:20260819:025416", key);
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
