using System.IO;
using System.Text;

namespace SteamRecordingBrowser.Services;

public static class SteamAchievementService
{
    public static IReadOnlyDictionary<string, string> ReadNames(string schemaPath, string language)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(schemaPath) || !File.Exists(schemaPath))
            return names;

        try
        {
            using var stream = File.Open(schemaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            var entries = ReadObject(reader);
            CollectAchievementNames(entries, NormalizeLanguage(language), names);
        }
        catch (Exception ex)
        {
            AppLogger.Write($"Could not read Steam achievement schema {schemaPath}: {ex.Message}", "DEBUG");
        }

        return names;
    }

    private static IReadOnlyList<KeyValueEntry> ReadObject(BinaryReader reader)
    {
        var entries = new List<KeyValueEntry>();
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var type = reader.ReadByte();
            if (type == 8)
                break;

            var key = ReadNullTerminatedUtf8(reader);
            switch (type)
            {
                case 0:
                    entries.Add(new KeyValueEntry(key, null, ReadObject(reader)));
                    break;
                case 1:
                    entries.Add(new KeyValueEntry(key, ReadNullTerminatedUtf8(reader), null));
                    break;
                case 2:
                case 3:
                case 4:
                case 6:
                    Skip(reader, sizeof(int));
                    break;
                case 5:
                    ReadNullTerminatedUtf16(reader);
                    break;
                case 7:
                case 9:
                    Skip(reader, sizeof(long));
                    break;
                case 10:
                    Skip(reader, sizeof(byte));
                    break;
                case 11:
                case 12:
                    break;
                default:
                    throw new InvalidDataException($"Unsupported binary KeyValues type {type}.");
            }
        }

        return entries;
    }

    private static void CollectAchievementNames(
        IReadOnlyList<KeyValueEntry> entries,
        string language,
        IDictionary<string, string> names)
    {
        foreach (var entry in entries)
        {
            if (entry.Children is null)
                continue;

            var apiName = GetString(entry.Children, "name");
            var display = GetObject(entry.Children, "display");
            var localizedNames = display is null ? null : GetObject(display, "name");
            var displayName = GetLocalizedString(localizedNames, language);
            if (!string.IsNullOrWhiteSpace(apiName) && !string.IsNullOrWhiteSpace(displayName))
                names[apiName] = displayName;

            CollectAchievementNames(entry.Children, language, names);
        }
    }

    private static string GetLocalizedString(IReadOnlyList<KeyValueEntry>? entries, string language)
    {
        if (entries is null)
            return "";

        return GetString(entries, language) is { Length: > 0 } localized
            ? localized
            : GetString(entries, "english") is { Length: > 0 } english
                ? english
                : entries.FirstOrDefault(entry =>
                    entry.Children is null &&
                    !entry.Key.Equals("token", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(entry.Value))?.Value ?? "";
    }

    private static IReadOnlyList<KeyValueEntry>? GetObject(
        IReadOnlyList<KeyValueEntry> entries,
        string key) => entries.FirstOrDefault(entry =>
            entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && entry.Children is not null)?.Children;

    private static string GetString(IReadOnlyList<KeyValueEntry> entries, string key) =>
        entries.FirstOrDefault(entry =>
            entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && entry.Children is null)?.Value ?? "";

    private static string NormalizeLanguage(string language) => language.Trim().ToLowerInvariant() switch
    {
        "zh-cn" or "zh-hans" or "simplified chinese" => "schinese",
        "zh-tw" or "zh-hant" or "traditional chinese" => "tchinese",
        "ko" or "korean" => "koreana",
        "pt-br" or "brazilian portuguese" => "brazilian",
        "es-419" or "latin american spanish" => "latam",
        { Length: > 0 } value => value,
        _ => "english"
    };

    private static string ReadNullTerminatedUtf8(BinaryReader reader)
    {
        using var buffer = new MemoryStream();
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var value = reader.ReadByte();
            if (value == 0)
                return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            buffer.WriteByte(value);
        }

        throw new EndOfStreamException("Steam schema string was not null terminated.");
    }

    private static void ReadNullTerminatedUtf16(BinaryReader reader)
    {
        while (reader.BaseStream.Position + 1 < reader.BaseStream.Length)
        {
            if (reader.ReadUInt16() == 0)
                return;
        }

        throw new EndOfStreamException("Steam schema wide string was not null terminated.");
    }

    private static void Skip(BinaryReader reader, int byteCount)
    {
        if (reader.BaseStream.Seek(byteCount, SeekOrigin.Current) > reader.BaseStream.Length)
            throw new EndOfStreamException("Steam schema value was truncated.");
    }

    private sealed record KeyValueEntry(
        string Key,
        string? Value,
        IReadOnlyList<KeyValueEntry>? Children);
}
