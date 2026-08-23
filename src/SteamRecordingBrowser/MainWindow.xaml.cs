using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MediaColor = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using TimelineLine = System.Windows.Shapes.Line;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;
using SteamRecordingBrowser.Dialogs;
using SteamRecordingBrowser.Models;
using SteamRecordingBrowser.Services;

namespace SteamRecordingBrowser;

public partial class MainWindow : Window
{
    private readonly MetadataService _metadata = new();
    private readonly DashCompatibilityService _dash = new();
    private readonly SteamService _steam = new();
    private readonly SettingsService _settings = new();
    private readonly RecordingScanner _scanner;
    private readonly LibVlcService _vlc;
    private readonly IProgress<StartupProgress>? _startupProgress;
    private bool _initialLoadCompleted;

    public event EventHandler? InitialLoadCompleted;

    public void SetStartupInteractionBlocked(bool isBlocked)
    {
        StartupInputBlocker.Visibility = isBlocked
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private readonly List<RecordingItem> _allItems = new();
    private readonly ObservableCollection<RecordingItem> _visibleItems = new();

    private CancellationTokenSource? _scanCancellation;
    private string _selectedGameId = "";
    private string _selectedTag = "";
    private string _sortMode = "Newest";
    private string _recordingRoot = "";
    private bool _clipLayoutChangedFromSettings;

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var recordingRootChanged = ShowSettings();

        if (_clipLayoutChangedFromSettings)
            await ApplyClipLayoutTransitionAsync();

        if (recordingRootChanged && Directory.Exists(_recordingRoot))
            await LoadRecordingsAsync();
    }

    // Clip-card previews intentionally share exactly one libVLC decoder.
    private readonly MediaPlayer _clipPreviewPlayer;
    private readonly DispatcherTimer _clipPreviewDelayTimer;
    private Media? _clipPreviewMedia;
    private RecordingItem? _clipPreviewItem;
    private FrameworkElement? _clipPreviewCard;
    private long _clipPreviewGeneration;
    private long _clipPreviewRevealGeneration;
    private bool _clipPreviewRevealPending;

    public MainWindow(IProgress<StartupProgress>? startupProgress = null)
    {
        _startupProgress = startupProgress;

        ReportStartup(21, "Creating main window…");
        InitializeComponent();
        VersionText.Text = $"v{AppInfo.Version}";

        ReportStartup(25, "Loading clip metadata…");
        _metadata.Load();

        ReportStartup(31, "Initializing recording services…");
        _scanner = new RecordingScanner(_steam, _dash, _metadata);
        _vlc = new LibVlcService(_dash);

        _clipPreviewPlayer = new MediaPlayer(_vlc.LibVlc)
        {
            Mute = true
        };
        ClipPreviewVideoView.MediaPlayer = _clipPreviewPlayer;

        _clipPreviewDelayTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _clipPreviewDelayTimer.Tick += ClipPreviewDelayTimer_Tick;

        _clipPreviewPlayer.EncounteredError += ClipPreviewPlayer_EncounteredError;
        _clipPreviewPlayer.EndReached += ClipPreviewPlayer_EndReached;
        _clipPreviewPlayer.TimeChanged += ClipPreviewPlayer_TimeChanged;

        RecordingList.ItemsSource = _visibleItems;
        TileRecordingList.ItemsSource = _visibleItems;
        ApplyClipLayout();

        SortFilter.ItemsSource = new[] { "Newest", "Oldest", "Largest", "Smallest" };
        SortFilter.SelectedItem = "Newest";

        ReportStartup(35, "Loading recording settings…");
        InitializeRecordingRoot();
        ReportStartup(39, "Recording folder ready…");

        Loaded += async (_, _) =>
        {
            try
            {
                ReportStartup(41, "Preparing recording library…");

                if (!Directory.Exists(_recordingRoot))
                {
                    ShowSettings();

                    if (_clipLayoutChangedFromSettings)
                        ApplyClipLayout();

                    if (!Directory.Exists(_recordingRoot))
                    {
                        StatusText.Text = "Choose a Steam Game Recording folder in Settings to begin.";
                        ReportStartup(100, "Ready — choose a recording folder in Settings.");
                        return;
                    }
                }

                await LoadRecordingsAsync(isInitialLoad: true);
            }
            finally
            {
                CompleteInitialLoad();
            }
        };
        Closed += (_, _) =>
        {
            _scanCancellation?.Cancel();

            StopClipPreview(closePopup: true);

            _clipPreviewDelayTimer.Stop();
            _clipPreviewDelayTimer.Tick -= ClipPreviewDelayTimer_Tick;
            _clipPreviewPlayer.EncounteredError -= ClipPreviewPlayer_EncounteredError;
            _clipPreviewPlayer.EndReached -= ClipPreviewPlayer_EndReached;
            _clipPreviewPlayer.TimeChanged -= ClipPreviewPlayer_TimeChanged;

            ClipPreviewVideoView.MediaPlayer = null;
            _clipPreviewPlayer.Dispose();

            _vlc.Dispose();
        };

        AppLogger.Write("============================================================");
        AppLogger.Write($"Steam Recording Browser v{AppInfo.Version} starting.");
        AppLogger.Write($".NET runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        AppLogger.Write("Runtime architecture: native C# WPF + bundled libVLC.");
    }

    private async Task LoadRecordingsAsync(bool isInitialLoad = false)
    {
        StopClipPreview(closePopup: true);

        _scanCancellation?.Cancel();
        _scanCancellation = new CancellationTokenSource();

        var root = _recordingRoot;

        if (!Directory.Exists(root))
        {
            StatusText.Text = "Choose a valid Steam Game Recording folder.";
            return;
        }

        _settings.SaveRecordingRoot(root);

        StatusText.Text = "Scanning recordings…";
        LoadProgress.Visibility = Visibility.Visible;
        LoadProgress.Value = 0;

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                LoadProgress.Maximum = Math.Max(1, p.Total);
                LoadProgress.Value = p.Current;

                if (p.Total == 0)
                {
                    var stageStatus = string.IsNullOrWhiteSpace(p.CurrentGame)
                        ? "Scanning recordings…"
                        : p.CurrentGame;

                    StatusText.Text = stageStatus;

                    if (isInitialLoad)
                    {
                        var stagePercent = stageStatus.StartsWith("Resolving", StringComparison.OrdinalIgnoreCase)
                            ? 45d
                            : stageStatus.StartsWith("Finding", StringComparison.OrdinalIgnoreCase)
                                ? 49d
                                : 52d;

                        ReportStartup(stagePercent, stageStatus);
                    }
                }
                else
                {
                    StatusText.Text =
                        $"Loading {p.Current} of {p.Total}: {p.CurrentGame}";

                    if (isInitialLoad)
                    {
                        var fraction = Math.Clamp(
                            p.Current / (double)Math.Max(1, p.Total),
                            0d,
                            1d);

                        // The clip scan is the expensive part of startup, so
                        // dedicate most of the determinate bar to real scan
                        // completion rather than arbitrary timed animation.
                        var percent = 52d + fraction * 40d;

                        ReportStartup(
                            percent,
                            $"Loading clip {p.Current} of {p.Total}: {p.CurrentGame}");
                    }
                }
            });

            var items = await _scanner.ScanAsync(root, progress, _scanCancellation.Token);

            _allItems.Clear();
            _allItems.AddRange(items);

            if (isInitialLoad)
                ReportStartup(94, "Building game and tag filters…");

            UpdateGameFilter();
            UpdateTagFilter();

            if (isInitialLoad)
                ReportStartup(97, "Preparing clip browser…");

            ApplyFilter();

            StatusText.Text = BuildStorageSummary(root);

            if (isInitialLoad)
                ReportStartup(100, $"Ready — loaded {_allItems.Count} clips.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Scan canceled.";
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Recording scan failed", ex);
            StatusText.Text = ex.Message;
            WpfMessageBox.Show(this, ex.Message, "Scan failed", WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
        }
        finally
        {
            LoadProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void ReportStartup(double percent, string status)
    {
        if (_initialLoadCompleted)
            return;

        _startupProgress?.Report(new StartupProgress(percent, status));
    }

    private void CompleteInitialLoad()
    {
        if (_initialLoadCompleted)
            return;

        _initialLoadCompleted = true;
        InitialLoadCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyFilter()
    {
        IEnumerable<RecordingItem> query = _allItems;

        if (!string.IsNullOrWhiteSpace(_selectedGameId))
            query = query.Where(x => x.GameId.Equals(_selectedGameId, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(_selectedTag))
            query = query.Where(x => x.Tags.Any(t => t.Equals(_selectedTag, StringComparison.OrdinalIgnoreCase)));

        if (FavoritesOnly.IsChecked == true)
            query = query.Where(x => x.IsFavorite);

        var terms = (SearchBox.Text ?? "")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var term in terms)
        {
            var captured = term;
            query = query.Where(x =>
                x.GameName.Contains(captured, StringComparison.OrdinalIgnoreCase) ||
                x.Description.Contains(captured, StringComparison.OrdinalIgnoreCase) ||
                x.Tags.Any(t => t.Contains(captured, StringComparison.OrdinalIgnoreCase)));
        }

        query = _sortMode switch
        {
            "Oldest" => query.OrderBy(x => x.Timestamp),
            "Largest" => query.OrderByDescending(x => x.SizeBytes),
            "Smallest" => query.OrderBy(x => x.SizeBytes),
            _ => query.OrderByDescending(x => x.Timestamp)
        };

        _visibleItems.Clear();
        foreach (var item in query)
            _visibleItems.Add(item);

        UpdateFilterStatus();
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(UpdateDateTimeline));
    }

    private void UpdateGameFilter()
    {
        var previousId = _selectedGameId;

        var options = new List<GameFilterItem> { new("", "All games") };
        options.AddRange(
            _allItems
                .GroupBy(x => x.GameId)
                .Select(g => new GameFilterItem(g.Key, g.First().GameName))
                .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase));

        GameFilter.ItemsSource = options;
        GameFilter.DisplayMemberPath = nameof(GameFilterItem.Name);
        GameFilter.SelectedItem = options.FirstOrDefault(x => x.Id == previousId) ?? options[0];
    }

    private void UpdateTagFilter()
    {
        var previous = _selectedTag;
        var tags = _allItems
            .SelectMany(x => x.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var options = new List<string> { "All tags" };
        options.AddRange(tags);
        TagFilter.ItemsSource = options;
        TagFilter.SelectedItem = string.IsNullOrWhiteSpace(previous) || !tags.Contains(previous, StringComparer.OrdinalIgnoreCase)
            ? "All tags" : previous;
    }

    private void UpdateFilterStatus()
    {
        var active =
            !string.IsNullOrWhiteSpace(_selectedGameId) ||
            !string.IsNullOrWhiteSpace(_selectedTag) ||
            FavoritesOnly.IsChecked == true ||
            !string.IsNullOrWhiteSpace(SearchBox.Text);

        FilterStatusPanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        FilterStatusText.Text = $"Showing {_visibleItems.Count} of {_allItems.Count} clips";
    }

    private static string BuildStorageSummary(string root)
    {
        try
        {
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(root));
            if (driveRoot is null) return "";
            var drive = new DriveInfo(driveRoot);
            return $"{drive.AvailableFreeSpace / (1024d * 1024 * 1024):N1} GB free of {drive.TotalSize / (1024d * 1024 * 1024):N1} GB on {drive.Name}";
        }
        catch { return ""; }
    }

    private void RecordingCard_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not FrameworkElement card ||
            card.DataContext is not RecordingItem item)
            return;

        try
        {
            // Moving directly from one card to another invalidates any pending
            // decoder start from the previous card.
            _clipPreviewGeneration++;
            _clipPreviewDelayTimer.Stop();

            if (_clipPreviewItem != item)
                StopClipPreview(closePopup: false);

            _clipPreviewCard = card;
            _clipPreviewItem = item;

            ClipPreviewPopup.PlacementTarget = card;
            ClipPreviewPopup.DataContext = item;
            ClipPreviewGameName.Text = item.GameName;
            ClipPreviewTime.Text = item.DisplayTime;

            SetClipPreviewThumbnail(item.DisplayImagePath);

            _clipPreviewRevealPending = false;
            ClipPreviewVideoView.Visibility = Visibility.Hidden;
            ClipPreviewUnavailableText.Visibility = Visibility.Collapsed;
            ClipPreviewThumbnail.Visibility = Visibility.Visible;

            // Open immediately with the exact Steam thumbnail. The video
            // decoder is deliberately delayed so sweeping the mouse across
            // cards does not repeatedly spin up DASH playback.
            ClipPreviewPopup.IsOpen = true;

            _clipPreviewDelayTimer.Start();
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Clip-card preview hover start failed", ex);
        }
    }

    private void RecordingCard_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not FrameworkElement card || card != _clipPreviewCard)
            return;

        _clipPreviewGeneration++;
        _clipPreviewDelayTimer.Stop();
        StopClipPreview(closePopup: true);
    }

    private void ClipPreviewDelayTimer_Tick(object? sender, EventArgs e)
    {
        _clipPreviewDelayTimer.Stop();

        var item = _clipPreviewItem;
        var card = _clipPreviewCard;
        var generation = _clipPreviewGeneration;

        if (item is null || card is null || !card.IsMouseOver || !ClipPreviewPopup.IsOpen)
            return;

        StartClipVideoPreview(item, generation);
    }

    private void StartClipVideoPreview(RecordingItem item, long generation)
    {
        try
        {
            StopClipPreviewMediaOnly();

            // The main browser and PlayerWindow use the same compatibility
            // manifest path, so clip previews get the same Steam DASH fixes.
            _clipPreviewMedia = _vlc.CreatePlaybackMedia(item.Path);
            _clipPreviewMedia.AddOption(":no-audio");
            _clipPreviewRevealGeneration = generation;
            _clipPreviewRevealPending = true;

            if (!_clipPreviewPlayer.Play(_clipPreviewMedia))
            {
                AppLogger.Write(
                    $"Clip-card video preview failed to start: {item.Path}",
                    "ERROR");
                ShowClipPreviewUnavailable();
                return;
            }

            // The hover may have changed while libVLC was being started.
            if (generation != _clipPreviewGeneration ||
                _clipPreviewItem != item ||
                _clipPreviewCard?.IsMouseOver != true)
            {
                StopClipPreviewMediaOnly();
                return;
            }

            ClipPreviewUnavailableText.Visibility = Visibility.Collapsed;

            AppLogger.Write(
                $"Clip-card video preview started: game={item.GameName} path={item.Path}");
        }
        catch (Exception ex)
        {
            AppLogger.WriteException(
                $"Clip-card video preview start failed: {item.Path}",
                ex);
            ShowClipPreviewUnavailable();
        }
    }

    private void ClipPreviewPlayer_EncounteredError(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var item = _clipPreviewItem;
            AppLogger.Write(
                $"Clip-card preview libVLC EncounteredError. " +
                $"game={item?.GameName ?? "(none)"} " +
                $"path={item?.Path ?? "(none)"} " +
                $"time={_clipPreviewPlayer.Time}ms state={_clipPreviewPlayer.State}",
                "ERROR");

            ShowClipPreviewUnavailable();
        }));
    }

    private void ClipPreviewPlayer_EndReached(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                // Keep a long hover useful without constructing another
                // player/media instance. Loop the same shared decoder.
                if (_clipPreviewItem is null ||
                    _clipPreviewCard?.IsMouseOver != true ||
                    !ClipPreviewPopup.IsOpen)
                    return;

                _clipPreviewPlayer.Time = 0;
                _clipPreviewPlayer.SetPause(false);
            }
            catch (Exception ex)
            {
                AppLogger.WriteException("Clip-card preview loop failed", ex);
            }
        }));
    }

    private void ClipPreviewPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        if (!_clipPreviewRevealPending || e.Time <= 0)
            return;

        _clipPreviewRevealPending = false;
        var generation = _clipPreviewRevealGeneration;

        Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(async () =>
            {
                // Give libVLC one render interval after reporting decoded
                // progress, then atomically replace the thumbnail. Keeping the
                // native VideoView hidden until this point prevents its
                // blank HWND from covering the WPF thumbnail during startup.
                await Task.Delay(70);

                if (generation != _clipPreviewGeneration ||
                    _clipPreviewCard?.IsMouseOver != true ||
                    !ClipPreviewPopup.IsOpen)
                    return;

                ClipPreviewVideoView.Visibility = Visibility.Visible;
                ClipPreviewThumbnail.Visibility = Visibility.Collapsed;
            }));
    }

    private void ShowClipPreviewUnavailable()
    {
        try
        {
            StopClipPreviewMediaOnly();
            _clipPreviewRevealPending = false;
            ClipPreviewVideoView.Visibility = Visibility.Hidden;

            if (!string.IsNullOrWhiteSpace(_clipPreviewItem?.DisplayImagePath))
            {
                // Preserve the useful static fallback instead of replacing a
                // good Steam thumbnail with an error message.
                ClipPreviewThumbnail.Visibility = Visibility.Visible;
                ClipPreviewUnavailableText.Visibility = Visibility.Collapsed;
            }
            else
            {
                ClipPreviewThumbnail.Visibility = Visibility.Collapsed;
                ClipPreviewUnavailableText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Clip-card preview fallback failed", ex);
        }
    }

    private void StopClipPreview(bool closePopup)
    {
        try
        {
            _clipPreviewDelayTimer.Stop();
            StopClipPreviewMediaOnly();

            _clipPreviewRevealPending = false;
            ClipPreviewVideoView.Visibility = Visibility.Hidden;
            ClipPreviewThumbnail.Visibility = Visibility.Visible;
            ClipPreviewUnavailableText.Visibility = Visibility.Collapsed;

            if (closePopup)
            {
                ClipPreviewPopup.IsOpen = false;
                ClipPreviewPopup.PlacementTarget = null;
                _clipPreviewItem = null;
                _clipPreviewCard = null;
            }
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Clip-card preview stop failed", ex);
        }
    }

    private void StopClipPreviewMediaOnly()
    {
        try
        {
            if (_clipPreviewPlayer.IsPlaying ||
                _clipPreviewPlayer.State is VLCState.Paused or VLCState.Opening or VLCState.Buffering)
            {
                _clipPreviewPlayer.Stop();
            }
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Clip-card preview player stop failed", ex);
        }

        try
        {
            _clipPreviewMedia?.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Clip-card preview media dispose failed", ex);
        }
        finally
        {
            _clipPreviewMedia = null;
        }
    }

    private void SetClipPreviewThumbnail(string? thumbnailPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(thumbnailPath) || !File.Exists(thumbnailPath))
            {
                ClipPreviewThumbnail.Source = null;
                return;
            }

            // OnLoad avoids holding a file handle on Steam's thumbnail.jpg.
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(thumbnailPath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            ClipPreviewThumbnail.Source = image;
        }
        catch (Exception ex)
        {
            ClipPreviewThumbnail.Source = null;
            AppLogger.WriteException(
                $"Clip-card thumbnail load failed: {thumbnailPath}",
                ex);
        }
    }

    private RecordingItem? SelectedItem =>
        TileLayoutPanel.Visibility == Visibility.Visible
            ? TileRecordingList.SelectedItem as RecordingItem
            : RecordingList.SelectedItem as RecordingItem;

    private void PlaySelected()
    {
        var item = SelectedItem;
        if (item is null || !File.Exists(item.Path)) return;

        try
        {
            var player = new PlayerWindow(_vlc, _metadata, item) { Owner = this };
            player.Closing += (_, _) =>
            {
                // Raise the owner before libVLC begins its synchronous teardown.
                // Waiting for Closed allows Windows to expose another app while
                // the native players stop and dispose.
                if (!IsVisible || WindowState == WindowState.Minimized)
                    return;

                Activate();
                Focus();
            };
            player.Show();
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Playback failed", ex);
            WpfMessageBox.Show(this, ex.Message, "Playback failed", WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
        }
    }

    private void ExportSelected()
    {
        var item = SelectedItem;
        if (item is null) return;

        var options = new ExportOptionsWindow(
            _vlc.GetVideoCodec(item.Path),
            item.DurationSeconds,
            item.SizeBytes) { Owner = this };
        if (options.ShowDialog() != true) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export recording to MP4",
            Filter = "MP4 video (*.mp4)|*.mp4",
            FileName = $"{SafeFilePart(item.GameName)} - {item.Timestamp:yyyy-MM-dd_HH-mm-ss} - {SafeFilePart(options.SelectedCodecFileLabel)}.mp4"
        };

        if (dialog.ShowDialog(this) != true) return;

        var progressWindow = new ExportProgressWindow((progress, cancellationToken) =>
            _vlc.ExportMp4Async(item, dialog.FileName, options.SelectedCodec, progress, cancellationToken))
        {
            Owner = this
        };

        if (progressWindow.ShowDialog() == true)
        {
            WpfMessageBox.Show(this, $"Export complete:\n\n{dialog.FileName}",
                "Steam Recording Browser", WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
        }
    }

    private static string SafeFilePart(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value.Trim();
    }

    private void RecordingList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => PlaySelected();
    private void PlayMenu_Click(object sender, RoutedEventArgs e) => PlaySelected();
    private void ExportMenu_Click(object sender, RoutedEventArgs e) => ExportSelected();

    private void FavoriteMenu_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedItem;
        if (item is null) return;
        item.IsFavorite = !item.IsFavorite;
        _metadata.UpdateFrom(item);
        ApplyFilter();
    }

    private void DescriptionMenu_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedItem;
        if (item is null) return;

        var dialog = new TextEntryDialog("Edit description", "Description:", item.Description) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        item.Description = dialog.Value.Trim();
        _metadata.UpdateFrom(item);
        ApplyFilter();
    }

    private void TagsMenu_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedItem;
        if (item is null) return;

        var dialog = new TextEntryDialog(
            "Edit tags",
            "Comma-separated tags:",
            string.Join(", ", item.Tags)) { Owner = this };

        if (dialog.ShowDialog() != true) return;

        item.Tags = MetadataService.NormalizeTags(new[] { dialog.Value });
        _metadata.UpdateFrom(item);
        UpdateTagFilter();
        ApplyFilter();
    }

    private void OpenFolderMenu_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedItem;
        if (item is null) return;
        var folder = Path.GetDirectoryName(item.Path);
        if (folder is null) return;

        Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _metadata.Load();
        await LoadRecordingsAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        ApplyFilter();
    }

    private void GameFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameFilter.SelectedItem is GameFilterItem selected)
            _selectedGameId = selected.Id;
        ApplyFilter();
    }

    private void TagFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = TagFilter.SelectedItem as string;
        _selectedTag = selected is null or "All tags" ? "" : selected;
        ApplyFilter();
    }

    private void SortFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _sortMode = SortFilter.SelectedItem as string ?? "Newest";
        ApplyFilter();
    }

    private void FavoritesOnly_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        FavoritesOnly.IsChecked = false;
        _selectedGameId = "";
        _selectedTag = "";
        UpdateGameFilter();
        UpdateTagFilter();
        ApplyFilter();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(AppLogger.LogPath))
            File.WriteAllText(AppLogger.LogPath, "");

        Process.Start(new ProcessStartInfo(AppLogger.LogPath) { UseShellExecute = true });
    }

    private void InitializeRecordingRoot()
    {
        var saved = _settings.Load().RecordingRoot?.Trim() ?? "";

        if (Directory.Exists(saved))
        {
            _recordingRoot = saved;
            AppLogger.Write($"Using saved recording root: {saved}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(saved))
            AppLogger.Write($"Saved recording root no longer exists: {saved}", "WARN");

        var discovered = _steam.FindDefaultRecordingRoot();
        if (Directory.Exists(discovered))
        {
            _recordingRoot = discovered;
            _settings.SaveRecordingRoot(discovered);
            return;
        }

        _recordingRoot = "";
        AppLogger.Write(
            "No Steam Game Recording folder was auto-detected; user selection is required.",
            "WARN");
    }

    private bool ShowSettings()
    {
        _clipLayoutChangedFromSettings = false;
        var dialog = new SettingsWindow(_metadata, _allItems)
        {
            Owner = this
        };

        dialog.ShowDialog();
        _recordingRoot = _settings.Load().RecordingRoot?.Trim() ?? "";

        _clipLayoutChangedFromSettings = dialog.ClipLayoutChanged;

        if (dialog.MetadataImported)
        {
            UpdateTagFilter();
            ApplyFilter();
        }

        return dialog.RecordingRootChanged;
    }

    private void ApplyClipLayout()
    {
        var useTiles = _settings.Load().UseTileLayout;
        ListLayoutPanel.Visibility = useTiles ? Visibility.Collapsed : Visibility.Visible;
        TileLayoutPanel.Visibility = useTiles ? Visibility.Visible : Visibility.Collapsed;

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(UpdateDateTimeline));
    }

    private async Task ApplyClipLayoutTransitionAsync()
    {
        LayoutBusyOverlay.Visibility = Visibility.Visible;

        try
        {
            // Let the overlay paint before WPF measures and arranges the new
            // item layout, which can be noticeable for large clip libraries.
            await Dispatcher.Yield(DispatcherPriority.Render);
            ApplyClipLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        finally
        {
            LayoutBusyOverlay.Visibility = Visibility.Collapsed;
            _clipLayoutChangedFromSettings = false;
        }
    }

    private void DateTimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateDateTimeline();

    private void UpdateDateTimeline()
    {
        RenderDateTimeline(DateTimelineCanvas, ListLayoutPanel.Visibility == Visibility.Visible);
        RenderDateTimeline(TileDateTimelineCanvas, TileLayoutPanel.Visibility == Visibility.Visible);
    }

    private void RenderDateTimeline(Canvas timelineCanvas, bool isVisible)
    {
        timelineCanvas.Children.Clear();

        var height = timelineCanvas.ActualHeight;
        if (!isVisible ||
            height <= 24 ||
            _visibleItems.Count == 0)
            return;

        const double railX = 5;
        const double edgePadding = 12;
        var usableHeight = height - (edgePadding * 2);

        timelineCanvas.Children.Add(new TimelineLine
        {
            X1 = railX,
            X2 = railX,
            Y1 = edgePadding,
            Y2 = height - edgePadding,
            Stroke = new SolidColorBrush(MediaColor.FromRgb(58, 67, 83)),
            StrokeThickness = 2
        });

        var markerCount = Math.Min(8, _visibleItems.Count);
        for (var marker = 0; marker < markerCount; marker++)
        {
            var fraction = markerCount == 1 ? 0d : marker / (double)(markerCount - 1);
            var itemIndex = (int)Math.Round(fraction * (_visibleItems.Count - 1));
            var y = edgePadding + (fraction * usableHeight);
            var item = _visibleItems[itemIndex];

            var tick = new TimelineLine
            {
                X1 = railX,
                X2 = 12,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(MediaColor.FromRgb(102, 192, 244)),
                StrokeThickness = 2
            };
            timelineCanvas.Children.Add(tick);

            var label = new TextBlock
            {
                Text = item.Timestamp.ToString("MMM d\nyyyy"),
                Foreground = new SolidColorBrush(MediaColor.FromRgb(152, 162, 179)),
                FontSize = 10,
                LineHeight = 12
            };
            Canvas.SetLeft(label, 16);
            Canvas.SetTop(label, Math.Clamp(y - 12, 0, Math.Max(0, height - 25)));
            timelineCanvas.Children.Add(label);
        }
    }

    private sealed record GameFilterItem(string Id, string Name);
}
