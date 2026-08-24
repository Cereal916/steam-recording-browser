using System.IO;
using System.Globalization;
using System.Xml.Linq;

namespace SteamRecordingBrowser.Services;

public static class LiveRecordingService
{
    private static readonly TimeSpan ActiveWriteWindow = TimeSpan.FromSeconds(8);

    public static bool IsActivelyRecording(string manifestPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(manifestPath);
            if (directory is null || !File.Exists(manifestPath))
                return false;

            var root = XDocument.Load(manifestPath).Root;
            if (!string.Equals(root?.Attribute("type")?.Value, "dynamic", StringComparison.OrdinalIgnoreCase))
                return false;

            var newestWrite = DateTime.MinValue;
            foreach (var segment in Directory.EnumerateFiles(directory, "*.m4s", SearchOption.TopDirectoryOnly))
            {
                var write = File.GetLastWriteTimeUtc(segment);
                if (write > newestWrite)
                    newestWrite = write;
            }

            return newestWrite != DateTime.MinValue &&
                   DateTime.UtcNow - newestWrite <= ActiveWriteWindow;
        }
        catch
        {
            return false;
        }
    }

    public static double GetDynamicDurationSeconds(string manifestPath)
    {
        try
        {
            var root = XDocument.Load(manifestPath).Root;
            if (!string.Equals(root?.Attribute("type")?.Value, "dynamic", StringComparison.OrdinalIgnoreCase) ||
                !DateTime.TryParse(root?.Attribute("availabilityStartTime")?.Value,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start))
                return 0;

            return Math.Clamp((DateTime.UtcNow - start.ToUniversalTime()).TotalSeconds, 0, 2 * 60 * 60);
        }
        catch
        {
            return 0;
        }
    }
}
