using System.Collections.ObjectModel;
using SteamRecordingBrowser.Services;

namespace SteamRecordingBrowser.Models;

public sealed class TagEditorModel
{
    private readonly Dictionary<string, string> _knownTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<string> _tags = new();

    public ReadOnlyObservableCollection<string> Tags { get; }

    public TagEditorModel(IEnumerable<string> initialTags, IEnumerable<string> knownTags)
    {
        Tags = new ReadOnlyObservableCollection<string>(_tags);
        foreach (var tag in MetadataService.NormalizeTags(knownTags))
            _knownTags.TryAdd(tag, tag);
        AddTags(string.Join(",", initialTags));
    }

    public IReadOnlyList<string> GetSuggestions(string query)
    {
        query = query.Trim();
        var selected = _tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _knownTags.Values
            .Where(tag => !selected.Contains(tag) && tag.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(tag => tag.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public int AddTags(string text)
    {
        var added = 0;
        foreach (var tag in MetadataService.NormalizeTags(new[] { text }))
        {
            if (_tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                continue;

            // Reuse the spelling of an existing tag, including when it is typed or pasted.
            var canonical = _knownTags.GetValueOrDefault(tag, tag);
            _knownTags.TryAdd(canonical, canonical);
            _tags.Add(canonical);
            added++;
        }

        return added;
    }

    public bool RemoveTag(string tag)
    {
        var existing = _tags.FirstOrDefault(value => value.Equals(tag, StringComparison.OrdinalIgnoreCase));
        return existing is not null && _tags.Remove(existing);
    }
}
