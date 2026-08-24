using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace SteamRecordingBrowser.Services;

public static class AppLogger
{
    private const int MaximumSessionEntries = 5_000;
    private const long MaximumLogFileBytes = 10 * 1024 * 1024;
    private const int MaximumArchiveFiles = 5;
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim FileGate = new(1, 1);
    private static readonly Queue<LogEntry> SessionEntries = new();
    private static readonly Channel<LogEntry> FileEntries = Channel.CreateBounded<LogEntry>(
        new BoundedChannelOptions(10_000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private static readonly Regex LogLinePattern = new(
        @"^\[(?<timestamp>[^\]]+)\] \[(?<level>[^\]]+)\] (?<message>.*)$",
        RegexOptions.Compiled);

    public static event Action<LogEntry>? EntryWritten;

    private static readonly Task FileWriterTask = Task.Run(WriteFileEntriesAsync);

    public static string LogPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "SteamRecordingBrowser.log");
    public static string LogDirectory => System.IO.Path.GetDirectoryName(LogPath)!;

    public static void Write(string message, string level = "INFO")
    {
        var entry = new LogEntry(DateTime.Now, level.ToUpperInvariant(), SanitizeMessage(message));
        try
        {
            lock (Gate)
            {
                if (SessionEntries.Count == MaximumSessionEntries)
                    SessionEntries.Dequeue();
                SessionEntries.Enqueue(entry);
            }

            FileEntries.Writer.TryWrite(entry);
            try { EntryWritten?.Invoke(entry); } catch { }
        }
        catch { }
    }

    private static async Task WriteFileEntriesAsync()
    {
        StreamWriter? writer = null;
        try
        {
            await foreach (var entry in FileEntries.Reader.ReadAllAsync())
            {
                try
                {
                    await FileGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        writer ??= CreateWriter();
                        var line = entry.ToLogText();
                        var lineBytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                        if (writer.BaseStream.Length + lineBytes > MaximumLogFileBytes)
                        {
                            await writer.DisposeAsync().ConfigureAwait(false);
                            writer = null;
                            TryRotateArchives();
                            writer = CreateWriter();
                        }
                        await writer.WriteLineAsync(line).ConfigureAwait(false);
                    }
                    finally
                    {
                        FileGate.Release();
                    }
                }
                catch
                {
                    if (writer is not null)
                        await writer.DisposeAsync();
                    writer = null;
                }
            }
        }
        finally
        {
            if (writer is not null)
                await writer.DisposeAsync();
        }
    }

    private static StreamWriter CreateWriter() =>
        new(new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(false)) { AutoFlush = true };

    private static bool TryRotateArchives()
    {
        var rotatingPath = LogPath + ".rotating";
        try
        {
            if (File.Exists(rotatingPath))
                File.Delete(rotatingPath);
            if (File.Exists(LogPath))
                File.Move(LogPath, rotatingPath);

            var oldest = GetArchivePath(MaximumArchiveFiles);
            if (File.Exists(oldest))
                File.Delete(oldest);
            for (var index = MaximumArchiveFiles - 1; index >= 1; index--)
            {
                var source = GetArchivePath(index);
                if (File.Exists(source))
                    File.Move(source, GetArchivePath(index + 1), overwrite: true);
            }
            if (File.Exists(rotatingPath))
                File.Move(rotatingPath, GetArchivePath(1), overwrite: true);
            return true;
        }
        catch
        {
            // A user may have a log open in an editor that does not permit
            // renaming. Continue writing the active file and retry rotation
            // on the next entry instead of losing diagnostics or crashing.
            try
            {
                if (File.Exists(rotatingPath) && !File.Exists(LogPath))
                    File.Move(rotatingPath, LogPath);
            }
            catch { }
            return false;
        }
    }

    private static string GetArchivePath(int index) =>
        System.IO.Path.Combine(LogDirectory, $"SteamRecordingBrowser.{index}.log");

    public static long GetLogStorageBytes()
    {
        try
        {
            return EnumerateManagedLogFiles().Where(File.Exists).Sum(path => new FileInfo(path).Length);
        }
        catch { return 0; }
    }

    public static async Task ClearArchivedLogsAsync()
    {
        await FileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            for (var index = 1; index <= MaximumArchiveFiles; index++)
            {
                var path = GetArchivePath(index);
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
        finally
        {
            FileGate.Release();
        }
    }

    public static async Task FlushAndStopAsync()
    {
        FileEntries.Writer.TryComplete();
        await FileWriterTask.ConfigureAwait(false);
    }

    private static IEnumerable<string> EnumerateManagedLogFiles()
    {
        yield return LogPath;
        for (var index = 1; index <= MaximumArchiveFiles; index++)
            yield return GetArchivePath(index);
    }

    private static string SanitizeMessage(string message)
    {
        var sanitized = message;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
            sanitized = sanitized.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        sanitized = Regex.Replace(sanitized,
            @"(?i)(token|api[_-]?key|authorization|password)=([^\s&]+)", "$1=<redacted>");
        return Regex.Replace(sanitized, @"(?i)(https?://[^\s?]+)\?[^\s]+", "$1?<redacted>");
    }

    public static void WriteException(string message, Exception ex) =>
        Write($"{message}: {ex}", "ERROR");

    public static IReadOnlyList<LogEntry> ReadRecentEntries(int maximumEntries)
    {
        if (maximumEntries <= 0)
            return Array.Empty<LogEntry>();

        try
        {
            lock (Gate)
            {
                if (!File.Exists(LogPath))
                    return Array.Empty<LogEntry>();

                const int maximumBytes = 2 * 1024 * 1024;
                using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var start = Math.Max(0, stream.Length - maximumBytes);
                stream.Seek(start, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8, true, leaveOpen: false);
                if (start > 0)
                    reader.ReadLine();

                var entries = new Queue<LogEntry>(maximumEntries);
                LogEntry? current = null;
                while (reader.ReadLine() is { } line)
                {
                    var match = LogLinePattern.Match(line);
                    if (match.Success && DateTime.TryParse(match.Groups["timestamp"].Value, out var timestamp))
                    {
                        if (current is not null)
                            AddBounded(entries, current, maximumEntries);
                        current = new LogEntry(timestamp, match.Groups["level"].Value, match.Groups["message"].Value);
                    }
                    else if (current is not null)
                    {
                        current = current with { Message = current.Message + Environment.NewLine + line };
                    }
                }

                if (current is not null)
                    AddBounded(entries, current, maximumEntries);
                return entries.ToArray();
            }
        }
        catch
        {
            return Array.Empty<LogEntry>();
        }
    }

    public static IReadOnlyList<LogEntry> ReadCurrentSessionEntries(int maximumEntries)
    {
        if (maximumEntries <= 0)
            return Array.Empty<LogEntry>();

        lock (Gate)
            return SessionEntries.TakeLast(maximumEntries).ToArray();
    }

    private static void AddBounded(Queue<LogEntry> entries, LogEntry entry, int maximumEntries)
    {
        if (entries.Count == maximumEntries)
            entries.Dequeue();
        entries.Enqueue(entry);
    }
}

public sealed record LogEntry(DateTime Timestamp, string Level, string Message)
{
    public string DisplayText => $"{Timestamp:HH:mm:ss.fff}  {Level,-5}  {Message}";
    public string ToLogText() => $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level}] {Message}";
}
