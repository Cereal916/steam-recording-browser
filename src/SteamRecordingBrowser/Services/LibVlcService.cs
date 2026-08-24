using System.IO;
using System.Diagnostics;
using LibVLCSharp.Shared;
using SteamRecordingBrowser.Models;

namespace SteamRecordingBrowser.Services;

public sealed class LibVlcService : IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly DashCompatibilityService _dash;
    private readonly FfmpegExportService _ffmpeg;
    private long _lastFrameTimingDiagnostic;

    public LibVLC LibVlc => _libVlc;

    public LibVlcService(DashCompatibilityService dash)
    {
        _dash = dash;
        _ffmpeg = new FfmpegExportService(dash);
        _libVlc = new LibVLC(
            "--no-video-title-show",
            // The WPF HWND is an SDR surface. Force libVLC to tone-map HDR
            // instead of repeatedly failing Rec.2020/PQ screen conversion.
            "--d3d11-hdr-mode=never",
            "--quiet");
        _libVlc.Log += LibVlc_Log;
    }

    private void LibVlc_Log(object? sender, LogEventArgs e)
    {
        var level = e.Level.ToString();
        var module = e.Module ?? "unknown";
        var relevantModule = module.Contains("dash", StringComparison.OrdinalIgnoreCase) ||
                             module.Contains("adaptive", StringComparison.OrdinalIgnoreCase) ||
                             module.Contains("http", StringComparison.OrdinalIgnoreCase) ||
                             module.Contains("demux", StringComparison.OrdinalIgnoreCase) ||
                             module.Contains("decoder", StringComparison.OrdinalIgnoreCase);
        var importantLevel = level.Contains("Warn", StringComparison.OrdinalIgnoreCase) ||
                             level.Contains("Error", StringComparison.OrdinalIgnoreCase);
        var benignNativeDiagnostic =
            module.Equals("drawable", StringComparison.OrdinalIgnoreCase) &&
            e.Message.Contains("unsupported control query", StringComparison.OrdinalIgnoreCase) ||
            module.Equals("direct3d11", StringComparison.OrdinalIgnoreCase) &&
            e.Message.Contains("SetThumbNailClip failed", StringComparison.OrdinalIgnoreCase) ||
            module.Equals("mp4", StringComparison.OrdinalIgnoreCase) &&
            (e.Message.Contains("elst box found", StringComparison.OrdinalIgnoreCase) ||
             e.Message.Contains("no chunk defined", StringComparison.OrdinalIgnoreCase) ||
             e.Message.Contains("STTS table of 0 entries", StringComparison.OrdinalIgnoreCase)) ||
            module.Equals("main", StringComparison.OrdinalIgnoreCase) &&
            (e.Message.Contains("picture is too late to be displayed", StringComparison.OrdinalIgnoreCase) ||
             e.Message.Contains("More than 11 late frames", StringComparison.OrdinalIgnoreCase));
        var frameTimingDiagnostic =
            e.Message.Contains("picture is too late to be displayed", StringComparison.OrdinalIgnoreCase) ||
            e.Message.Contains("More than 11 late frames", StringComparison.OrdinalIgnoreCase);
        if (frameTimingDiagnostic)
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Read(ref _lastFrameTimingDiagnostic);
            if (previous != 0 && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(5))
                return;
            Interlocked.Exchange(ref _lastFrameTimingDiagnostic, now);
        }
        if (importantLevel || relevantModule)
        {
            var appLevel = benignNativeDiagnostic || !importantLevel ? "DEBUG" : "WARN";
            AppLogger.Write($"libVLC [{level}] [{module}] {e.Message}", appLevel);
        }
    }

    public Media CreatePlaybackMedia(string recordingPath)
    {
        var manifest = _dash.GetPlaybackManifest(recordingPath);
        return CreatePlaybackMedia(new Uri(manifest), isLive: false);
    }

    public LiveDashServer CreateLiveDashServer(IReadOnlyList<string> recordingPaths) => new(_dash, recordingPaths);

    public Media CreatePlaybackMedia(Uri source, bool isLive, bool useHardwareDecoding = true)
    {
        var media = new Media(_libVlc, source);

        // Prefer hardware-assisted decoding when the installed GPU/driver and
        // codec support it. libVLC falls back to software when necessary.
        media.AddOption(useHardwareDecoding ? ":avcodec-hw=any" : ":avcodec-hw=none");
        if (!useHardwareDecoding)
            AppLogger.Write($"Software video decoding selected for {source}.", "DEBUG");
        if (isLive)
        {
            media.AddOption(":network-caching=1000");
            media.AddOption(":live-caching=1000");
            media.AddOption(":http-reconnect=true");
        }

        return media;
    }

    public VideoCodecInfo GetVideoCodec(string recordingPath) =>
        _dash.GetVideoCodec(recordingPath);

    public async Task ExportMp4Async(
        RecordingItem item,
        string destination,
        ExportVideoCodec codec,
        bool useHardwareEncoding,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        var incompleteDestination = GetIncompletePath(destination);
        await FfmpegExportService.DeletePartialOutputAsync(incompleteDestination).ConfigureAwait(false);
        var completed = false;

        try
        {
            if (codec != ExportVideoCodec.Original)
            {
                await _ffmpeg.ExportAsync(item, incompleteDestination, codec, useHardwareEncoding, status, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                status?.Report($"Exporting {System.IO.Path.GetFileName(incompleteDestination)} as {codec.DisplayName()}…");

                // Complete and dispose the libVLC output pipeline before ffprobe
                // opens the MP4. Its stream table is not guaranteed to be final
                // until the muxer has been closed.
                await RunOriginalRemuxAsync(item.Path, incompleteDestination, cancellationToken).ConfigureAwait(false);

                if (!File.Exists(incompleteDestination) || new FileInfo(incompleteDestination).Length == 0)
                    throw new InvalidOperationException("libVLC completed without producing a valid MP4 file.");

                if (FfmpegExportService.FindFfprobe() is not null)
                    await FfmpegExportService.ValidateOutputAsync(incompleteDestination, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(incompleteDestination, destination, overwrite: true);
            completed = true;
            status?.Report($"Export complete: {destination}");
        }
        finally
        {
            if (!completed)
                await FfmpegExportService.DeletePartialOutputAsync(incompleteDestination).ConfigureAwait(false);
        }
    }

    private static string GetIncompletePath(string destination)
    {
        var directory = Path.GetDirectoryName(destination) ?? "";
        var fileName = Path.GetFileNameWithoutExtension(destination);
        var extension = Path.GetExtension(destination);
        return Path.Combine(directory, $"{fileName}.incomplete{extension}");
    }

    private async Task RunOriginalRemuxAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        using var media = new Media(_libVlc, new Uri(source));
        var escaped = destination.Replace(@"\", @"\\").Replace("\"", "\\\"");
        media.AddOption($":sout=#std{{access=file,mux=mp4,dst=\"{escaped}\"}}");

        using var player = new MediaPlayer(_libVlc);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Ended(object? s, EventArgs e) => completion.TrySetResult(true);
        void Error(object? s, EventArgs e) => completion.TrySetException(
            new InvalidOperationException("libVLC reported an Original codec export error."));

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
        }
        finally
        {
            player.EndReached -= Ended;
            player.EncounteredError -= Error;
        }
    }

    public void Dispose()
    {
        _libVlc.Log -= LibVlc_Log;
        _libVlc.Dispose();
    }
}

public enum ExportVideoCodec
{
    Original,
    H264,
    Hevc,
    Av1
}

public static class ExportVideoCodecExtensions
{
    public static string DisplayName(this ExportVideoCodec codec) => codec switch
    {
        ExportVideoCodec.H264 => "H.264",
        ExportVideoCodec.Hevc => "HEVC / H.265",
        ExportVideoCodec.Av1 => "AV1",
        _ => "the original codec"
    };

    public static string FileNameLabel(this ExportVideoCodec codec) => codec switch
    {
        ExportVideoCodec.H264 => "H.264",
        ExportVideoCodec.Hevc => "HEVC",
        ExportVideoCodec.Av1 => "AV1",
        _ => "Original"
    };
}
