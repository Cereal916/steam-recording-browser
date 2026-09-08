namespace SteamRecordingBrowser.Models;

public enum SteamTimelineClipPriority
{
    None = 1,
    Standard = 2,
    Featured = 3
}

public sealed record SteamTimelineEvent
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required string TypeLabel { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public string Icon { get; init; } = "";
    public int Priority { get; init; }
    public SteamTimelineClipPriority ClipPriority { get; init; } = SteamTimelineClipPriority.None;
    public required DateTime Timestamp { get; init; }
    public required double PositionSeconds { get; init; }
    public double DurationSeconds { get; init; }

    public bool IsRange => DurationSeconds > 0.001;
    public bool IsSuggestedClip => ClipPriority >= SteamTimelineClipPriority.Standard;
    public bool IsFeaturedClip => ClipPriority == SteamTimelineClipPriority.Featured;
    public string TimeText => RecordingItem.FormatDuration(PositionSeconds);
    public string DurationText => RecordingItem.FormatDuration(DurationSeconds);

    public string BrowserMetaText
    {
        get
        {
            var parts = new List<string> { TypeLabel, TimeText };
            if (IsRange)
                parts.Add(DurationText);
            if (IsFeaturedClip)
                parts.Add("featured clip");
            else if (IsSuggestedClip)
                parts.Add("suggested clip");
            return string.Join("  •  ", parts);
        }
    }

    public string ToolTipText
    {
        get
        {
            var lines = new List<string> { Title, BrowserMetaText };
            if (!string.IsNullOrWhiteSpace(Description))
                lines.Add(Description);
            if (Priority > 0)
                lines.Add($"Steam priority: {Priority:N0}");
            return string.Join(Environment.NewLine, lines);
        }
    }
}
