using SteamRecordingBrowser.Services;
using Xunit;

namespace SteamRecordingBrowser.Tests;

public sealed class RecordingScannerTests
{
    [Theory]
    [InlineData("bg_1808500_20260908_042539")]
    [InlineData("bg_1808500_20260908_042539_0")]
    [InlineData("BG_1808500_20260908_042539_12")]
    public void TryParseRecordingFolderName_AcceptsSteamSessionFormats(string folder)
    {
        var parsed = RecordingScanner.TryParseRecordingFolderName(
            folder,
            out var gameId,
            out var timestampUtc);

        Assert.True(parsed);
        Assert.Equal("1808500", gameId);
        Assert.Equal(DateTimeKind.Utc, timestampUtc.Kind);
        Assert.Equal(new DateTime(2026, 9, 8, 4, 25, 39, DateTimeKind.Utc), timestampUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bg_1808500_20260908")]
    [InlineData("clip_1808500_20260908_042539")]
    [InlineData("bg_1808500_20260908_042539_extra")]
    public void TryParseRecordingFolderName_RejectsUnrecognizedFormats(string folder)
    {
        var parsed = RecordingScanner.TryParseRecordingFolderName(folder, out _, out _);

        Assert.False(parsed);
    }
}
