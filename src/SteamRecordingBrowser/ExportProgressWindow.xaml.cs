using System.Text.RegularExpressions;
using System.Windows;
using SteamRecordingBrowser.Services;

namespace SteamRecordingBrowser;

public partial class ExportProgressWindow : Window
{
    private readonly Func<IProgress<string>, CancellationToken, Task> _export;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _finished;

    public ExportProgressWindow(Func<IProgress<string>, CancellationToken, Task> export)
    {
        _export = export;
        InitializeComponent();
        Loaded += ExportProgressWindow_Loaded;
        Closing += (_, args) =>
        {
            if (_finished) return;
            args.Cancel = true;
            RequestCancellation();
        };
    }

    private async void ExportProgressWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var progress = new Progress<string>(UpdateProgress);
        try
        {
            await _export(progress, _cancellation.Token);
            _finished = true;
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            _finished = true;
            DialogResult = false;
            Close();
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("MP4 export failed", ex);
            _finished = true;
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
            Close();
        }
    }

    private void UpdateProgress(string status)
    {
        StatusText.Text = status;
        var match = Regex.Match(status, @"(?<percent>\d+(?:\.\d+)?)%");
        if (!match.Success || !double.TryParse(match.Groups["percent"].Value, out var percent))
            return;

        ExportProgress.Value = Math.Clamp(percent, 0, 100);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => RequestCancellation();

    private void RequestCancellation()
    {
        if (_cancellation.IsCancellationRequested) return;
        StatusText.Text = "Cancelling export…";
        CancelButton.IsEnabled = false;
        _cancellation.Cancel();
    }
}
