using System.IO;
using LibVLCSharp.Shared;
using SteamRecordingBrowser.Models;

namespace SteamRecordingBrowser.Services;

public sealed class LibVlcService : IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly DashCompatibilityService _dash;

    public LibVLC LibVlc => _libVlc;

    public LibVlcService(DashCompatibilityService dash)
    {
        _dash = dash;
        _libVlc = new LibVLC(
            "--no-video-title-show",
            "--quiet");
    }

    public Media CreatePlaybackMedia(string recordingPath)
    {
        var manifest = _dash.GetPlaybackManifest(recordingPath);
        var media = new Media(_libVlc, new Uri(manifest));

        // Prefer hardware-assisted decoding when the installed GPU/driver and
        // codec support it. libVLC falls back to software when necessary.
        media.AddOption(":avcodec-hw=any");

        return media;
    }

    public async Task ExportMp4Async(
        RecordingItem item,
        string destination,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destination))
            File.Delete(destination);

        status?.Report($"Exporting {System.IO.Path.GetFileName(destination)}…");

        // Keep export input consistent with historical behavior: remux the
        // original Steam MPD into MP4 without transcoding.
        using var media = new Media(_libVlc, new Uri(item.Path));

        var escaped = destination.Replace(@"\", @"\\").Replace("\"", "\\\"");
        media.AddOption($":sout=#std{{access=file,mux=mp4,dst=\"{escaped}\"}}");
        media.AddOption(":sout-keep");

        using var player = new MediaPlayer(_libVlc);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Ended(object? s, EventArgs e) => completion.TrySetResult(true);
        void Error(object? s, EventArgs e) => completion.TrySetException(
            new InvalidOperationException("libVLC reported an export error."));

        player.EndReached += Ended;
        player.EncounteredError += Error;

        try
        {
            if (!player.Play(media))
                throw new InvalidOperationException("libVLC could not start the export.");

            using var registration = cancellationToken.Register(() =>
            {
                try { player.Stop(); } catch { }
                completion.TrySetCanceled(cancellationToken);
            });

            await completion.Task.ConfigureAwait(false);

            if (!File.Exists(destination) || new FileInfo(destination).Length == 0)
                throw new InvalidOperationException("libVLC completed without producing a valid MP4 file.");

            status?.Report($"Export complete: {destination}");
        }
        finally
        {
            player.EndReached -= Ended;
            player.EncounteredError -= Error;
        }
    }

    public void Dispose() => _libVlc.Dispose();
}
