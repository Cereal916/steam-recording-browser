using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SteamRecordingBrowser.Services;

public sealed class LiveDashServer : IDisposable
{
    private readonly DashCompatibilityService _dash;
    private readonly string[] _manifestPaths;
    private readonly string[] _recordingDirectories;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _serverTask;
    private long _segmentRequestCount;
    private long _liveManifestRequestCount;

    public Uri ManifestUri { get; }
    public Uri LiveManifestUri { get; }

    public LiveDashServer(DashCompatibilityService dash, IReadOnlyList<string> manifestPaths)
    {
        _dash = dash;
        if (manifestPaths.Count == 0)
            throw new ArgumentException("At least one recording session is required.", nameof(manifestPaths));
        _manifestPaths = manifestPaths.Select(Path.GetFullPath).ToArray();
        _recordingDirectories = _manifestPaths.Select(path => Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Recording manifest has no directory.")).ToArray();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        ManifestUri = new Uri($"http://127.0.0.1:{port}/manifest.mpd");
        LiveManifestUri = new Uri($"http://127.0.0.1:{port}/live.mpd");
        _serverTask = AcceptClientsAsync(_cancellation.Token);
        AppLogger.Write($"DASH bridge started for {_manifestPaths.Length} session(s) on port {port}.");
    }

    private async Task AcceptClientsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            AppLogger.WriteException("Live DASH bridge stopped unexpectedly", ex);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestLine)) return;

                string? range = null;
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } header)
                {
                    if (header.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                        range = header[6..].Trim();
                }

                var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || parts[0] is not ("GET" or "HEAD"))
                {
                    await WriteErrorAsync(stream, 405, "Method Not Allowed", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var requestPath = Uri.UnescapeDataString(parts[1].Split('?', 2)[0]).TrimStart('/');
                if (requestPath.Equals("manifest.mpd", StringComparison.OrdinalIgnoreCase))
                {
                    var manifest = _dash.CreateCombinedManifest(_manifestPaths, staticSnapshot: true);
                    var bytes = Encoding.UTF8.GetBytes(manifest);
                    AppLogger.Write(
                        $"DASH snapshot served. sessions={_manifestPaths.Length} bytes={bytes.Length} " +
                        $"active={_manifestPaths.Count(LiveRecordingService.IsActivelyRecording)}", "DEBUG");
                    await WriteBytesAsync(stream, bytes, "application/dash+xml", parts[0] == "HEAD", cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (requestPath.Equals("live.mpd", StringComparison.OrdinalIgnoreCase))
                {
                    var activeIndex = Array.FindLastIndex(_manifestPaths, LiveRecordingService.IsActivelyRecording);
                    if (activeIndex < 0)
                    {
                        AppLogger.Write("Dynamic DASH manifest requested after recording finalized.", "WARN");
                        await WriteErrorAsync(stream, 404, "No Active Recording", cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    var manifest = _dash.CreateBridgedLiveManifest(_manifestPaths[activeIndex], activeIndex);
                    var bytes = Encoding.UTF8.GetBytes(manifest);
                    var manifestRequest = Interlocked.Increment(ref _liveManifestRequestCount);
                    if (manifestRequest <= 3 || manifestRequest % 120 == 0)
                        AppLogger.Write(
                            $"Dynamic DASH manifest served. request={manifestRequest} session={activeIndex} " +
                            $"bytes={bytes.Length} source={_manifestPaths[activeIndex]}", "DEBUG");
                    await WriteBytesAsync(stream, bytes, "application/dash+xml", parts[0] == "HEAD", cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                var pathParts = requestPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (pathParts.Length != 2 || pathParts[0].Length < 2 || pathParts[0][0] != 's' ||
                    !int.TryParse(pathParts[0][1..], NumberStyles.None, CultureInfo.InvariantCulture, out var sessionIndex) ||
                    sessionIndex < 0 || sessionIndex >= _recordingDirectories.Length ||
                    Path.GetFileName(pathParts[1]) != pathParts[1] ||
                    (!pathParts[1].EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) &&
                     !pathParts[1].EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)))
                {
                    await WriteErrorAsync(stream, 404, "Not Found", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var filePath = Path.Combine(_recordingDirectories[sessionIndex], pathParts[1]);
                if (!File.Exists(filePath))
                {
                    AppLogger.Write($"DASH segment not found: request={requestPath} resolved={filePath}", "WARN");
                    await WriteErrorAsync(stream, 404, "Not Found", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var segmentRequest = Interlocked.Increment(ref _segmentRequestCount);
                if (segmentRequest <= 3 || segmentRequest % 500 == 0)
                    AppLogger.Write(
                        $"DASH segment served. request={segmentRequest} path={requestPath} " +
                        $"bytes={new FileInfo(filePath).Length} ageMs=" +
                        $"{(DateTime.UtcNow - File.GetLastWriteTimeUtc(filePath)).TotalMilliseconds:F0} " +
                        $"range={range ?? "none"}", "DEBUG");

                // Avoid handing libVLC a fragment while Steam is still
                // appending its final boxes. It will retry from the refreshed
                // dynamic manifest on the next update.
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(filePath) < TimeSpan.FromMilliseconds(750))
                {
                    AppLogger.Write($"DASH segment still being written: {requestPath}", "WARN");
                    await WriteErrorAsync(stream, 503, "Segment Still Recording", cancellationToken, "Retry-After: 1\r\n")
                        .ConfigureAwait(false);
                    return;
                }

                await WriteFileAsync(stream, filePath, range, parts[0] == "HEAD", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception ex)
            {
                AppLogger.WriteException("Live DASH request failed", ex);
            }
        }
    }

    private static async Task WriteFileAsync(
        Stream output, string path, string? range, bool headOnly, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var start = 0L;
        var end = file.Length - 1;
        var partial = TryParseRange(range, file.Length, out var rangeStart, out var rangeEnd);
        if (partial)
        {
            start = rangeStart;
            end = rangeEnd;
        }

        var length = Math.Max(0, end - start + 1);
        var status = partial ? "206 Partial Content" : "200 OK";
        var extra = partial ? $"Content-Range: bytes {start}-{end}/{file.Length}\r\n" : "";
        await WriteHeadersAsync(output, status, "video/iso.segment", length, extra, cancellationToken)
            .ConfigureAwait(false);
        if (headOnly || length == 0) return;

        file.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static bool TryParseRange(string? value, long length, out long start, out long end)
    {
        start = 0;
        end = Math.Max(0, length - 1);
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return false;
        var values = value[6..].Split('-', 2);
        if (!long.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out start))
            return false;
        if (values.Length > 1 && long.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedEnd))
            end = Math.Min(parsedEnd, length - 1);
        return start >= 0 && start <= end && start < length;
    }

    private static Task WriteBytesAsync(Stream output, byte[] bytes, string contentType, bool headOnly,
        CancellationToken cancellationToken) => WriteBytesCoreAsync(output, bytes, contentType, headOnly, cancellationToken);

    private static async Task WriteBytesCoreAsync(Stream output, byte[] bytes, string contentType, bool headOnly,
        CancellationToken cancellationToken)
    {
        await WriteHeadersAsync(output, "200 OK", contentType, bytes.Length,
            "Cache-Control: no-store, no-cache, must-revalidate\r\n", cancellationToken).ConfigureAwait(false);
        if (!headOnly)
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteErrorAsync(Stream output, int code, string reason, CancellationToken cancellationToken,
        string extra = "") => WriteHeadersAsync(output, $"{code} {reason}", "text/plain", 0, extra, cancellationToken);

    private static async Task WriteHeadersAsync(Stream output, string status, string contentType, long contentLength,
        string extra, CancellationToken cancellationToken)
    {
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {contentLength}\r\n" +
            $"Accept-Ranges: bytes\r\nConnection: close\r\n{extra}\r\n");
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _listener.Stop();
        try { _serverTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cancellation.Dispose();
        AppLogger.Write($"DASH bridge stopped for {_manifestPaths.Length} session(s).");
    }
}
