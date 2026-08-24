using System.IO;
using System.Diagnostics;
using System.Windows;
using SteamRecordingBrowser.Models;
using SteamRecordingBrowser.Services;

namespace SteamRecordingBrowser;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings = new();
    private readonly MetadataService _metadata;
    private readonly IReadOnlyCollection<RecordingItem> _recordings;
    private bool _initializingLayout;

    public bool RecordingRootChanged { get; private set; }
    public bool MetadataImported { get; private set; }
    public bool ClipLayoutChanged { get; private set; }

    public SettingsWindow(MetadataService metadata, IReadOnlyCollection<RecordingItem> recordings)
    {
        _metadata = metadata;
        _recordings = recordings;
        InitializeComponent();
        var settings = _settings.Load();
        RecordingRootBox.Text = settings.RecordingRoot;
        _initializingLayout = true;
        LayoutSelector.ItemsSource = new[] { "List", "Tiles" };
        LayoutSelector.SelectedItem = settings.UseTileLayout ? "Tiles" : "List";
        _initializingLayout = false;
        UpdateShortcutStatus();
        UpdateLogStorageStatus();
    }

    private void LayoutSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializingLayout)
            return;

        _settings.SaveUseTileLayout(LayoutSelector.SelectedItem as string == "Tiles");
        ClipLayoutChanged = true;
    }

    private void BackupMetadata_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Back up Steam Recording Browser metadata",
            Filter = "JSON metadata (*.json)|*.json",
            FileName = $"SteamRecordingBrowser_metadata_{DateTime.Now:yyyyMMdd_HHmmss}.json"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            _metadata.Backup(dialog.FileName);
            MessageBox.Show(this, "Metadata backup created.", "Steam Recording Browser",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Metadata backup failed", ex);
            MessageBox.Show(this, ex.Message, "Backup failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportMetadata_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Steam Recording Browser metadata",
            Filter = "JSON metadata (*.json)|*.json"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        if (MessageBox.Show(this,
                "Importing will replace the current favorites, descriptions, and tags.\n\n" +
                "A safety backup will be created first. Continue?",
                "Import metadata",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var result = _metadata.Import(dialog.FileName, _recordings);
            MetadataImported = true;

            var message = result.Matched == 0
                ? $"The backup was read, but no current recordings matched.\n\nSafety backup:\n{result.SafetyBackup}"
                : $"Matched clips: {result.Matched}\nFavorites: {result.Favorites}\n" +
                  $"Descriptions: {result.Descriptions}\nTagged clips: {result.Tagged}";

            MessageBox.Show(this, message, "Metadata import",
                MessageBoxButton.OK,
                result.Matched == 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Metadata import failed", ex);
            MessageBox.Show(this, ex.Message, "Import failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseRecordingRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the Steam Game Recording folder",
            Multiselect = false
        };

        if (Directory.Exists(RecordingRootBox.Text))
            dialog.InitialDirectory = RecordingRootBox.Text;

        if (dialog.ShowDialog(this) != true)
            return;

        RecordingRootBox.Text = dialog.FolderName;
        _settings.SaveRecordingRoot(dialog.FolderName);
        RecordingRootChanged = true;
    }

    private void CreateShortcut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DesktopShortcutService.Create();
            UpdateShortcutStatus();

            MessageBox.Show(
                this,
                "The desktop shortcut was created successfully.",
                "Desktop shortcut",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Desktop shortcut creation failed from settings", ex);
            MessageBox.Show(
                this,
                $"The desktop shortcut could not be created.\n\n{ex.Message}",
                "Shortcut creation failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void UpdateShortcutStatus()
    {
        if (DesktopShortcutService.Exists)
        {
            ShortcutStatusText.Text = "A shortcut already exists on your desktop.";
            CreateShortcutButton.Content = "Replace shortcut";
        }
        else
        {
            ShortcutStatusText.Text = "Add a shortcut to your desktop for quick access.";
            CreateShortcutButton.Content = "Create shortcut";
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppLogger.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", AppLogger.LogDirectory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Could not open application log folder", ex);
            MessageBox.Show(this, "The log folder could not be opened.", "Application logs",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ClearArchivedLogs_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Delete all archived log files? The active log will be kept.",
                "Clear archived logs", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            await AppLogger.ClearArchivedLogsAsync();
            UpdateLogStorageStatus();
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Could not clear archived application logs", ex);
            MessageBox.Show(this, "Some archived logs could not be deleted.", "Application logs",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateLogStorageStatus()
    {
        var bytes = AppLogger.GetLogStorageBytes();
        LogStorageStatusText.Text = $"Current log storage: {RecordingItem.FormatBytes(bytes)} (maximum approximately 60 MB).";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
