using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamRecordingBrowser.Services;

public static class AppLogger
{
    private const int MaximumSessionEntries = 5_000;
    private static readonly object Gate = new();
    private static readonly Queue<LogEntry> SessionEntries = new();
    private static readonly Regex LogLinePattern = new(
        @"^\[(?<timestamp>[^\]]+)\] \[(?<level>[^\]]+)\] (?<message>.*)$",
        RegexOptions.Compiled);

    public static event Action<LogEntry>? EntryWritten;

    public static string LogPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "SteamRecordingBrowser.log");

    public static void Write(string message, string level = "INFO")
    {
        var entry = new LogEntry(DateTime.Now, level.ToUpperInvariant(), message);
        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    LogPath,
                    entry.ToLogText() + Environment.NewLine,
                    new UTF8Encoding(false));
                if (SessionEntries.Count == MaximumSessionEntries)
                    SessionEntries.Dequeue();
                SessionEntries.Enqueue(entry);
            }

            try { EntryWritten?.Invoke(entry); } catch { }
        }
        catch { }
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
