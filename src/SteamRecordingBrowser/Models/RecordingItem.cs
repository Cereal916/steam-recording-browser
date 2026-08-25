using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SteamRecordingBrowser.Models;

public sealed class RecordingItem : INotifyPropertyChanged
{
    private bool _isFavorite;
    private string _description = "";
    private IReadOnlyList<string> _tags = Array.Empty<string>();
    private bool _isLive;

    public required string Path { get; init; }
    public required string Folder { get; init; }
    public required string GameId { get; init; }
    public required string GameName { get; init; }
    public required DateTime Timestamp { get; init; }
    public DateTime PlaybackStartTime { get; init; }
    public required long SizeBytes { get; init; }
    public required double DurationSeconds { get; init; }
    public string? ThumbnailPath { get; init; }
    public string? CoverArtPath { get; init; }
    public string? DisplayImagePath => ThumbnailPath ?? CoverArtPath;
    public bool IsAutoRecording { get; init; }
    public bool IsSavedClip => !IsAutoRecording;
    public IReadOnlyList<string> SessionPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<double> SessionStartOffsetsSeconds { get; init; } = Array.Empty<double>();
    public IReadOnlyList<DateTime> SessionStartTimes { get; init; } = Array.Empty<DateTime>();
    public string VideoCodec { get; init; } = "Unknown";
    public string AudioCodec { get; init; } = "Unknown";
    public string Resolution { get; init; } = "Unknown";
    public string FrameRate { get; init; } = "Unknown";
    public string Bitrate { get; init; } = "Unknown";
    public IReadOnlyList<string> SteamMetadata { get; init; } = Array.Empty<string>();

    public bool IsLive
    {
        get => _isLive;
        set
        {
            if (_isLive == value) return;
            _isLive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RecordingTypeLabel));
            OnPropertyChanged(nameof(VideoInfoText));
            OnPropertyChanged(nameof(SupportsAnnotations));
            OnPropertyChanged(nameof(AnnotationDescription));
            OnPropertyChanged(nameof(AnnotationTagDisplay));
            OnPropertyChanged(nameof(AnnotationTagsText));
        }
    }

    public string RecordingTypeLabel => IsLive ? "LIVE • AUTO RECORDING" :
        IsAutoRecording ? "AUTO RECORDING" : "SAVED CLIP";
    public bool SupportsAnnotations => IsSavedClip && !IsLive;

    public string DisplayTime => Timestamp.ToString("MMM d, yyyy  h:mm:ss tt");
    public string DurationText => DurationSeconds > 0 ? FormatDuration(DurationSeconds) : "—";
    public string SizeText => FormatBytes(SizeBytes);
    public int SessionCount => Math.Max(1, SessionPaths.Count);
    public string SteamMetadataDisplay => string.Join("; ", SteamMetadata);
    public string VideoInfoText
    {
        get
        {
            var lines = new List<string>
            {
                GameName,
                $"Type: {RecordingTypeLabel}",
                $"Recorded: {DisplayTime}",
                $"Duration: {DurationText}",
                $"Size: {SizeText}",
                $"Video: {VideoCodec} • {Resolution} • {FrameRate}",
                $"Video bitrate: {Bitrate}",
                $"Audio: {AudioCodec}"
            };
            if (SessionPaths.Count > 1)
                lines.Add($"Gameplay sessions: {SessionPaths.Count}");
            if (SteamMetadata.Count > 0)
            {
                lines.Add("");
                lines.Add("Steam metadata");
                lines.AddRange(SteamMetadata.Select(value => $"• {value}"));
            }
            lines.Add("");
            lines.Add($"Location: {Path}");
            return string.Join(Environment.NewLine, lines);
        }
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FavoriteGlyph));
            OnPropertyChanged(nameof(TableFavoriteGlyph));
        }
    }

    public string FavoriteGlyph => IsFavorite ? "★" : "";
    public string TableFavoriteGlyph => IsFavorite ? "★" : "☆";

    public string Description
    {
        get => _description;
        set
        {
            if (_description == value) return;
            _description = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(AnnotationDescription));
        }
    }

    public IReadOnlyList<string> Tags
    {
        get => _tags;
        set
        {
            _tags = value ?? Array.Empty<string>();
            OnPropertyChanged();
            OnPropertyChanged(nameof(TagDisplay));
            OnPropertyChanged(nameof(AnnotationTagDisplay));
            OnPropertyChanged(nameof(AnnotationTagsText));
        }
    }

    public string TagDisplay => Tags.Count > 0 ? "Tags: " + string.Join(", ", Tags) : "";
    public string TagsText => string.Join(", ", Tags);
    public string AnnotationDescription => SupportsAnnotations ? Description : "";
    public string AnnotationTagDisplay => SupportsAnnotations ? TagDisplay : "";
    public string AnnotationTagsText => SupportsAnnotations ? TagsText : "";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}";
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024 * 1024):N1} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024d * 1024):N0} MB";
        if (bytes >= 1024L) return $"{bytes / 1024d:N0} KB";
        return $"{bytes:N0} B";
    }
}
