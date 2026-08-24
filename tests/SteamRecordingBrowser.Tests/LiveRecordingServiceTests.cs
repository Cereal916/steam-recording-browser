using System.Net;
using System.Net.Http.Headers;
using SteamRecordingBrowser.Services;
using Xunit;

namespace SteamRecordingBrowser.Tests;

public sealed class LiveRecordingServiceTests
{
    private const string Manifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="dynamic"
             availabilityStartTime="2026-08-23T12:00:00Z"
             mediaPresentationDuration="PT30S" minBufferTime="PT3S">
          <Period start="PT10S">
            <AdaptationSet contentType="video">
              <Representation id="0" mimeType="video/mp4" codecs="avc1.640028" bandwidth="12000000">
                <SegmentTemplate timescale="1000" duration="3000"
                                 initialization="init-stream$RepresentationID$.m4s"
                                 media="chunk-stream$RepresentationID$-$Number%05d$.m4s"
                                 startNumber="1" />
              </Representation>
            </AdaptationSet>
          </Period>
        </MPD>
        """;

    [Fact]
    public void CreateLiveManifest_SwitchesBetweenDynamicAndFinalizedModes()
    {
        using var recording = new TemporaryRecording(Manifest);
        var dash = new DashCompatibilityService();
        File.WriteAllBytes(Path.Combine(recording.DirectoryPath, "init-stream0.m4s"), new byte[] { 0, 1, 2 });

        var live = dash.CreateLiveManifest(recording.ManifestPath);
        Assert.Contains("type=\"dynamic\"", live);
        Assert.Contains("minimumUpdatePeriod=\"PT1S\"", live);
        Assert.Contains("availabilityStartTime=\"2026-08-23T12:00:00Z\"", live);
        Assert.DoesNotContain("mediaPresentationDuration", live);

        File.SetLastWriteTimeUtc(recording.ManifestPath, DateTime.UtcNow.AddMinutes(-1));
        File.SetLastWriteTimeUtc(Path.Combine(recording.DirectoryPath, "init-stream0.m4s"), DateTime.UtcNow.AddMinutes(-1));
        var finalized = dash.CreateLiveManifest(recording.ManifestPath);
        Assert.Contains("type=\"static\"", finalized);
        Assert.Contains("duration=\"PT20S\"", finalized);
    }

    [Fact]
    public void GetDurationSeconds_UsesDeclaredDurationForInactiveDynamicManifest()
    {
        using var recording = new TemporaryRecording(Manifest);
        var segmentPath = Path.Combine(recording.DirectoryPath, "init-stream0.m4s");
        File.WriteAllBytes(segmentPath, [0, 1, 2]);
        File.SetLastWriteTimeUtc(recording.ManifestPath, DateTime.UtcNow.AddMinutes(-1));
        File.SetLastWriteTimeUtc(segmentPath, DateTime.UtcNow.AddMinutes(-1));

        var duration = new DashCompatibilityService().GetDurationSeconds(recording.ManifestPath);

        Assert.Equal(20, duration);
    }

    [Fact]
    public void GetDurationSeconds_PreservesSteamRetainedDurationWhenPeriodOffsetIsLarger()
    {
        const string finalizedRollingManifest = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static"
                 mediaPresentationDuration="PT1H55M34.602S">
              <Period start="PT2H39M24S" />
            </MPD>
            """;
        using var recording = new TemporaryRecording(finalizedRollingManifest);

        var duration = new DashCompatibilityService().GetDurationSeconds(recording.ManifestPath);

        Assert.Equal(6934.602, duration, 3);
    }

    [Fact]
    public void GetDynamicDurationSeconds_UsesRetainedSegmentsAfterSessionRestart()
    {
        using var recording = new TemporaryRecording(Manifest);
        File.WriteAllBytes(Path.Combine(recording.DirectoryPath, "init-stream0.m4s"), [0, 1, 2]);
        foreach (var number in Enumerable.Range(1, 40))
            File.WriteAllBytes(Path.Combine(recording.DirectoryPath,
                $"chunk-stream0-{number:D5}.m4s"), [0, 1, 2]);

        var duration = LiveRecordingService.GetDynamicDurationSeconds(recording.ManifestPath);

        Assert.Equal(120, duration);
    }

    [Fact]
    public void CreateLiveManifest_AdvancesPastSegmentsRemovedFromRollingBuffer()
    {
        using var recording = new TemporaryRecording(Manifest);
        File.WriteAllBytes(Path.Combine(recording.DirectoryPath, "init-stream0.m4s"), new byte[] { 0, 1, 2 });
        File.WriteAllBytes(Path.Combine(recording.DirectoryPath, "chunk-stream0-00166.m4s"), new byte[] { 0, 1, 2 });
        File.WriteAllBytes(Path.Combine(recording.DirectoryPath, "chunk-stream0-00170.m4s"), new byte[] { 0, 1, 2 });

        var dash = new DashCompatibilityService();
        var manifest = dash.CreateLiveManifest(recording.ManifestPath);

        Assert.Contains("startNumber=\"166\"", manifest);
        Assert.Contains("availabilityStartTime=\"2026-08-23T12:08:15.0000000Z\"", manifest);
        var bridged = dash.CreateBridgedLiveManifest(recording.ManifestPath, 2);
        Assert.Contains("type=\"dynamic\"", bridged);
        Assert.Contains("media=\"s2/chunk-stream$RepresentationID$-$Number%05d$.m4s\"", bridged);
    }

    [Fact]
    public void CreateLiveManifest_AlignsRepresentationsToCommonRollingStart()
    {
        const string twoTrackManifest = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="dynamic"
                 availabilityStartTime="2026-08-23T12:00:00Z" minBufferTime="PT3S">
              <Period start="PT0S">
                <AdaptationSet contentType="video"><Representation id="0">
                  <SegmentTemplate timescale="1000" duration="3000" startNumber="1"
                    initialization="init-stream$RepresentationID$.m4s"
                    media="chunk-stream$RepresentationID$-$Number%05d$.m4s" />
                </Representation></AdaptationSet>
                <AdaptationSet contentType="audio"><Representation id="1">
                  <SegmentTemplate timescale="1000" duration="3000" startNumber="1"
                    initialization="init-stream$RepresentationID$.m4s"
                    media="chunk-stream$RepresentationID$-$Number%05d$.m4s" />
                </Representation></AdaptationSet>
              </Period>
            </MPD>
            """;
        using var recording = new TemporaryRecording(twoTrackManifest);
        foreach (var representation in new[] { 0, 1 })
            File.WriteAllBytes(Path.Combine(recording.DirectoryPath, $"init-stream{representation}.m4s"), [0, 1, 2]);
        foreach (var number in Enumerable.Range(100, 15))
            File.WriteAllBytes(Path.Combine(recording.DirectoryPath, $"chunk-stream0-{number:D5}.m4s"), [0, 1, 2]);
        foreach (var number in Enumerable.Range(105, 10))
            File.WriteAllBytes(Path.Combine(recording.DirectoryPath, $"chunk-stream1-{number:D5}.m4s"), [0, 1, 2]);

        var manifest = new DashCompatibilityService().CreateLiveManifest(recording.ManifestPath);

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(manifest, "startNumber=\"105\"").Count);
    }

    [Fact]
    public async Task LiveDashServer_ServesManifestAndSegmentRanges()
    {
        using var recording = new TemporaryRecording(Manifest);
        var cancellationToken = TestContext.Current.CancellationToken;
        var segmentPath = Path.Combine(recording.DirectoryPath, "init-stream0.m4s");
        await File.WriteAllBytesAsync(segmentPath,
            Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(), cancellationToken);
        File.SetLastWriteTimeUtc(segmentPath, DateTime.UtcNow.AddSeconds(-2));

        using var server = new LiveDashServer(new DashCompatibilityService(), new[] { recording.ManifestPath });
        using var client = new HttpClient();
        var manifest = await client.GetStringAsync(server.ManifestUri, cancellationToken);
        Assert.Contains("type=\"static\"", manifest);
        Assert.Contains("mediaPresentationDuration=", manifest);

        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(server.ManifestUri, "s0/init-stream0.m4s"));
        request.Headers.Range = new RangeHeaderValue(4, 11);
        using var response = await client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(Enumerable.Range(4, 8).Select(value => (byte)value),
            await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    private sealed class TemporaryRecording : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(
            Path.GetTempPath(), "SteamRecordingBrowser.Tests", Guid.NewGuid().ToString("N"));
        public string ManifestPath => Path.Combine(DirectoryPath, "session.mpd");

        public TemporaryRecording(string manifest)
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(ManifestPath, manifest);
        }

        public void Dispose()
        {
            try { Directory.Delete(DirectoryPath, recursive: true); } catch { }
        }
    }
}
