using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
using SteamRecordingBrowser.Utilities;

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
    private readonly BulkObservableCollection<RecordingItem> _visibleItems = new();

    private CancellationTokenSource? _scanCancellation;
    private string _selectedGameId = "";
    private string _selectedTag = "";
    private string _sortMode = "Newest";
    private string _recordingRoot = "";
    private bool _clipLayoutChangedFromSettings;
    private LogViewerWindow? _logViewer;
    private DispatcherTimer? _searchFilterTimer;
    private DispatcherOperation? _dateTimelineUpdateOperation;
    private readonly ObservableCollection<TableColumnOption> _tableColumnOptions = new();
    private bool _updatingTableColumns;
    private Point _columnDragStart;
    private TableColumnOption? _draggedTableColumn;
    private List<TableColumnOption>? _columnOrderBeforeDrag;

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
    private readonly DispatcherTimer _liveRecordingTimer;
    private Media? _clipPreviewMedia;
    private RecordingItem? _clipPreviewItem;
    private FrameworkElement? _clipPreviewCard;
    private long _clipPreviewGeneration;
    private long _clipPreviewRevealGeneration;
    private bool _clipPreviewRevealPending;
    private bool _liveStateRefreshPending;

    public MainWindow(IProgress<StartupProgress>? startupProgress = null)
    {
        _startupProgress = startupProgress;

        ReportStartup(21, "Creating main window…");
        InitializeComponent();
        InitializeTableColumns();
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
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _clipPreviewDelayTimer.Tick += ClipPreviewDelayTimer_Tick;

        _searchFilterTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(225)
        };
        _searchFilterTimer.Tick += SearchFilterTimer_Tick;

        _liveRecordingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _liveRecordingTimer.Tick += async (_, _) => await RefreshLiveRecordingStatesAsync();
        _liveRecordingTimer.Start();

        _clipPreviewPlayer.EncounteredError += ClipPreviewPlayer_EncounteredError;
        _clipPreviewPlayer.EndReached += ClipPreviewPlayer_EndReached;
        _clipPreviewPlayer.TimeChanged += ClipPreviewPlayer_TimeChanged;

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
            _searchFilterTimer?.Stop();
            if (_searchFilterTimer is not null)
                _searchFilterTimer.Tick -= SearchFilterTimer_Tick;
            _clipPreviewPlayer.EncounteredError -= ClipPreviewPlayer_EncounteredError;
            _clipPreviewPlayer.EndReached -= ClipPreviewPlayer_EndReached;
            _clipPreviewPlayer.TimeChanged -= ClipPreviewPlayer_TimeChanged;
            _liveRecordingTimer.Stop();

            ClipPreviewVideoView.MediaPlayer = null;
            _clipPreviewPlayer.Dispose();

            _vlc.Dispose();
        };

    }

    private async Task RefreshLiveRecordingStatesAsync()
    {
        if (_liveStateRefreshPending)
            return;

        _liveStateRefreshPending = true;
        try
        {
            var items = _allItems.Where(item => item.IsAutoRecording)
                .Select(item => (Item: item, Paths: item.SessionPaths.ToArray()))
                .ToArray();
            var states = await Task.Run(() => items
                .Select(entry => entry.Paths.Any(LiveRecordingService.IsActivelyRecording))
                .ToArray());
            for (var index = 0; index < items.Length; index++)
                items[index].Item.IsLive = states[index];
        }
        finally
        {
            _liveStateRefreshPending = false;
        }
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
        _searchFilterTimer?.Stop();
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
                x.RecordingTypeLabel.Contains(captured, StringComparison.OrdinalIgnoreCase) ||
                x.Tags.Any(t => t.Contains(captured, StringComparison.OrdinalIgnoreCase)));
        }

        var useTableFilters = TableLayoutPanel.Visibility == Visibility.Visible;
        var tableGame = useTableFilters ? TableGameFilter.Text?.Trim() ?? "" : "";
        var tableType = useTableFilters ? TableTypeFilter.Text?.Trim() ?? "" : "";
        var tableCodec = useTableFilters ? TableCodecFilter.Text?.Trim() ?? "" : "";
        var tableMetadata = useTableFilters ? TableMetadataFilter.Text?.Trim() ?? "" : "";
        if (tableGame.Length > 0)
            query = query.Where(x => x.GameName.Contains(tableGame, StringComparison.OrdinalIgnoreCase));
        if (tableType.Length > 0)
            query = query.Where(x => x.RecordingTypeLabel.Contains(tableType, StringComparison.OrdinalIgnoreCase));
        if (tableCodec.Length > 0)
            query = query.Where(x => x.VideoCodec.Contains(tableCodec, StringComparison.OrdinalIgnoreCase) ||
                                     x.AudioCodec.Contains(tableCodec, StringComparison.OrdinalIgnoreCase));
        if (tableMetadata.Length > 0)
            query = query.Where(x => x.Description.Contains(tableMetadata, StringComparison.OrdinalIgnoreCase) ||
                                     x.Tags.Any(tag => tag.Contains(tableMetadata, StringComparison.OrdinalIgnoreCase)) ||
                                     x.SteamMetadata.Any(value => value.Contains(tableMetadata, StringComparison.OrdinalIgnoreCase)));

        query = _sortMode switch
        {
            "Oldest" => query.OrderBy(x => x.Timestamp),
            "Largest" => query.OrderByDescending(x => x.SizeBytes),
            "Smallest" => query.OrderBy(x => x.SizeBytes),
            _ => query.OrderByDescending(x => x.Timestamp)
        };

        _visibleItems.ReplaceAll(query);

        UpdateFilterStatus();
        ScheduleDateTimelineUpdate();
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
            !string.IsNullOrWhiteSpace(SearchBox.Text) ||
            TableLayoutPanel.Visibility == Visibility.Visible &&
            (!string.IsNullOrWhiteSpace(TableGameFilter.Text) ||
             !string.IsNullOrWhiteSpace(TableTypeFilter.Text) ||
             !string.IsNullOrWhiteSpace(TableCodecFilter.Text) ||
             !string.IsNullOrWhiteSpace(TableMetadataFilter.Text));

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
                StopClipPreview(closePopup: true);

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

            // Require a continuous hover before opening the enlarged preview
            // or starting its decoder. This avoids popup churn and expensive
            // DASH initialization while cards move under the pointer during
            // tile-view scrolling.
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

        if (item is null || card is null || !card.IsMouseOver)
            return;

        ClipPreviewPopup.IsOpen = true;
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

    private RecordingItem? SelectedItem => TableLayoutPanel.Visibility == Visibility.Visible
        ? RecordingTable.SelectedItem as RecordingItem
        : TileLayoutPanel.Visibility == Visibility.Visible
            ? TileRecordingList.SelectedItem as RecordingItem
            : RecordingList.SelectedItem as RecordingItem;

    private void PlaySelected()
    {
        var item = SelectedItem;
        if (item is null || !File.Exists(item.Path)) return;

        try
        {
            // The hover preview is hosted inside a Popup. If that popup closes
            // while its decoder is still running, libVLC can lose the embedded
            // HWND and create an independent "VLC Direct3D11" window.
            _clipPreviewGeneration++;
            StopClipPreview(closePopup: true);

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
            _vlc.ExportMp4Async(item, dialog.FileName, options.SelectedCodec,
                options.UseHardwareEncoding, progress, cancellationToken))
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

    private void TableFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RecordingItem item }) return;
        item.IsFavorite = !item.IsFavorite;
        _metadata.UpdateFrom(item);
        ApplyFilter();
        e.Handled = true;
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

        _searchFilterTimer?.Stop();
        _searchFilterTimer?.Start();
    }

    private void SearchFilterTimer_Tick(object? sender, EventArgs e)
    {
        _searchFilterTimer?.Stop();
        ApplyFilter();
    }

    private void TableFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchFilterTimer?.Stop();
        _searchFilterTimer?.Start();
    }

    private void ClearTableFilters_Click(object sender, RoutedEventArgs e)
    {
        TableGameFilter.Clear();
        TableTypeFilter.Clear();
        TableCodecFilter.Clear();
        TableMetadataFilter.Clear();
        ApplyFilter();
    }

    private void InitializeTableColumns()
    {
        _updatingTableColumns = true;
        try
        {
            var saved = _settings.Load().TableColumns
                .Where(column => !string.IsNullOrWhiteSpace(column.Name))
                .GroupBy(column => column.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var ordered = RecordingTable.Columns
                .Select((column, defaultIndex) => new
                {
                    Column = column,
                    Name = ReferenceEquals(column, FavoriteColumn)
                        ? "Favorite"
                        : column.Header?.ToString() ?? $"Column {defaultIndex + 1}",
                    DefaultIndex = defaultIndex
                })
                .OrderBy(entry => saved.TryGetValue(entry.Name, out var setting)
                    ? setting.DisplayIndex
                    : int.MaxValue)
                .ThenBy(entry => entry.DefaultIndex)
                .ToList();

            for (var index = 0; index < ordered.Count; index++)
                ordered[index].Column.DisplayIndex = index;

            _tableColumnOptions.Clear();
            foreach (var entry in ordered)
            {
                var visible = !saved.TryGetValue(entry.Name, out var setting) || setting.IsVisible;
                if (setting?.Width > 0)
                    entry.Column.Width = new DataGridLength(Math.Clamp(setting.Width, 40, 1200));
                entry.Column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                _tableColumnOptions.Add(new TableColumnOption(entry.Name, entry.Column, visible));
            }
            ColumnChooserList.ItemsSource = _tableColumnOptions;
        }
        finally
        {
            _updatingTableColumns = false;
        }
    }

    private void ColumnChooserButton_Click(object sender, RoutedEventArgs e) =>
        ColumnChooserPopup.IsOpen = !ColumnChooserPopup.IsOpen;

    private void ColumnChooserList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggedTableColumn = null;
        if (FindVisualAncestor<TextBlock>(e.OriginalSource as DependencyObject) is not { Tag: "ColumnDragHandle" })
            return;

        var container = ItemsControl.ContainerFromElement(ColumnChooserList, e.OriginalSource as DependencyObject)
            as ListBoxItem;
        if (container?.DataContext is not TableColumnOption option) return;
        _columnDragStart = e.GetPosition(ColumnChooserList);
        _draggedTableColumn = option;
        ColumnChooserList.SelectedItem = option;
    }

    private void ColumnChooserList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedTableColumn is null || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(ColumnChooserList);
        if (Math.Abs(position.X - _columnDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _columnDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var dragged = _draggedTableColumn;
        _draggedTableColumn = null;
        _columnOrderBeforeDrag = _tableColumnOptions.ToList();
        var result = DragDrop.DoDragDrop(ColumnChooserList, dragged, DragDropEffects.Move);
        if (result != DragDropEffects.Move && _columnOrderBeforeDrag is not null)
            ApplyTableColumnOrder(_columnOrderBeforeDrag);
        _columnOrderBeforeDrag = null;
    }

    private void ColumnChooserList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TableColumnOption)) is not TableColumnOption dragged)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var targetContainer = ItemsControl.ContainerFromElement(ColumnChooserList, e.OriginalSource as DependencyObject)
            as ListBoxItem;
        if (targetContainer?.DataContext is TableColumnOption target && !ReferenceEquals(dragged, target))
            MoveTableColumn(dragged, _tableColumnOptions.IndexOf(target), persist: false);

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void ColumnChooserList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TableColumnOption)) is not TableColumnOption dragged) return;
        var targetContainer = ItemsControl.ContainerFromElement(ColumnChooserList, e.OriginalSource as DependencyObject)
            as ListBoxItem;
        if (targetContainer?.DataContext is TableColumnOption target && !ReferenceEquals(dragged, target))
            MoveTableColumn(dragged, _tableColumnOptions.IndexOf(target), persist: false);
        SaveTableColumnSettings();
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void ColumnVisibility_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingTableColumns || sender is not CheckBox { DataContext: TableColumnOption option })
            return;

        if (!option.IsVisible && _tableColumnOptions.Count(column => column.IsVisible) == 0)
        {
            option.IsVisible = true;
            return;
        }

        option.Column.Visibility = option.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        SaveTableColumnSettings();
    }

    private void MoveColumnUp_Click(object sender, RoutedEventArgs e) => MoveSelectedTableColumn(-1);
    private void MoveColumnDown_Click(object sender, RoutedEventArgs e) => MoveSelectedTableColumn(1);

    private void MoveSelectedTableColumn(int offset)
    {
        if (ColumnChooserList.SelectedItem is not TableColumnOption option) return;
        var currentIndex = _tableColumnOptions.IndexOf(option);
        var targetIndex = currentIndex + offset;
        if (targetIndex < 0 || targetIndex >= _tableColumnOptions.Count) return;

        MoveTableColumn(option, targetIndex);
    }

    private void MoveTableColumn(TableColumnOption option, int targetIndex, bool persist = true)
    {
        var currentIndex = _tableColumnOptions.IndexOf(option);
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= _tableColumnOptions.Count || currentIndex == targetIndex)
            return;

        _updatingTableColumns = true;
        try
        {
            option.Column.DisplayIndex = targetIndex;
            _tableColumnOptions.Move(currentIndex, targetIndex);
            ColumnChooserList.SelectedItem = option;
        }
        finally
        {
            _updatingTableColumns = false;
        }
        if (persist)
            SaveTableColumnSettings();
    }

    private void ApplyTableColumnOrder(IReadOnlyList<TableColumnOption> order)
    {
        _updatingTableColumns = true;
        try
        {
            for (var targetIndex = 0; targetIndex < order.Count; targetIndex++)
            {
                var option = order[targetIndex];
                option.Column.DisplayIndex = targetIndex;
                var currentIndex = _tableColumnOptions.IndexOf(option);
                if (currentIndex != targetIndex)
                    _tableColumnOptions.Move(currentIndex, targetIndex);
            }
        }
        finally
        {
            _updatingTableColumns = false;
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void RecordingTable_ColumnReordered(object sender, DataGridColumnEventArgs e)
    {
        if (_updatingTableColumns) return;
        _updatingTableColumns = true;
        try
        {
            var ordered = _tableColumnOptions.OrderBy(option => option.Column.DisplayIndex).ToList();
            _tableColumnOptions.Clear();
            foreach (var option in ordered) _tableColumnOptions.Add(option);
        }
        finally
        {
            _updatingTableColumns = false;
        }
        SaveTableColumnSettings();
    }

    private void TableColumnResize_DragCompleted(
        object sender,
        System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        SaveTableColumnSettings();

    private void SaveTableColumnSettings() =>
        _settings.SaveTableColumns(_tableColumnOptions
            .OrderBy(option => option.Column.DisplayIndex)
            .Select(option => new TableColumnSetting
            {
                Name = option.Name,
                IsVisible = option.IsVisible,
                DisplayIndex = option.Column.DisplayIndex,
                Width = option.Column.ActualWidth
            }));

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
        TableGameFilter.Clear();
        TableTypeFilter.Clear();
        TableCodecFilter.Clear();
        TableMetadataFilter.Clear();
        FavoritesOnly.IsChecked = false;
        _selectedGameId = "";
        _selectedTag = "";
        UpdateGameFilter();
        UpdateTagFilter();
        ApplyFilter();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logViewer is { IsLoaded: true })
        {
            _logViewer.Activate();
            _logViewer.Focus();
            return;
        }

        _logViewer = new LogViewerWindow { Owner = this };
        _logViewer.Closed += (_, _) => _logViewer = null;
        _logViewer.Show();
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
        var layout = _settings.Load().EffectiveClipLayout;
        StopClipPreview(closePopup: true);
        ListLayoutPanel.Visibility = layout == "List" ? Visibility.Visible : Visibility.Collapsed;
        TileLayoutPanel.Visibility = layout == "Tiles" ? Visibility.Visible : Visibility.Collapsed;
        TableLayoutPanel.Visibility = layout == "Table" ? Visibility.Visible : Visibility.Collapsed;
        RecordingList.ItemsSource = layout == "List" ? _visibleItems : null;
        TileRecordingList.ItemsSource = layout == "Tiles" ? _visibleItems : null;
        RecordingTable.ItemsSource = layout == "Table" ? _visibleItems : null;

        ApplyFilter();

        ScheduleDateTimelineUpdate();
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
        ScheduleDateTimelineUpdate();

    private void ScheduleDateTimelineUpdate()
    {
        if (_dateTimelineUpdateOperation is { Status: DispatcherOperationStatus.Pending })
            return;

        _dateTimelineUpdateOperation = Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() =>
            {
                _dateTimelineUpdateOperation = null;
                UpdateDateTimeline();
            }));
    }

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

    private sealed class TableColumnOption : INotifyPropertyChanged
    {
        private bool _isVisible;

        public TableColumnOption(string name, DataGridColumn column, bool isVisible)
        {
            Name = name;
            Column = column;
            _isVisible = isVisible;
        }

        public string Name { get; }
        public DataGridColumn Column { get; }
        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible == value) return;
                _isVisible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
