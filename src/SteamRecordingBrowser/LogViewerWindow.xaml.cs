using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using SteamRecordingBrowser.Services;

namespace SteamRecordingBrowser;

public partial class LogViewerWindow : Window
{
    private const int MaximumEntries = 5_000;
    private const int MaximumPendingEntries = 10_000;
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly Queue<LogEntry> _paused = new();
    private readonly DispatcherTimer _batchTimer;
    private readonly ICollectionView _view;
    private int _pendingCount;
    private bool _pausedDisplay;
    private int _loadGeneration;
    private HashSet<string>? _initialSnapshotKeys;
    private bool _programmaticScroll;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    public LogViewerWindow()
    {
        InitializeComponent();
        DataContext = this;
        _view = CollectionViewSource.GetDefaultView(Entries);
        _view.Filter = MatchesFilter;
        _batchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
            (_, _) => FlushPending(), Dispatcher);
        AppLogger.EntryWritten += OnEntryWritten;
        Loaded += LogViewerWindow_Loaded;
        Closed += LogViewerWindow_Closed;
    }

    private async void LogViewerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var generation = _loadGeneration;
        var recent = await Task.Run(() => AppLogger.ReadRecentEntries(MaximumEntries));
        if (generation == _loadGeneration)
        {
            _programmaticScroll = true;
            _initialSnapshotKeys = recent.Select(entry => entry.ToLogText()).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in recent)
                AddEntry(entry);
            ScrollToLatest();
            _ = Dispatcher.BeginInvoke(() => _programmaticScroll = false, DispatcherPriority.ContextIdle);
        }
        _batchTimer.Start();
        UpdateStatus();
    }

    private void OnEntryWritten(LogEntry entry)
    {
        _pending.Enqueue(entry);
        var count = Interlocked.Increment(ref _pendingCount);
        if (count <= MaximumPendingEntries || !_pending.TryDequeue(out _))
            return;
        Interlocked.Decrement(ref _pendingCount);
    }

    private void FlushPending()
    {
        var followLatest = !_pausedDisplay && AutoScrollCheckBox.IsChecked == true &&
                           Volatile.Read(ref _pendingCount) > 0;
        if (followLatest)
            _programmaticScroll = true;

        var processed = 0;
        while (processed < 250 && _pending.TryDequeue(out var entry))
        {
            Interlocked.Decrement(ref _pendingCount);
            if (_initialSnapshotKeys?.Remove(entry.ToLogText()) == true)
            {
                processed++;
                continue;
            }

            if (_pausedDisplay)
            {
                if (_paused.Count == MaximumEntries)
                    _paused.Dequeue();
                _paused.Enqueue(entry);
            }
            else
            {
                AddEntry(entry);
            }
            processed++;
        }

        if (Volatile.Read(ref _pendingCount) == 0)
            _initialSnapshotKeys = null;

        if (followLatest && processed > 0)
            ScrollToLatest();
        if (followLatest)
            _ = Dispatcher.BeginInvoke(() => _programmaticScroll = false, DispatcherPriority.ContextIdle);
        UpdateStatus();
    }

    private void AddEntry(LogEntry entry)
    {
        if (Entries.Count == MaximumEntries)
            Entries.RemoveAt(0);
        Entries.Add(entry);
    }

    private bool MatchesFilter(object value)
    {
        if (value is not LogEntry entry)
            return false;
        var levelEnabled = entry.Level.ToUpperInvariant() switch
        {
            "INFO" => InfoFilterToggle.IsChecked == true,
            "WARN" => WarnFilterToggle.IsChecked == true,
            "ERROR" => ErrorFilterToggle.IsChecked == true,
            _ => false
        };
        if (!levelEnabled)
            return false;
        return string.IsNullOrWhiteSpace(SearchBox.Text) ||
               entry.DisplayText.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase);
    }

    private void LevelFilter_Click(object sender, RoutedEventArgs e)
    {
        _view.Refresh();
        UpdateStatus();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        UpdateStatus();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _pausedDisplay = !_pausedDisplay;
        PauseButton.Content = _pausedDisplay ? "Resume" : "Pause";
        if (!_pausedDisplay)
        {
            _programmaticScroll = AutoScrollCheckBox.IsChecked == true;
            while (_paused.Count > 0)
                AddEntry(_paused.Dequeue());
            if (AutoScrollCheckBox.IsChecked == true)
            {
                ScrollToLatest();
                _ = Dispatcher.BeginInvoke(() => _programmaticScroll = false, DispatcherPriority.ContextIdle);
            }
        }
        UpdateStatus();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _loadGeneration++;
        Entries.Clear();
        _paused.Clear();
        while (_pending.TryDequeue(out _))
            Interlocked.Decrement(ref _pendingCount);
        UpdateStatus();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(AppLogger.LogPath))
            File.WriteAllText(AppLogger.LogPath, "");
        Process.Start(new ProcessStartInfo(AppLogger.LogPath) { UseShellExecute = true });
    }

    private void LogList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
            AutoScrollCheckBox.IsChecked = false;
    }

    private void LogList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_programmaticScroll && e.VerticalChange < 0)
            AutoScrollCheckBox.IsChecked = false;
    }

    private void ScrollToLatest()
    {
        if (Entries.Count > 0)
            LogList.ScrollIntoView(Entries[^1]);
    }

    private void UpdateStatus()
    {
        var visible = _view?.Cast<object>().Count() ?? 0;
        var pausedText = _pausedDisplay ? $" • paused ({_paused.Count:N0} queued)" : " • live";
        StatusText.Text = $"{visible:N0} shown • {Entries.Count:N0} retained{pausedText}";
    }

    private void LogViewerWindow_Closed(object? sender, EventArgs e)
    {
        _batchTimer.Stop();
        AppLogger.EntryWritten -= OnEntryWritten;
    }
}
