using System.IO;
using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace SteamRecordingBrowser.Services;

public sealed class SteamService
{
    private readonly Dictionary<string, string?> _coverArtCache = new(StringComparer.OrdinalIgnoreCase);

    public string? FindDefaultRecordingRoot()
    {
        var install = GetSteamInstallPath();
        if (string.IsNullOrWhiteSpace(install))
            return null;

        var userdata = Path.Combine(install, "userdata");
        if (!Directory.Exists(userdata))
            return null;

        var candidates = new List<(string Path, bool HasRecordings, DateTime LastWriteUtc)>();

        try
        {
            foreach (var accountDirectory in Directory.EnumerateDirectories(userdata))
            {
                var recordings = Path.Combine(accountDirectory, "gamerecordings");
                if (!Directory.Exists(recordings))
                    continue;

                var hasRecordings = false;
                try
                {
                    hasRecordings = Directory
                        .EnumerateFiles(recordings, "session.mpd", SearchOption.AllDirectories)
                        .Any();
                }
                catch (Exception ex)
                {
                    AppLogger.Write(
                        $"Could not inspect Steam recording folder {recordings}: {ex.Message}",
                        "WARN");
                }

                DateTime lastWriteUtc;
                try
                {
                    lastWriteUtc = Directory.GetLastWriteTimeUtc(recordings);
                }
                catch
                {
                    lastWriteUtc = DateTime.MinValue;
                }

                candidates.Add((recordings, hasRecordings, lastWriteUtc));
            }
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Steam recording-folder discovery failed", ex);
            return null;
        }

        var selected = candidates
            .OrderByDescending(x => x.HasRecordings)
            .ThenByDescending(x => x.LastWriteUtc)
            .Select(x => x.Path)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(selected))
            AppLogger.Write($"Auto-detected Steam recording root: {selected}");

        return selected;
    }

    public IReadOnlyDictionary<string, string> GetInstalledAppNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in GetLibraryPaths())
        {
            var steamApps = System.IO.Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamApps)) continue;

            foreach (var manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                try
                {
                    var text = File.ReadAllText(manifest);
                    var appId = MatchAcf(text, "appid");
                    var name = MatchAcf(text, "name");
                    if (!string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(name))
                        map[appId] = name;
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"Could not parse Steam manifest {manifest}: {ex.Message}", "WARN");
                }
            }
        }

        AppLogger.Write($"Resolved {map.Count} Steam app names.");
        return map;
    }

    public string? FindCachedCoverArt(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
            return null;

        if (_coverArtCache.TryGetValue(appId, out var cached))
            return cached;

        string? result = null;

        try
        {
            var install = GetSteamInstallPath();
            if (install is null)
                return CacheResult();

            var libraryCache = Path.Combine(install, "appcache", "librarycache");
            if (!Directory.Exists(libraryCache))
                return CacheResult();

            // Support both Steam's current per-app/hash layout and its older
            // flat cache naming convention. Prefer portrait library artwork,
            // then fall back to a horizontal library header.
            var appDirectory = Path.Combine(libraryCache, appId);
            if (Directory.Exists(appDirectory))
            {
                result = FindArtwork(appDirectory, "library_600x900")
                         ?? FindArtwork(appDirectory, "library_header");
            }

            result ??= FindExistingFile(
                Path.Combine(libraryCache, $"{appId}_library_600x900.jpg"),
                Path.Combine(libraryCache, $"{appId}_library_600x900.png"),
                Path.Combine(libraryCache, $"{appId}_library_header.jpg"),
                Path.Combine(libraryCache, $"{appId}_header.jpg"));
        }
        catch (Exception ex)
        {
            AppLogger.Write($"Could not resolve cached cover art for app {appId}: {ex.Message}", "WARN");
        }

        return CacheResult();

        string? CacheResult()
        {
            _coverArtCache[appId] = result;
            return result;
        }
    }

    private static string? FindArtwork(string root, string fileNameWithoutExtension)
    {
        foreach (var extension in new[] { ".jpg", ".png", ".webp" })
        {
            var match = Directory
                .EnumerateFiles(root, fileNameWithoutExtension + extension, SearchOption.AllDirectories)
                .FirstOrDefault();

            if (match is not null)
                return match;
        }

        return null;
    }

    private static string? FindExistingFile(params string[] paths) =>
        paths.FirstOrDefault(File.Exists);

    private IEnumerable<string> GetLibraryPaths()
    {
        var install = GetSteamInstallPath();
        if (install is null) yield break;

        yield return install;

        var vdf = System.IO.Path.Combine(install, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        var text = File.ReadAllText(vdf);
        foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
        {
            var path = m.Groups[1].Value.Replace(@"\\", @"\");
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) &&
                !path.Equals(install, StringComparison.OrdinalIgnoreCase))
                yield return path;
        }
    }

    private static string? GetSteamInstallPath()
    {
        foreach (var keyPath in new[]
        {
            @"HKEY_CURRENT_USER\Software\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam"
        })
        {
            foreach (var valueName in new[] { "SteamPath", "InstallPath" })
            {
                var value = Registry.GetValue(keyPath, valueName, null) as string;
                if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
                    return value.Replace('/', '\\');
            }
        }

        foreach (var candidate in new[]
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam"
        })
        {
            if (Directory.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static string MatchAcf(string text, string key)
    {
        var match = Regex.Match(text, $"\"{Regex.Escape(key)}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : "";
    }
}
