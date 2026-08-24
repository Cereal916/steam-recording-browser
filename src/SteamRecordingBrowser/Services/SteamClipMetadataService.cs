using System.IO;
using System.Text;

namespace SteamRecordingBrowser.Services;

// Reads the small subset of Valve's CGameRecordingClipFile protobuf that is
// useful in the UI. Unknown fields are skipped so Steam can extend the file.
public static class SteamClipMetadataService
{
    public static IReadOnlyList<string> ReadForRecording(string recordingPath)
    {
        var metadataPath = FindClipFile(recordingPath);
        if (metadataPath is null) return Array.Empty<string>();

        try
        {
            var results = new List<string>();
            ParseClip(File.ReadAllBytes(metadataPath), results);
            return results.Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToArray();
        }
        catch (Exception ex)
        {
            AppLogger.Write($"Could not read Steam clip metadata from {metadataPath}: {ex.Message}", "DEBUG");
            return Array.Empty<string>();
        }
    }

    private static string? FindClipFile(string path)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "clip.pb");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    private static void ParseClip(byte[] data, List<string> output)
    {
        foreach (var field in ReadFields(data))
        {
            if (field.Bytes is null) continue;
            switch (field.Number)
            {
                case 1: ParseTimeline(field.Bytes, output); break;
                case 7: AddText(output, "Clip name", field.Bytes); break;
                case 9: AddText(output, "Recorded on", field.Bytes); break;
                case 14: ParseRecordingTag(field.Bytes, output); break;
                case 15: ParsePhase(field.Bytes, output); break;
            }
        }
    }

    private static void ParseTimeline(byte[] data, List<string> output)
    {
        foreach (var field in ReadFields(data))
        {
            if (field.Bytes is null) continue;
            if (field.Number == 6) ParsePhase(field.Bytes, output);
            if (field.Number == 7) ParseEvent(field.Bytes, output);
        }
    }

    private static void ParseEvent(byte[] data, List<string> output)
    {
        string? icon = null;
        string? title = null;
        foreach (var field in ReadFields(data))
        {
            if (field.Bytes is null) continue;
            if (field.Number == 8) icon = Decode(field.Bytes);
            if (field.Number == 9) title = Decode(field.Bytes);
        }
        if (!string.IsNullOrWhiteSpace(title))
        {
            var label = icon?.Contains("achievement", StringComparison.OrdinalIgnoreCase) == true ? "Achievement" : "Event";
            output.Add($"{label}: {title}");
        }
    }

    private static void ParseRecordingTag(byte[] data, List<string> output)
    {
        foreach (var field in ReadFields(data))
            if (field.Number == 2 && field.Bytes is not null)
                ParseNamedPair(field.Bytes, "Tag", output);
    }

    private static void ParsePhase(byte[] data, List<string> output)
    {
        foreach (var field in ReadFields(data))
        {
            if (field.Bytes is null) continue;
            if (field.Number is 6 or 7) ParseNamedPair(field.Bytes, "Session", output);
            if (field.Number == 9) ParseNamedPair(field.Bytes, "Detail", output);
        }
    }

    private static void ParseNamedPair(byte[] data, string fallbackLabel, List<string> output)
    {
        string? first = null;
        string? second = null;
        foreach (var field in ReadFields(data))
        {
            if (field.Bytes is null) continue;
            if (field.Number == 1) first = Decode(field.Bytes);
            if (field.Number == 2) second = Decode(field.Bytes);
        }
        if (!string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second))
            output.Add($"{second}: {first}");
        else if (!string.IsNullOrWhiteSpace(first))
            output.Add($"{fallbackLabel}: {first}");
        else if (!string.IsNullOrWhiteSpace(second))
            output.Add($"{fallbackLabel}: {second}");
    }

    private static void AddText(List<string> output, string label, byte[] data)
    {
        var value = Decode(data);
        if (!string.IsNullOrWhiteSpace(value)) output.Add($"{label}: {value}");
    }

    private static string Decode(byte[] data) => Encoding.UTF8.GetString(data).Trim('\0', ' ', '\r', '\n', '\t');

    private static IEnumerable<ProtoField> ReadFields(byte[] data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            if (!TryReadVarint(data, ref offset, out var key) || key == 0) yield break;
            var number = (int)(key >> 3);
            var wireType = (int)(key & 7);
            switch (wireType)
            {
                case 0:
                    if (!TryReadVarint(data, ref offset, out _)) yield break;
                    yield return new ProtoField(number, null);
                    break;
                case 1:
                    if (offset + 8 > data.Length) yield break;
                    offset += 8;
                    yield return new ProtoField(number, null);
                    break;
                case 2:
                    if (!TryReadVarint(data, ref offset, out var length) || length > int.MaxValue || offset + (int)length > data.Length) yield break;
                    var bytes = data.AsSpan(offset, (int)length).ToArray();
                    offset += (int)length;
                    yield return new ProtoField(number, bytes);
                    break;
                case 5:
                    if (offset + 4 > data.Length) yield break;
                    offset += 4;
                    yield return new ProtoField(number, null);
                    break;
                default:
                    yield break;
            }
        }
    }

    private static bool TryReadVarint(byte[] data, ref int offset, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && offset < data.Length; shift += 7)
        {
            var current = data[offset++];
            value |= (ulong)(current & 0x7f) << shift;
            if ((current & 0x80) == 0) return true;
        }
        return false;
    }

    private sealed record ProtoField(int Number, byte[]? Bytes);
}
