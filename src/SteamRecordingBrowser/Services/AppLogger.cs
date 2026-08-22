using System.IO;
using System.Text;

namespace SteamRecordingBrowser.Services;

public static class AppLogger
{
    private static readonly object Gate = new();

    public static string LogPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "SteamRecordingBrowser.log");

    public static void Write(string message, string level = "INFO")
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
        }
        catch { }
    }

    public static void WriteException(string message, Exception ex) =>
        Write($"{message}: {ex}", "ERROR");
}
