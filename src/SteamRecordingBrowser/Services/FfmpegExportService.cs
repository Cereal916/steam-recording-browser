using System.Diagnostics;
using System.Globalization;
using System.IO;
using SteamRecordingBrowser.Models;

namespace SteamRecordingBrowser.Services;

public sealed class FfmpegExportService
{
    private readonly DashCompatibilityService _dash;

    public FfmpegExportService(DashCompatibilityService dash) => _dash = dash;

    public static string? FindFfmpeg() => FindTool("ffmpeg.exe");
    public static string? FindFfprobe() => FindTool("ffprobe.exe");
    public static bool IsAvailable => FindFfmpeg() is not null && FindFfprobe() is not null;

    public async Task ExportAsync(
        RecordingItem item,
        string destination,
        ExportVideoCodec codec,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExportCoreAsync(item, destination, codec, status, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await DeletePartialOutputAsync(destination).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ExportCoreAsync(
        RecordingItem item,
        string destination,
        ExportVideoCodec codec,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        var ffmpeg = FindFfmpeg() ?? throw MissingFfmpegException();
        var manifest = _dash.GetPlaybackManifest(item.Path);
        var encoders = await GetEncodersAsync(ffmpeg, cancellationToken).ConfigureAwait(false);
        var candidates = BuildEncoderCandidates(codec, encoders);

        if (candidates.Count == 0)
            throw new InvalidOperationException($"The bundled FFmpeg build has no {codec.DisplayName()} encoder.");

        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDelete(destination);
            status?.Report($"Exporting with {candidate.DisplayName}…");

            var result = await RunEncodeAsync(
                ffmpeg,
                manifest,
                destination,
                item.DurationSeconds,
                candidate,
                status,
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode == 0)
            {
                await ValidateOutputAsync(destination, cancellationToken).ConfigureAwait(false);
                status?.Report($"Export complete: {destination}");
                return;
            }

            failures.Add($"{candidate.DisplayName}: {LastUsefulLine(result.Error)}");
            AppLogger.Write($"FFmpeg encoder failed ({candidate.DisplayName}): {result.Error}", "WARN");
        }

        TryDelete(destination);
        throw new InvalidOperationException(
            $"FFmpeg could not encode this recording as {codec.DisplayName()}.\n\n" +
            string.Join("\n", failures));
    }

    public static async Task ValidateOutputAsync(string path, CancellationToken cancellationToken)
    {
        var ffprobe = FindFfprobe() ?? throw MissingFfmpegException();
        var startInfo = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("stream=codec_type");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("csv=p=0");
        startInfo.ArgumentList.Add(path);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start ffprobe.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        var tracks = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (process.ExitCode != 0 || !tracks.Contains("video", StringComparer.OrdinalIgnoreCase))
        {
            TryDelete(path);
            throw new InvalidOperationException(
                "Export validation failed because the MP4 contains no video stream. " + LastUsefulLine(error));
        }

        if (!tracks.Contains("audio", StringComparer.OrdinalIgnoreCase))
        {
            TryDelete(path);
            throw new InvalidOperationException("Export validation failed because the MP4 contains no audio stream.");
        }
    }

    private static async Task<ProcessResult> RunEncodeAsync(
        string ffmpeg,
        string input,
        string destination,
        double durationSeconds,
        EncoderCandidate encoder,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var value in new[] { "-hide_banner", "-y", "-i", input, "-map", "0:v:0", "-map", "0:a:0?" })
            startInfo.ArgumentList.Add(value);
        foreach (var value in encoder.Arguments)
            startInfo.ArgumentList.Add(value);
        foreach (var value in new[]
                 {
                     "-c:a", "aac", "-b:a", "192k", "-ac", "2",
                     "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", destination
                 })
            startInfo.ArgumentList.Add(value);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start FFmpeg.");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        long outputMicroseconds = 0;
        double encodingSpeed = 0;

        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
                    long.TryParse(line[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out outputMicroseconds);
                else if (line.StartsWith("speed=", StringComparison.Ordinal))
                    double.TryParse(line[6..].TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out encodingSpeed);
                else if (line == "progress=continue" && durationSeconds > 0)
                {
                    var encodedSeconds = outputMicroseconds / 1_000_000d;
                    var percent = Math.Clamp(encodedSeconds / durationSeconds * 100d, 0d, 100d);
                    var eta = encodingSpeed > 0
                        ? TimeSpan.FromSeconds(Math.Max(0, durationSeconds - encodedSeconds) / encodingSpeed)
                        : (TimeSpan?)null;
                    var detail = encodingSpeed > 0
                        ? $" • {encodingSpeed:N1}x • about {FormatEta(eta!.Value)} remaining"
                        : "";
                    status?.Report($"Encoding with {encoder.DisplayName}: {percent:N0}%{detail}");
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, await errorTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            await DeletePartialOutputAsync(destination).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<HashSet<string>> GetEncodersAsync(string ffmpeg, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-encoders");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not inspect FFmpeg encoders.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var combined = await outputTask.ConfigureAwait(false) + await errorTask.ConfigureAwait(false);

        return combined
            .Split('\n')
            .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && parts[0].StartsWith('V'))
            .Select(parts => parts[1])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static List<EncoderCandidate> BuildEncoderCandidates(
        ExportVideoCodec codec,
        IReadOnlySet<string> available)
    {
        var candidates = codec switch
        {
            ExportVideoCodec.H264 => new[]
            {
                Candidate("h264_nvenc", "NVIDIA NVENC", "-preset", "p5", "-b:v", "12M", "-maxrate", "14M", "-bufsize", "24M", "-pix_fmt", "yuv420p"),
                Candidate("h264_qsv", "Intel Quick Sync", "-preset", "medium", "-b:v", "12M", "-maxrate", "14M", "-bufsize", "24M", "-pix_fmt", "nv12"),
                Candidate("h264_amf", "AMD AMF", "-quality", "balanced", "-b:v", "12M", "-maxrate", "14M", "-bufsize", "24M", "-pix_fmt", "nv12"),
                Candidate("libx264", "H.264 software", "-preset", "medium", "-b:v", "12M", "-maxrate", "14M", "-bufsize", "24M", "-pix_fmt", "yuv420p")
            },
            ExportVideoCodec.Hevc => new[]
            {
                Candidate("hevc_nvenc", "NVIDIA NVENC", "-preset", "p5", "-b:v", "8M", "-maxrate", "10M", "-bufsize", "16M", "-pix_fmt", "yuv420p", "-tag:v", "hvc1"),
                Candidate("hevc_qsv", "Intel Quick Sync", "-preset", "medium", "-b:v", "8M", "-maxrate", "10M", "-bufsize", "16M", "-pix_fmt", "nv12", "-tag:v", "hvc1"),
                Candidate("hevc_amf", "AMD AMF", "-quality", "balanced", "-b:v", "8M", "-maxrate", "10M", "-bufsize", "16M", "-pix_fmt", "nv12", "-tag:v", "hvc1"),
                Candidate("libx265", "HEVC software", "-preset", "medium", "-b:v", "8M", "-maxrate", "10M", "-bufsize", "16M", "-pix_fmt", "yuv420p", "-tag:v", "hvc1")
            },
            ExportVideoCodec.Av1 => new[]
            {
                Candidate("av1_nvenc", "NVIDIA NVENC", "-preset", "p5", "-b:v", "6M", "-pix_fmt", "yuv420p"),
                Candidate("av1_qsv", "Intel Quick Sync", "-preset", "medium", "-b:v", "6M", "-pix_fmt", "nv12"),
                Candidate("av1_amf", "AMD AMF", "-quality", "balanced", "-b:v", "6M", "-pix_fmt", "nv12"),
                Candidate("libsvtav1", "SVT-AV1 software", "-preset", "8", "-b:v", "6M", "-pix_fmt", "yuv420p"),
                Candidate("libaom-av1", "AOM AV1 software", "-cpu-used", "6", "-b:v", "6M", "-pix_fmt", "yuv420p")
            },
            _ => Array.Empty<EncoderCandidate>()
        };

        return candidates.Where(candidate => available.Contains(candidate.Name)).ToList();
    }

    private static EncoderCandidate Candidate(string name, string displayName, params string[] args) =>
        new(name, displayName, new[] { "-c:v", name }.Concat(args).ToArray());

    private static string? FindTool(string name)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", name);
        if (File.Exists(bundled)) return bundled;

        var adjacent = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(adjacent)) return adjacent;

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        return null;
    }

    private static InvalidOperationException MissingFfmpegException() => new(
        "FFmpeg is not available. Keep the bundled ffmpeg folder with the application, " +
        "or install FFmpeg and add it to PATH for development builds.");

    private static string LastUsefulLine(string value) => value
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .LastOrDefault() ?? "Unknown encoder error.";

    private static string FormatEta(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{value.Minutes}:{value.Seconds:00}";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    internal static async Task DeletePartialOutputAsync(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                    return;

                File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 9)
                {
                    AppLogger.Write($"Could not remove cancelled export: {path}. {ex.Message}", "WARN");
                    return;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }

    private sealed record EncoderCandidate(string Name, string DisplayName, string[] Arguments);
    private sealed record ProcessResult(int ExitCode, string Error);
}
