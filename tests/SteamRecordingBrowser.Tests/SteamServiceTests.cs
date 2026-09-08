using SteamRecordingBrowser.Models;
using SteamRecordingBrowser.Services;
using Xunit;

namespace SteamRecordingBrowser.Tests;

public sealed class SteamServiceTests
{
    private const string ClipId = "clip_1808500_20260318_014053";
    private const string Session = "bg_1808500_20260318_004223";

    [Theory]
    [InlineData("bg_1808500_20260318_004223")]
    [InlineData("bg_1808500_20260318_004223_0")]
    [InlineData("fg_1808500_20260318_004223")]
    public async Task ScanAndGetRecordingUri_TargetsTheSavedClipInsteadOfItsVideoSession(string session)
    {
        using var recording = new TemporaryClip(ClipId, session);
        var scanner = new RecordingScanner(new SteamService(), new DashCompatibilityService(), new MetadataService());

        var item = Assert.Single(await scanner.ScanAsync(recording.Root, null, CancellationToken.None));

        Assert.True(item.IsSavedClip);
        Assert.Equal(ClipId, item.SteamClipId);
        Assert.Equal($"steam://open/clip/{ClipId}", SteamService.GetRecordingUri(item).AbsoluteUri);
        Assert.Equal("Open this exact clip in Steam", item.OpenInSteamToolTip);
    }

    [Fact]
    public void GetClipIdForRecording_DistinguishesClipsFromTheSameSession()
    {
        const string secondId = "clip_1808500_20260318_014100";
        using var first = new TemporaryClip(ClipId, Session);
        using var second = new TemporaryClip(secondId, Session);

        Assert.Equal(ClipId, SteamClipMetadataService.GetClipIdForRecording(first.ManifestPath));
        Assert.Equal(secondId, SteamClipMetadataService.GetClipIdForRecording(second.ManifestPath));
    }

    [Theory]
    [InlineData("clip_1808500_20260318_014053_1")]
    [InlineData("CLIP_1808500_20260318_014053")]
    public void GetClipIdForRecording_PreservesTheFullFolderIdentity(string clipId)
    {
        using var recording = new TemporaryClip(clipId, Session);

        Assert.Equal(clipId, SteamClipMetadataService.GetClipIdForRecording(recording.ManifestPath));
    }

    [Theory]
    [InlineData("renamed-clip", true)]
    [InlineData("clip_1808500_20260318", true)]
    [InlineData("clip_1808500_20260318_014053_extra", true)]
    [InlineData(ClipId, false)]
    public void GetRecordingUri_FallsBackWhenClipIdentityCannotBeResolved(string folder, bool hasMetadata)
    {
        using var recording = new TemporaryClip(folder, Session, hasMetadata);
        var clipId = SteamClipMetadataService.GetClipIdForRecording(recording.ManifestPath);
        var item = CreateItem(clipId);

        Assert.Null(clipId);
        Assert.Equal("steam://open/screenshots/1808500", SteamService.GetRecordingUri(item).AbsoluteUri);
        Assert.Equal("Open this game's media library in Steam", item.OpenInSteamToolTip);
    }

    [Fact]
    public void GetClipIdForRecording_DoesNotUseAnUnrelatedAncestorAboveClipMetadata()
    {
        using var recording = new TemporaryClip(Path.Combine(ClipId, "renamed-clip"), Session);

        Assert.Null(SteamClipMetadataService.GetClipIdForRecording(recording.ManifestPath));
    }

    [Fact]
    public void GetRecordingUri_AutomaticRecordingsStillOpenTheGameTimeline()
    {
        var item = CreateItem(ClipId, isAutoRecording: true);

        Assert.Equal("steam://open/recording/1808500", SteamService.GetRecordingUri(item).AbsoluteUri);
        Assert.Equal("Open this game's recording timeline in Steam", item.OpenInSteamToolTip);
    }

    [Fact]
    public void GetRecordingUri_RejectsNullItems()
    {
        Assert.Throws<ArgumentNullException>(() => SteamService.GetRecordingUri(null!));
    }

    private static RecordingItem CreateItem(string? clipId, bool isAutoRecording = false) => new()
    {
        Path = @"C:\recordings\video\bg_1808500_20260318_004223\session.mpd",
        Folder = Session,
        GameId = "1808500",
        GameName = "Test game",
        Timestamp = DateTime.UnixEpoch,
        SizeBytes = 0,
        DurationSeconds = 1,
        IsAutoRecording = isAutoRecording,
        SteamClipId = clipId
    };

    private sealed class TemporaryClip : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "SteamClipUriTests", Guid.NewGuid().ToString("N"));
        public string ManifestPath { get; }

        public TemporaryClip(string clipFolder, string session, bool hasMetadata = true)
        {
            var clipDirectory = Path.Combine(Root, "clips", clipFolder);
            var videoDirectory = Path.Combine(clipDirectory, "video", session);
            Directory.CreateDirectory(videoDirectory);
            ManifestPath = Path.Combine(videoDirectory, "session.mpd");
            File.WriteAllText(ManifestPath,
                "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT1S\"><Period /></MPD>");
            if (hasMetadata)
                File.WriteAllBytes(Path.Combine(clipDirectory, "clip.pb"), []);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
