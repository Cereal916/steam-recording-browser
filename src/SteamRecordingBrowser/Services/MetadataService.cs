using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamRecordingBrowser.Models;

namespace SteamRecordingBrowser.Services;

public sealed class MetadataService
{
    private readonly Dictionary<string, MetadataEntry> _byKey =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, MetadataEntry> _byPath =
        new(StringComparer.OrdinalIgnoreCase);

    public string MetadataRoot { get; }

    public string LibraryPath => System.IO.Path.Combine(MetadataRoot, "library.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public MetadataService(string? metadataRoot = null)
    {
        MetadataRoot = metadataRoot ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamRecordingBrowser");
        Directory.CreateDirectory(MetadataRoot);
    }

    public void Load()
    {
        _byKey.Clear();
        _byPath.Clear();

        if (!File.Exists(LibraryPath))
        {
            AppLogger.Write($"Metadata store not found yet: {LibraryPath}");
            return;
        }

        if (LoadFromJson(File.ReadAllText(LibraryPath)))
        {
            var safety = System.IO.Path.Combine(
                MetadataRoot,
                $"library_before_clip_identity_{DateTime.Now:yyyyMMdd_HHmmss_fffffff}.json");
            File.Copy(LibraryPath, safety);
            Save();
            AppLogger.Write($"Migrated saved-clip metadata identities. Original metadata saved to {safety}");
        }
        AppLogger.Write($"Loaded {_byKey.Count} metadata entries.");
    }

    private bool LoadFromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        IEnumerable<JsonElement> rows;

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            rows = doc.RootElement.EnumerateArray();
        else if (doc.RootElement.TryGetProperty("Entries", out var entries) &&
                 entries.ValueKind == JsonValueKind.Array)
            rows = entries.EnumerateArray();
        else
            throw new InvalidDataException("Metadata JSON contains neither an entry array nor an Entries property.");

        var loaded = new List<(MetadataEntry Entry, bool Migrated)>();
        foreach (var row in rows)
        {
            var entry = new MetadataEntry
            {
                RecordingKey = ReadString(row, "RecordingKey"),
                Path = ReadString(row, "Path"),
                Favorite = ReadBool(row, "Favorite"),
                Description = ReadString(row, "Description"),
                Tags = ReadTags(row)
            };

            var migrated = false;
            if (!string.IsNullOrWhiteSpace(entry.Path))
            {
                var pathKey = GetRecordingKey(entry.Path);
                if (pathKey.StartsWith("clip:", StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(entry.RecordingKey) ||
                     entry.RecordingKey.StartsWith("bg:", StringComparison.OrdinalIgnoreCase) ||
                     entry.RecordingKey.StartsWith("path:", StringComparison.OrdinalIgnoreCase)))
                {
                    // Old session keys shared one entry across multiple clips. Only
                    // its recorded path identifies an owner; never copy it to siblings.
                    entry.RecordingKey = pathKey;
                    migrated = true;
                }
                else if (string.IsNullOrWhiteSpace(entry.RecordingKey))
                    entry.RecordingKey = pathKey;
            }

            if (string.IsNullOrWhiteSpace(entry.RecordingKey))
                continue;

            entry.Tags = NormalizeTags(entry.Tags);
            loaded.Add((entry, migrated));
        }

        // An explicit clip entry takes precedence over a migrated legacy entry,
        // regardless of JSON order. Build path aliases only for surviving entries.
        foreach (var (entry, _) in loaded.OrderByDescending(row => row.Migrated))
            _byKey[entry.RecordingKey] = entry;

        foreach (var entry in _byKey.Values)
        {
            var normalized = NormalizePath(entry.Path);
            if (normalized.Length > 0)
                _byPath[normalized] = entry;
        }

        return loaded.Any(row => row.Migrated);
    }

    public MetadataEntry ForRecording(string recordingPath)
    {
        var key = GetRecordingKey(recordingPath);

        if (_byKey.TryGetValue(key, out var existing))
        {
            existing.Path = recordingPath;
            _byPath[NormalizePath(recordingPath)] = existing;
            return existing;
        }

        var normalized = NormalizePath(recordingPath);
        if (_byPath.TryGetValue(normalized, out var legacy) &&
            legacy.RecordingKey.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
        {
            _byKey.Remove(legacy.RecordingKey);
            legacy.RecordingKey = key;
            legacy.Path = recordingPath;
            _byKey[key] = legacy;
            return legacy;
        }

        var created = new MetadataEntry { RecordingKey = key, Path = recordingPath };
        _byKey[key] = created;
        _byPath[normalized] = created;
        return created;
    }

    public void ApplyTo(RecordingItem item)
    {
        var m = ForRecording(item.Path);
        item.IsFavorite = m.Favorite;
        item.Description = m.Description;
        item.Tags = m.Tags.ToArray();
    }

    public void UpdateFrom(RecordingItem item)
    {
        var m = ForRecording(item.Path);
        m.Favorite = item.IsFavorite;
        m.Description = item.Description ?? "";
        m.Tags = NormalizeTags(item.Tags).ToList();
        m.Path = item.Path;
        Save();
    }

    public IReadOnlyList<string> GetKnownTags() =>
        NormalizeTags(_byKey.Values.SelectMany(entry => entry.Tags));

    public void Save()
    {
        var meaningful = _byKey.Values
            .Where(x => x.Favorite || !string.IsNullOrWhiteSpace(x.Description) || x.Tags.Count > 0)
            .GroupBy(x => x.RecordingKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.RecordingKey)
            .ToList();

        var document = new MetadataDocument { Entries = meaningful };
        File.WriteAllText(LibraryPath, JsonSerializer.Serialize(document, JsonOptions));
    }

    public void Backup(string destination)
    {
        Save();
        File.Copy(LibraryPath, destination, true);
    }

    public MetadataImportResult Import(string source, IReadOnlyCollection<RecordingItem> currentItems)
    {
        var json = File.ReadAllText(source);
        using var _ = JsonDocument.Parse(json); // validate before replacing anything

        Save();

        var safety = System.IO.Path.Combine(
            MetadataRoot,
            $"library_before_import_{DateTime.Now:yyyyMMdd_HHmmss}.json");

        if (File.Exists(LibraryPath))
            File.Copy(LibraryPath, safety, true);

        _byKey.Clear();
        _byPath.Clear();
        LoadFromJson(json);

        foreach (var item in currentItems)
            ApplyTo(item);

        Save();

        var matched = currentItems.Where(HasMeaningfulMetadata).ToList();
        return new MetadataImportResult(
            matched.Count,
            matched.Count(x => x.IsFavorite),
            matched.Count(x => !string.IsNullOrWhiteSpace(x.Description)),
            matched.Count(x => x.Tags.Count > 0),
            safety);
    }

    private bool HasMeaningfulMetadata(RecordingItem item)
    {
        var key = GetRecordingKey(item.Path);
        if (!_byKey.TryGetValue(key, out var m)) return false;
        return m.Favorite || !string.IsNullOrWhiteSpace(m.Description) || m.Tags.Count > 0;
    }

    public static string GetRecordingKey(string path)
    {
        string? backgroundKey = null;
        try
        {
            var dir = new DirectoryInfo(System.IO.Path.GetDirectoryName(path)!);
            while (dir is not null)
            {
                // Resolve from the path alone so imports and disconnected drives
                // retain the same identity. A clip overrides its nested video session.
                if (Regex.IsMatch(dir.Name, @"\Aclip_[0-9]+_[0-9]{8}_[0-9]{6}(?:_[0-9]+)?\z",
                    RegexOptions.IgnoreCase))
                    return "clip:" + dir.Name[5..].Replace('_', ':');

                var match = Regex.Match(
                    dir.Name,
                    @"^bg_(\d+)_(\d{8})_(\d{6})$",
                    RegexOptions.IgnoreCase);

                if (match.Success)
                    backgroundKey ??= $"bg:{match.Groups[1].Value}:{match.Groups[2].Value}:{match.Groups[3].Value}";

                dir = dir.Parent;
            }
        }
        catch { }

        return backgroundKey ?? "path:" + NormalizePath(path);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return System.IO.Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant(); }
        catch { return path.Trim().TrimEnd('\\').ToLowerInvariant(); }
    }

    public static List<string> NormalizeTags(IEnumerable<string>? tags) =>
        (tags ?? Array.Empty<string>())
            .SelectMany(t => (t ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ReadString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static bool ReadBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False && p.GetBoolean();

    private static List<string> ReadTags(JsonElement e, string name = "Tags")
    {
        if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array)
            return new();

        return p.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? "")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }
}

public sealed record MetadataImportResult(
    int Matched,
    int Favorites,
    int Descriptions,
    int Tagged,
    string SafetyBackup);
