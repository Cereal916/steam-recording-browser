using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SteamRecordingBrowser.Models;

public sealed class RecordingItem : INotifyPropertyChanged
{
    private bool _isFavorite;
    private string _description = "";
    private IReadOnlyList<string> _tags = Array.Empty<string>();

    public required string Path { get; init; }
    public required string Folder { get; init; }
    public required string GameId { get; init; }
    public required string GameName { get; init; }
    public required DateTime Timestamp { get; init; }
    public required long SizeBytes { get; init; }
    public required double DurationSeconds { get; init; }
    public string? ThumbnailPath { get; init; }
    public string? CoverArtPath { get; init; }
    public string? DisplayImagePath => ThumbnailPath ?? CoverArtPath;

    public string DisplayTime => Timestamp.ToString("MMM d, yyyy  h:mm:ss tt");
    public string DurationText => DurationSeconds > 0 ? FormatDuration(DurationSeconds) : "—";
    public string SizeText => FormatBytes(SizeBytes);

    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (_isFavorite != value) { _isFavorite = value; OnPropertyChanged(); OnPropertyChanged(nameof(FavoriteGlyph)); } }
    }

    public string FavoriteGlyph => IsFavorite ? "★" : "";

    public string Description
    {
        get => _description;
        set { if (_description != value) { _description = value ?? ""; OnPropertyChanged(); } }
    }

    public IReadOnlyList<string> Tags
    {
        get => _tags;
        set
        {
            _tags = value ?? Array.Empty<string>();
            OnPropertyChanged();
            OnPropertyChanged(nameof(TagDisplay));
        }
    }

    public string TagDisplay => Tags.Count > 0 ? "Tags: " + string.Join(", ", Tags) : "";

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
