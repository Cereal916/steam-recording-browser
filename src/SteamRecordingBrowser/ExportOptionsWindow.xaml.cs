using System.Windows;
using System.Windows.Controls;
using SteamRecordingBrowser.Models;
using SteamRecordingBrowser.Services;

namespace SteamRecordingBrowser;

public partial class ExportOptionsWindow : Window
{
    private readonly VideoCodecInfo _sourceCodec;

    public ExportVideoCodec SelectedCodec { get; private set; } = ExportVideoCodec.Original;
    public string SelectedCodecFileLabel => SelectedCodec == ExportVideoCodec.Original
        ? $"{_sourceCodec.ExportCodec?.FileNameLabel() ?? _sourceCodec.DisplayName} Orig"
        : SelectedCodec.FileNameLabel();

    public ExportOptionsWindow(VideoCodecInfo sourceCodec, double durationSeconds, long sourceSizeBytes)
    {
        _sourceCodec = sourceCodec;
        InitializeComponent();
        OriginalCodecText.Text = $"Current: {sourceCodec.DisplayName} • no quality loss";
        OriginalBitrateText.Text = $"Approx. source bitrate: {FormatSourceBitrate(durationSeconds, sourceSizeBytes)}";
        OriginalSizeText.Text = $"Estimated: {RecordingItem.FormatBytes(Math.Max(0, sourceSizeBytes))}";
        H264SizeText.Text = $"Estimated: {FormatTranscodedSize(durationSeconds, 12_000)}";
        HevcSizeText.Text = $"Estimated: {FormatTranscodedSize(durationSeconds, 8_000)}";
        Av1SizeText.Text = $"Estimated: {FormatTranscodedSize(durationSeconds, 6_000)}";
        ApplyFfmpegAvailability();
        UpdateSelection();
    }

    private void ApplyFfmpegAvailability()
    {
        if (FfmpegExportService.IsAvailable)
            return;

        foreach (var button in new[] { H264Button, HevcButton, Av1Button })
        {
            button.IsEnabled = false;
            button.ToolTip = "FFmpeg is required for transcoding. The release package includes it; development builds can use a bundled ffmpeg folder or FFmpeg on PATH.";
            ToolTipService.SetShowOnDisabled(button, true);
        }
    }

    private static string FormatSourceBitrate(double durationSeconds, long sourceSizeBytes)
    {
        if (durationSeconds <= 0 || sourceSizeBytes <= 0)
            return "Unknown";

        var megabitsPerSecond = sourceSizeBytes * 8d / durationSeconds / 1_000_000d;
        return $"{megabitsPerSecond:N1} Mbps total";
    }

    private static string FormatTranscodedSize(double durationSeconds, int videoKbps)
    {
        if (durationSeconds <= 0)
            return "Unknown";

        const int audioKbps = 192;
        const double containerOverheadFactor = 1.01;
        var estimatedBytes = durationSeconds * (videoKbps + audioKbps) * 1000d / 8d * containerOverheadFactor;
        return RecordingItem.FormatBytes((long)Math.Round(estimatedBytes));
    }

    private void CodecButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } &&
            Enum.TryParse<ExportVideoCodec>(value, out var codec))
        {
            SelectedCodec = codec;
            UpdateSelection();
        }
    }

    private void UpdateSelection()
    {
        var buttons = new[] { OriginalButton, H264Button, HevcButton, Av1Button };
        foreach (var button in buttons)
        {
            var selected = button.Tag?.ToString() == SelectedCodec.ToString();
            button.Background = selected
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 48, 68))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(27, 32, 43));
            button.BorderBrush = selected
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 192, 244))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 60, 73));
        }

        (SelectedCodecText.Text, CodecHintText.Text) = SelectedCodec switch
        {
            ExportVideoCodec.H264 => ("H.264 + AAC • Recommended for sharing",
                "Re-encodes for broad compatibility with YouTube, Twitch, TikTok, Instagram, Facebook, browsers, and editors."),
            ExportVideoCodec.Hevc => ("HEVC / H.265 + AAC • Smaller file",
                "Uses less space than H.264 at similar quality, but encoding and social-site processing can take longer."),
            ExportVideoCodec.Av1 => ("AV1 + AAC • Maximum compression",
                "Produces compact files but is the slowest option and is less consistently accepted outside YouTube."),
            _ => ("Original codec • Fastest and lossless",
                "Copies Steam's existing video and audio streams into MP4 without re-encoding. Compatibility depends on the original recording codec.")
        };
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
