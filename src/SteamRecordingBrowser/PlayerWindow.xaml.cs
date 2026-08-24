using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Shapes;
using MediaColor = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Microsoft.Win32;
using LibVLCSharp.Shared;
using SteamRecordingBrowser.Services;
using SteamRecordingBrowser.Models;
using SteamRecordingBrowser.Dialogs;

namespace SteamRecordingBrowser;

public partial class PlayerWindow : Window
{
    private readonly LibVlcService _vlc;
    private readonly MetadataService _metadata;
    private readonly RecordingItem _item;
    private readonly MediaPlayer _player;
    private readonly Media _media;
    private Media _activeMedia;
    private Media? _liveMedia;
    private readonly LiveDashServer? _liveDashServer;
    private int _lastAudibleVolume = 100;
    private int _desiredVolume = 100;
    private bool _desiredMuted;
    private readonly DispatcherTimer _timer;
    private bool _dragging;
    private long _displayTimeMs;
    private long _displayClockTimestamp;
    private long _lastCorrectionTimestamp;
    private long _lastLiveStateCheckTimestamp;
    private bool _displayClockInitialized;
    private bool _mediaEnded;
    private long? _pendingRestartSeekMs;
    private bool _liveRecoveryPending;
    private bool _followingLive;
    private long _historyLengthMs;
    private long _lastBufferingLogTimestamp;
    private long _videoTransitionGeneration;
    private bool _videoTransitionCovered = true;
    private bool _videoTransitionHidePending;
    private bool _videoTransitionAwaitingPlaying = true;

    // libVLC can continue reporting the pre-seek DASH timestamp briefly after
    // an explicit seek. During this window the UI trusts the requested target
    // instead of treating the stale decoder time as a large-drift correction.
    private bool _seekSettling;
    private long _seekTargetMs;
    private long _seekSettleDeadlineTimestamp;

    private readonly MediaPlayer _previewPlayer;
    private readonly Media _previewMedia;

    private bool _previewStarted;
    private bool _previewHostRetryPending;

    private long _lastHoverPreviewTargetMs = -1;
    private long _lastHoverPreviewProgressLogTimestamp;
    private long _lastPreviewSeekTimestamp;

    // Mouse movement only updates the newest target. A dedicated pump feeds
    // libVLC at a sustainable rate so stale DASH seeks are never queued.
    private readonly DispatcherTimer _scrubFrameTimer;
    private bool _scrubTargetDirty;
    private long _lastIssuedScrubTargetMs = -1;
    private long _lastScrubIssueTimestamp;
    private long _lastScrubDiagnosticTimestamp;

    // Timeline scrubbing uses the existing main MediaPlayer/VideoView.
    // The decoder stays actively running and muted during a drag so seek
    // frames are rendered directly into the visible player.
    private bool _mainMuteBeforeScrub;
    private readonly DispatcherTimer _scrubFinishPauseTimer;
    private readonly DispatcherTimer _scrubHoldFreezeTimer;
    private readonly DispatcherTimer _scrubPauseWatchdogTimer;
    private long _scrubFinishTargetMs = -1;
    private long _scrubHoldTargetMs = -1;
    private long _scrubWakeUntilTimestamp;

    private bool _resumeAfterScrub;
    private bool _internalTimelineChange;
    private bool _scrubbing;
    private bool _fineScrubbing;
    private long _fineScrubAnchorMs;
    private double _fineScrubAnchorMouseX;
    private bool _scrubPausePending;
    private bool _scrubReleasePending;
    private double _pendingScrubPosition;
private readonly DispatcherTimer _hoverFramePauseTimer;
    private long _hoverStaticTargetMs = -1;

    private bool _nativeInputHookAttached;
    private IntPtr _mouseHook = IntPtr.Zero;
    private IntPtr _keyboardHook = IntPtr.Zero;
    private LowLevelMouseProc? _mouseHookProc;
    private LowLevelKeyboardProc? _keyboardHookProc;

    private bool _spaceKeyDown;

    private long _timelineTicksLength = -1;
    private double _timelineTicksWidth = -1;

    private const long LongVideoThresholdMs = 60 * 60 * 1000;
    private const long LongVideoPreviewSegmentMs = 3_000;
    private const long FineScrubHalfWindowMs = 2 * 60 * 1000;
    private const long LiveEdgeSafetyDelayMs = 6_000;

    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;

    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmLButtonDown = 0x0201;
    private const int WmMouseWheel = 0x020A;

    private const int VkSpace = 0x20;
    private const uint GaRoot = 2;
    private const uint GaRootOwner = 3;

    public PlayerWindow(LibVlcService vlc, MetadataService metadata, RecordingItem item)
    {
        InitializeComponent();

        _vlc = vlc;
        _metadata = metadata;
        _item = item;

        Title = $"Steam Recording Browser — {item.GameName}";
        PlayerInfoBadge.ToolTip = item.VideoInfoText;
        ClipInfoText.Text = $"{item.GameName}  •  {item.DisplayTime}  •  {item.RecordingTypeLabel}";
        LivePlaybackBadge.Visibility = item.IsLive ? Visibility.Visible : Visibility.Collapsed;
        GoLiveButton.Visibility = item.IsLive ? Visibility.Visible : Visibility.Collapsed;
        UpdateFavoriteButton();

        _player = new MediaPlayer(vlc.LibVlc)
        {
            Mute = false,
            Volume = 100
        };
        VideoView.MediaPlayer = _player;
        var sessionPaths = item.SessionPaths.Count > 0 ? item.SessionPaths : new[] { item.Path };
        _liveDashServer = item.IsLive || sessionPaths.Count > 1
            ? vlc.CreateLiveDashServer(sessionPaths)
            : null;
        var playbackUri = _liveDashServer?.ManifestUri;
        _media = playbackUri is not null
            ? vlc.CreatePlaybackMedia(playbackUri, isLive: true, useHardwareDecoding: !item.IsLive)
            : vlc.CreatePlaybackMedia(item.Path);
        _activeMedia = _media;

        // A second muted player is dedicated to timeline hover previews so
        // previewing another point never disturbs main playback.
        _previewPlayer = new MediaPlayer(vlc.LibVlc)
        {
            Mute = true
        };
        PreviewVideoView.MediaPlayer = _previewPlayer;
        _previewMedia = playbackUri is not null
            ? vlc.CreatePlaybackMedia(playbackUri, isLive: true, useHardwareDecoding: !item.IsLive)
            : vlc.CreatePlaybackMedia(item.Path);
        _previewMedia.AddOption(":no-audio");

        UpdateMetadataDisplay();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => UpdateTimeline();
        _scrubFrameTimer = new DispatcherTimer
        {
            // Wake frequently; PumpScrubFrame applies adaptive back-pressure.
            Interval = TimeSpan.FromMilliseconds(8)
        };
        _scrubFrameTimer.Tick += ScrubFrameTimer_Tick;

        _scrubFinishPauseTimer = new DispatcherTimer
        {
            // Allow the actively decoding player just enough time to present
            // the exact release frame before freezing it again.
            Interval = TimeSpan.FromMilliseconds(90)
        };
        _scrubFinishPauseTimer.Tick += ScrubFinishPauseTimer_Tick;

        _scrubHoldFreezeTimer = new DispatcherTimer
        {
            // Keep the decoder active only long enough to present the latest
            // requested scrub frame, then freeze while the mouse stays held.
            Interval = TimeSpan.FromMilliseconds(55)
        };
        _scrubHoldFreezeTimer.Tick += ScrubHoldFreezeTimer_Tick;

        _scrubPauseWatchdogTimer = new DispatcherTimer
        {
            // While the mouse is held on the timeline, continuously enforce
            // the frozen state except during the short decode window opened
            // by PumpScrubFrame.
            Interval = TimeSpan.FromMilliseconds(20)
        };
        _scrubPauseWatchdogTimer.Tick += ScrubPauseWatchdogTimer_Tick;

        _hoverFramePauseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(90)
        };
        _hoverFramePauseTimer.Tick += HoverFramePauseTimer_Tick;

        _player.EndReached += Player_EndReached;
        _player.EncounteredError += Player_EncounteredError;
        _player.Opening += Player_Opening;
        _player.Buffering += Player_Buffering;
        _player.Stopped += Player_Stopped;
        _player.TimeChanged += Player_TimeChanged;
        _player.Playing += Player_Playing;
        _player.Paused += Player_Paused;

        _previewPlayer.EncounteredError += PreviewPlayer_EncounteredError;
        _previewPlayer.Playing += PreviewPlayer_Playing;

        DpiChanged += (_, args) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            AppLogger.Write(
                $"Player DPI changed: old={args.OldDpi.PixelsPerInchX:F0} new={args.NewDpi.PixelsPerInchX:F0} hwnd=0x{hwnd.ToInt64():X}");
        };

        Loaded += (_, _) =>
        {
            AttachNativeInputHook();
            ConfigureNativeVideoHost(VideoView);

            if (!_player.Play(_media))
                System.Windows.MessageBox.Show(this, "libVLC could not start this recording.", "Playback error",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);

            _timer.Start();

            // Keep the WPF window itself focusable for normal controls, but
            // native libVLC focus is also handled by the thread message hook.
            Focus();
        };

        Closed += (_, _) =>
        {
            DetachNativeInputHook();

            _timer.Stop();
            _scrubFrameTimer.Stop();
            _scrubFrameTimer.Tick -= ScrubFrameTimer_Tick;
            _scrubFinishPauseTimer.Stop();
            _scrubFinishPauseTimer.Tick -= ScrubFinishPauseTimer_Tick;
            _scrubHoldFreezeTimer.Stop();
            _scrubHoldFreezeTimer.Tick -= ScrubHoldFreezeTimer_Tick;
            _scrubPauseWatchdogTimer.Stop();
            _scrubPauseWatchdogTimer.Tick -= ScrubPauseWatchdogTimer_Tick;
            _hoverFramePauseTimer.Stop();

            _player.EndReached -= Player_EndReached;
            _player.EncounteredError -= Player_EncounteredError;
            _player.Opening -= Player_Opening;
            _player.Buffering -= Player_Buffering;
            _player.Stopped -= Player_Stopped;
            _player.TimeChanged -= Player_TimeChanged;
            _player.Playing -= Player_Playing;
            _player.Paused -= Player_Paused;

            _previewPlayer.EncounteredError -= PreviewPlayer_EncounteredError;
            _previewPlayer.Playing -= PreviewPlayer_Playing;
            _player.Stop();
            _previewPlayer.Stop();

            VideoView.MediaPlayer = null;
            PreviewVideoView.MediaPlayer = null;

            _previewMedia.Dispose();
            _previewPlayer.Dispose();
            _liveMedia?.Dispose();
            _media.Dispose();
            _player.Dispose();
            _liveDashServer?.Dispose();
        };
    }

    private void UpdateTimeline()
    {
        var length = Math.Max(0, _player.Length);
        var reportedTime = Math.Max(0, _player.Time);
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        RefreshLivePlaybackState(now);

        if (_followingLive)
        {
            _internalTimelineChange = true;
            Timeline.Value = 1;
            _internalTimelineChange = false;
            TimeLabel.Text = _player.IsPlaying ? "LIVE" : "LIVE • Paused";
            PlayPauseButton.Content = _player.IsPlaying ? "Pause" : "Play";
            return;
        }

        if (length > 0)
            _historyLengthMs = length;

        UpdateTimelineTicks(length);

        if (!_displayClockInitialized)
            ResetDisplayClock(reportedTime, now);

        if (_player.IsPlaying && !_mediaEnded)
        {
            var elapsedTicks = now - _displayClockTimestamp;
            var elapsedMs = elapsedTicks * 1000d / System.Diagnostics.Stopwatch.Frequency;

            _displayTimeMs += (long)Math.Round(elapsedMs);
            _displayClockTimestamp = now;

            if (length > 0)
                _displayTimeMs = Math.Clamp(_displayTimeMs, 0, length);

            // libVLC's DASH timestamp can wobble at Steam segment boundaries.
            // Do not re-anchor every frame. Only inspect decoder drift a few
            // times per second and correct it gently unless it is very large.
            var sinceCorrectionTicks = now - _lastCorrectionTimestamp;
            var sinceCorrectionMs =
                sinceCorrectionTicks * 1000d / System.Diagnostics.Stopwatch.Frequency;

            if (sinceCorrectionMs >= 250)
            {
                _lastCorrectionTimestamp = now;

                if (_seekSettling)
                {
                    var targetDistance = Math.Abs(reportedTime - _seekTargetMs);
                    var deadlineReached = now >= _seekSettleDeadlineTimestamp;

                    // libVLC has caught up to the requested location. Once
                    // it's reasonably close, resume normal drift correction.
                    if (targetDistance <= 500)
                    {
                        _seekSettling = false;
                        ResetDisplayClock(reportedTime, now);
                    }
                    else if (deadlineReached)
                    {
                        // Do not snap backward when the grace period expires.
                        // Keep the smooth display where it is and simply
                        // resume normal correction from the next sample.
                        _seekSettling = false;
                        _lastCorrectionTimestamp = now;
                    }
                    // Otherwise ignore stale decoder timestamps completely.
                }
                else
                {
                    var drift = reportedTime - _displayTimeMs;
                    var absDrift = Math.Abs(drift);

                    if (absDrift >= 1200)
                    {
                        // A large discrepancy outside an explicit seek is a
                        // real decoder discontinuity. Follow libVLC.
                        _displayTimeMs = reportedTime;
                        _displayClockTimestamp = now;
                    }
                    else if (absDrift >= 250)
                    {
                        var correction = Math.Clamp(drift * 0.12, -35d, 35d);
                        _displayTimeMs += (long)Math.Round(correction);

                        if (length > 0)
                            _displayTimeMs = Math.Clamp(_displayTimeMs, 0, length);
                    }
                }
            }
        }
        else
        {
            if (_scrubbing)
            {
                _displayTimeMs = NormalizedToMs(_pendingScrubPosition);
            }
            else
            {
                // Normal paused/stopped state follows libVLC's actual time.
                _displayTimeMs = reportedTime;
            }

            _displayClockTimestamp = now;
            _lastCorrectionTimestamp = now;
        }

        if (_mediaEnded && length > 0)
            _displayTimeMs = length;

        if (!_dragging && length > 0)
        {
            _internalTimelineChange = true;
            Timeline.Value = Math.Clamp((double)_displayTimeMs / length, 0d, 1d);
            _internalTimelineChange = false;
        }

        TimeLabel.Text = $"{FormatMs(_displayTimeMs)} / {FormatMs(length)}";
        PlayPauseButton.Content = _mediaEnded ? "Replay" : (_player.IsPlaying ? "Pause" : "Play");
    }

    private void RefreshLivePlaybackState(long now)
    {
        if (_liveDashServer is null)
            return;

        var elapsed = (now - _lastLiveStateCheckTimestamp) * 1000d /
                      System.Diagnostics.Stopwatch.Frequency;
        if (_lastLiveStateCheckTimestamp != 0 && elapsed < 2_000)
            return;

        _lastLiveStateCheckTimestamp = now;
        _item.IsLive = _item.SessionPaths.Any(LiveRecordingService.IsActivelyRecording);
        LivePlaybackBadge.Visibility = _item.IsLive ? Visibility.Visible : Visibility.Collapsed;
        GoLiveButton.Visibility = _item.IsLive ? Visibility.Visible : Visibility.Collapsed;
        ClipInfoText.Text = $"{_item.GameName}  •  {_item.DisplayTime}  •  {_item.RecordingTypeLabel}";
    }

    private void GoLive_Click(object sender, RoutedEventArgs e)
    {
        if (_liveDashServer is null || !_item.IsLive)
            return;

        _liveMedia ??= _vlc.CreatePlaybackMedia(
            _liveDashServer.LiveManifestUri, isLive: true, useHardwareDecoding: true);
        _followingLive = true;
        _activeMedia = _liveMedia;
        _pendingRestartSeekMs = null;
        _mediaEnded = false;
        ShowVideoTransitionCover("switching to dynamic live playback");
        AppLogger.Write(
            $"Go Live switching to dynamic session. uri={_liveDashServer.LiveManifestUri} " +
            $"sessions={string.Join(" | ", _item.SessionPaths)} state={_player.State}");
        _player.Stop();
        if (!_player.Play(_activeMedia))
            AppLogger.Write("libVLC could not start the dynamic live session.", "ERROR");
    }

    private void Player_EndReached(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_followingLive && _item.IsLive && _liveDashServer is not null)
            {
                _mediaEnded = false;
                PlayPauseButton.Content = "Following live";
                if (!_liveRecoveryPending)
                    _ = RecoverLiveEdgeAsync();
                return;
            }

            _mediaEnded = true;
            _seekSettling = false;
            _displayClockInitialized = false;

            if (_player.Length > 0)
            {
                _displayTimeMs = _player.Length;
                Timeline.Value = 1;
                TimeLabel.Text = $"{FormatMs(_player.Length)} / {FormatMs(_player.Length)}";
            }

            PlayPauseButton.Content = "Replay";
        }));
    }

    private void Player_EncounteredError(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ShowVideoTransitionCover("player error");
            AppLogger.Write(
                    $"Main player encountered an error. path={_item.Path} live={_item.IsLive} " +
                    $"sessions={_item.SessionPaths.Count} time={_player.Time}ms length={_player.Length}ms " +
                    $"position={_player.Position:F4} state={_player.State}",
                    "ERROR");
        }));
    }

    private void Player_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        if (!_videoTransitionCovered || _videoTransitionAwaitingPlaying ||
            _videoTransitionHidePending || e.Time <= 0)
            return;

        _videoTransitionHidePending = true;
        var generation = Interlocked.Read(ref _videoTransitionGeneration);
        Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(async () =>
            {
                // TimeChanged can precede presentation by a render interval.
                // Keep the native HWND covered until the decoded frame has
                // had time to reach the Direct3D swap chain.
                await Task.Delay(120);
                if (!_videoTransitionCovered ||
                    generation != Interlocked.Read(ref _videoTransitionGeneration) ||
                    !_player.IsPlaying)
                {
                    _videoTransitionHidePending = false;
                    return;
                }

                VideoTransitionCover.Visibility = Visibility.Collapsed;
                _videoTransitionCovered = false;
                _videoTransitionHidePending = false;
                AppLogger.Write($"Video transition cover removed at {_player.Time}ms.", "DEBUG");
            }));
    }

    private void ShowVideoTransitionCover(string reason)
    {
        Interlocked.Increment(ref _videoTransitionGeneration);
        _videoTransitionCovered = true;
        _videoTransitionHidePending = false;
        _videoTransitionAwaitingPlaying = true;
        VideoTransitionCover.Visibility = Visibility.Visible;
        AppLogger.Write($"Video transition cover shown: {reason}.", "DEBUG");
    }

    private void Player_Opening(object? sender, EventArgs e)
    {
        AppLogger.Write(
            $"Main player opening media. mode={(_followingLive ? "dynamic-live" : "history")} " +
            $"time={_player.Time}ms length={_player.Length}ms state={_player.State}", "DEBUG");
        Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            ConfigureNativeVideoHost(VideoView, _player)));
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(async () =>
        {
            await Task.Delay(100);
            if (IsLoaded)
                ConfigureNativeVideoHost(VideoView, _player);
        }));
    }

    private void Player_Buffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsedMs = (now - _lastBufferingLogTimestamp) * 1000d /
                        System.Diagnostics.Stopwatch.Frequency;
        if (_lastBufferingLogTimestamp != 0 && elapsedMs < 1_000 && e.Cache is > 0 and < 100)
            return;

        _lastBufferingLogTimestamp = now;
        AppLogger.Write(
            $"Main player buffering. mode={(_followingLive ? "dynamic-live" : "history")} " +
            $"cache={e.Cache:F1}% time={_player.Time}ms length={_player.Length}ms state={_player.State}", "DEBUG");
    }

    private void Player_Stopped(object? sender, EventArgs e)
    {
        AppLogger.Write(
            $"Main player stopped. mode={(_followingLive ? "dynamic-live" : "history")} " +
            $"time={_player.Time}ms length={_player.Length}ms state={_player.State}", "DEBUG");
        Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            ConfigureNativeVideoHost(VideoView, _player)));
    }

    private void SeekToLiveEdge()
    {
        var length = Math.Max(0, _player.Length);
        var target = Math.Max(0, length - LiveEdgeSafetyDelayMs);
        if (_player.State == VLCState.Ended)
        {
            if (IsDynamicLivePlayback)
            {
                RestartDynamicLivePlayback("recovering ended live stream");
                return;
            }
            RestartMediaAt(target);
            return;
        }

        _mediaEnded = false;
        _seekSettling = true;
        _seekTargetMs = target;
        _seekSettleDeadlineTimestamp = System.Diagnostics.Stopwatch.GetTimestamp() +
                                       System.Diagnostics.Stopwatch.Frequency * 2;
        if (length > 0)
            _player.Time = target;
        if (!_player.IsPlaying)
            _player.Play();
        ResetDisplayClock(target, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private async Task RecoverLiveEdgeAsync()
    {
        _liveRecoveryPending = true;
        try
        {
            await Task.Delay(1_000);
            if (IsLoaded && _item.IsLive)
            {
                if (IsDynamicLivePlayback)
                    RestartDynamicLivePlayback("refreshing live stream after endpoint");
                else
                {
                    var target = Math.Max(0, Math.Max(0, _player.Length) - LiveEdgeSafetyDelayMs);
                    RestartMediaAt(target);
                }
            }
        }
        finally
        {
            _liveRecoveryPending = false;
        }
    }

    private void Player_Playing(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _mediaEnded = false;
            _videoTransitionAwaitingPlaying = false;
            AppLogger.Write(
                $"Main player entered Playing. live={_item.IsLive} time={_player.Time}ms " +
                $"length={_player.Length}ms position={_player.Position:F4} state={_player.State}", "DEBUG");

            if (!_scrubbing)
                EnsureMainAudioEnabled();

            if (_scrubbing)
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();

                if (_scrubWakeUntilTimestamp == 0 ||
                    now > _scrubWakeUntilTimestamp)
                {
                    try
                    {
                        _player.SetPause(true);
                        AppLogger.Write(
                            $"Unexpected Playing state suppressed during timeline scrub. actual={_player.Time}ms.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.WriteException(
                            "Could not suppress Playing during scrub",
                            ex);
                    }

                    return;
                }
            }

            if (_pendingRestartSeekMs.HasValue)
            {
                var target = _pendingRestartSeekMs.Value;
                _pendingRestartSeekMs = null;
                _player.Time = target;
                BeginSeekSettlement(target);
            }
            else
            {
                _seekSettling = false;
                ResetDisplayClock(Math.Max(0, _player.Time));
            }

        }));
    }

    private void EnsureMainAudioEnabled()
    {
        try
        {
            // libVLC creates its audio output asynchronously. Apply the UI's
            // state after playback starts so the initial 100% setting reaches
            // the active output device rather than only the player wrapper.
            _player.Volume = _desiredVolume;
            _player.Mute = _desiredMuted || _desiredVolume <= 0;

            if (_player.AudioTrack < 0)
            {
                var audioTrack = _player.AudioTrackDescription?
                    .FirstOrDefault(track => track.Id >= 0);

                if (audioTrack.HasValue)
                    _player.SetAudioTrack(audioTrack.Value.Id);
            }

            AppLogger.Write(
                $"Main player audio ready. tracks={_player.AudioTrackCount} " +
                $"selected={_player.AudioTrack} volume={_player.Volume} muted={_player.Mute}");
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Could not initialize main-player audio", ex);
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
            return;

        var volume = (int)Math.Round(e.NewValue);
        _desiredVolume = volume;
        _player.Volume = volume;
        VolumeValueText.Text = $"{volume}%";

        if (volume > 0)
        {
            _lastAudibleVolume = volume;
            _desiredMuted = false;
            _player.Mute = false;
            MuteButton.Content = "Mute";
        }
        else
        {
            _desiredMuted = true;
            _player.Mute = true;
            MuteButton.Content = "Unmute";
        }
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_player.Mute || VolumeSlider.Value <= 0)
        {
            if (VolumeSlider.Value <= 0)
                VolumeSlider.Value = _lastAudibleVolume;
            else
                _player.Mute = false;

            _desiredMuted = false;
            MuteButton.Content = "Mute";
            return;
        }

        _desiredMuted = true;
        _player.Mute = true;
        MuteButton.Content = "Unmute";
    }

    private void Player_Paused(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                AppLogger.Write(
                    $"Main player Paused. scrubbing={_scrubbing} pausePending={_scrubPausePending} releasePending={_scrubReleasePending} time={_player.Time}ms state={_player.State}");

                // Scrubbing no longer waits for a paused-player seek path.
            }
            catch (Exception ex)
            {
                AppLogger.WriteException("Paused-event scrub handling failed", ex);
            }
        }));
    }

    private void AttachNativeInputHook()
    {
        if (_nativeInputHookAttached)
            return;

        try
        {
            _mouseHookProc = LowLevelMouseHookCallback;
            _keyboardHookProc = LowLevelKeyboardHookCallback;

            var module = GetModuleHandle(null);

            _mouseHook = SetWindowsHookEx(
                WhMouseLl,
                _mouseHookProc,
                module,
                0);

            _keyboardHook = SetWindowsHookEx(
                WhKeyboardLl,
                _keyboardHookProc,
                module,
                0);

            if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                AppLogger.Write(
                    $"Low-level input hook setup incomplete. mouse=0x{_mouseHook.ToInt64():X} keyboard=0x{_keyboardHook.ToInt64():X} error={error}",
                    "ERROR");

                DetachNativeInputHook();
                return;
            }

            _nativeInputHookAttached = true;
            AppLogger.Write("Player low-level mouse/keyboard hooks attached.");
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Low-level input hook setup failed", ex);
            DetachNativeInputHook();
        }
    }

    private void DetachNativeInputHook()
    {
        try
        {
            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }

            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Low-level input hook teardown failed", ex);
        }
        finally
        {
            _nativeInputHookAttached = false;
            _mouseHookProc = null;
            _keyboardHookProc = null;
            _spaceKeyDown = false;
        }

        AppLogger.Write("Player low-level mouse/keyboard hooks detached.");
    }

    private IntPtr LowLevelKeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        try
        {
            var message = wParam.ToInt32();
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);

            if (data.VkCode != VkSpace)
                return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            if (message == WmKeyUp)
            {
                _spaceKeyDown = false;
                return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }

            if (message == WmKeyDown &&
                IsPlayerForeground() &&
                !IsTextEntryFocused())
            {
                if (_spaceKeyDown)
                    return (IntPtr)1;

                _spaceKeyDown = true;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    AppLogger.Write(
                        $"Low-level Space toggle. actual={_player.Time}ms state={_player.State}");
                    TogglePlayback("Space");
                }));

                return (IntPtr)1;
            }
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Low-level keyboard hook failed", ex);
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr LowLevelMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        try
        {
            if (!IsPlayerForeground())
                return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

            var message = wParam.ToInt32();
            var data = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
            var point = data.Point;

            switch (message)
            {
                case WmMouseWheel:
                {
                    if (!IsPointOverPlayerWindow(point))
                        break;

                    var wheelDelta = unchecked((short)((data.MouseData >> 16) & 0xFFFF));
                    var direction = Math.Sign(wheelDelta);
                    if (direction == 0)
                        break;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var deltaMs = direction * 5000L;
                        AppLogger.Write(
                            $"Low-level wheel seek: delta={deltaMs}ms actual={_player.Time}ms state={_player.State}");
                        SeekRelative(deltaMs);
                    }));

                    return (IntPtr)1;
                }

                case WmLButtonDown:
                {
                    // Timeline interaction owns the left button exclusively.
                    // Never let the native video-click shortcut compete with
                    // a slider drag/capture.
                    if (_scrubbing ||
                        Timeline.IsMouseCaptured ||
                        IsPointOverTimeline(point))
                    {
                        break;
                    }

                    if (!IsPointOverVideo(point))
                        break;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_scrubbing || Timeline.IsMouseCaptured)
                            return;

                        AppLogger.Write(
                            $"Low-level video click toggle. actual={_player.Time}ms state={_player.State}");
                        TogglePlayback("video click");
                    }));

                    return (IntPtr)1;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Low-level mouse hook failed", ex);
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool IsPlayerForeground()
    {
        var playerHwnd = new WindowInteropHelper(this).Handle;
        if (playerHwnd == IntPtr.Zero)
            return false;

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;

        if (foreground == playerHwnd)
            return true;

        // libVLC/WPF interop can place focus on a native child or owned HWND
        // rather than the WPF top-level HWND. Treat the entire player HWND
        // hierarchy as foreground so native click/wheel shortcuts do not
        // disappear when the video surface owns focus.
        if (IsChild(playerHwnd, foreground))
            return true;

        var foregroundRoot = GetAncestor(foreground, GaRoot);
        if (foregroundRoot == playerHwnd)
            return true;

        var foregroundRootOwner = GetAncestor(foreground, GaRootOwner);
        if (foregroundRootOwner == playerHwnd)
            return true;

        var playerRootOwner = GetAncestor(playerHwnd, GaRootOwner);
        return playerRootOwner != IntPtr.Zero &&
               foregroundRootOwner == playerRootOwner;
    }

    private bool IsPointOverPlayerWindow(NativePoint screenPoint)
    {
        if (!TryScreenPointToWindowDip(screenPoint, out var windowPoint))
            return false;

        return windowPoint.X >= 0 &&
               windowPoint.X < ActualWidth &&
               windowPoint.Y >= 0 &&
               windowPoint.Y < ActualHeight;
    }

    private bool IsPointOverVideo(NativePoint screenPoint)
    {
        return IsPointOverElement(VideoView, screenPoint);
    }

    private bool IsPointOverTimeline(NativePoint screenPoint)
    {
        return IsPointOverElement(Timeline, screenPoint);
    }

    private bool IsPointOverElement(
        FrameworkElement element,
        NativePoint screenPoint)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;

        if (!TryScreenPointToWindowDip(screenPoint, out var windowPoint))
            return false;

        try
        {
            var transform = element.TransformToAncestor(this);
            var bounds = transform.TransformBounds(
                new Rect(
                    0,
                    0,
                    element.ActualWidth,
                    element.ActualHeight));

            return bounds.Contains(windowPoint);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryScreenPointToWindowDip(
        NativePoint screenPoint,
        out Point windowPoint)
    {
        windowPoint = default;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return false;

        // WH_MOUSE_LL supplies screen coordinates in Win32 device pixels.
        // Convert those pixels into this HWND's client coordinates first,
        // then use the HWND's CURRENT monitor DPI transform to convert to
        // WPF device-independent pixels. This stays correct as the window
        // moves between monitors with different scaling.
        var clientPoint = screenPoint;
        if (!ScreenToClient(hwnd, ref clientPoint))
            return false;

        var source = HwndSource.FromHwnd(hwnd);
        var fromDevice =
            source?.CompositionTarget?.TransformFromDevice
            ?? System.Windows.Media.Matrix.Identity;

        windowPoint = fromDevice.Transform(
            new Point(clientPoint.X, clientPoint.Y));

        return true;
    }


    private delegate IntPtr LowLevelMouseProc(
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    private delegate IntPtr LowLevelKeyboardProc(
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(
        IntPtr hWnd,
        uint gaFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(
        IntPtr hWndParent,
        IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(
        IntPtr hWnd,
        ref NativePoint lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr hWnd,
        out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
    private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int fnObject);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, [In] ref NativeRect lprc, IntPtr hbr);

    private static bool ConfigureNativeVideoHost(Control view, MediaPlayer? player = null)
    {
        const int gclpBackgroundBrush = -10;
        const int blackBrush = 4;

        view.ApplyTemplate();
        if (view.Template?.FindName("PART_PlayerHost", view) is not HwndHost host ||
            host.Handle == IntPtr.Zero)
        {
            AppLogger.Write("Could not resolve the native libVLC video host for black background setup.", "DEBUG");
            return false;
        }

        if (player is not null)
            player.Hwnd = host.Handle;
        var brush = GetStockObject(blackBrush);
        PaintNativeVideoWindowBlack(host.Handle, brush, gclpBackgroundBrush);
        EnumChildWindows(host.Handle, (child, _) =>
        {
            PaintNativeVideoWindowBlack(child, brush, gclpBackgroundBrush);
            return true;
        }, IntPtr.Zero);
        AppLogger.Write($"Native libVLC video host background set to black. hwnd=0x{host.Handle.ToInt64():X}", "DEBUG");
        return true;
    }

    private static void PaintNativeVideoWindowBlack(IntPtr hwnd, IntPtr brush, int backgroundBrushIndex)
    {
        SetClassLongPtr(hwnd, backgroundBrushIndex, brush);
        if (GetClientRect(hwnd, out var rect))
        {
            var dc = GetDC(hwnd);
            if (dc != IntPtr.Zero)
            {
                FillRect(dc, ref rect, brush);
                ReleaseDC(hwnd, dc);
            }
        }
        InvalidateRect(hwnd, IntPtr.Zero, true);
    }

    private void TogglePlayback(string source)
    {
        try
        {
            if (_scrubbing || Timeline.IsMouseCaptured)
            {
                AppLogger.Write(
                    $"{source}: play/pause ignored because timeline scrubbing is active.");
                return;
            }

            if (_mediaEnded)
            {
                AppLogger.Write($"{source}: replay from start.");
                RestartMediaAt(0);
                return;
            }

            if (_player.IsPlaying)
            {
                AppLogger.Write(
                    $"{source}: pause at {_player.Time}ms state={_player.State}");
                _player.SetPause(true);
            }
            else
            {
                AppLogger.Write(
                    $"{source}: play from {_player.Time}ms state={_player.State}");
                _player.SetPause(false);
            }
        }
        catch (Exception ex)
        {
            AppLogger.WriteException($"{source} play/pause toggle failed", ex);
        }
    }

    private bool IsTextEntryFocused()
    {
        return Keyboard.FocusedElement is System.Windows.Controls.TextBox
            or System.Windows.Controls.PasswordBox;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayback("Play/Pause button");
    }

    private void SeekButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var deltaMs))
        {
            SeekRelative(deltaMs);
        }
    }

    private void ExactTime_Click(object sender, RoutedEventArgs e)
    {
        if (_player.Length <= 0)
            return;

        var current = Math.Clamp(Math.Max(0, _player.Time), 0, _player.Length);
        ExactTimeBox.Text = FormatMs(current);
        TimestampValidationText.Visibility = Visibility.Collapsed;
        ExactTimeBox.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(70, 85, 107));
        ExactTimePopup.IsOpen = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            ExactTimeBox.Focus();
            ExactTimeBox.SelectAll();
        }), DispatcherPriority.Input);
    }

    private void ApplyExactTime_Click(object sender, RoutedEventArgs e) => ApplyExactTimestamp();

    private void ExactTimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyExactTimestamp();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ExactTimePopup.IsOpen = false;
            TimeButton.Focus();
            e.Handled = true;
        }
    }

    private void ApplyExactTimestamp()
    {
        if (!TryParseTimestamp(ExactTimeBox.Text, out var targetMs))
        {
            TimestampValidationText.Text = "Use seconds, MM:SS, or HH:MM:SS.";
            TimestampValidationText.Visibility = Visibility.Visible;
            ExactTimeBox.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(220, 84, 84));
            ExactTimeBox.Focus();
            ExactTimeBox.SelectAll();
            return;
        }

        var seekLength = IsDynamicLivePlayback ? _historyLengthMs : _player.Length;
        targetMs = Math.Clamp(targetMs, 0, Math.Max(0, seekLength));
        ExactTimePopup.IsOpen = false;
        if (IsDynamicLivePlayback)
        {
            SwitchToHistory(targetMs);
            TimeButton.Focus();
            return;
        }
        SeekToNormalizedPosition(targetMs / (double)_player.Length);
        TimeButton.Focus();
    }

    private void Timeline_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (IsDynamicLivePlayback)
        {
            var historyPosition = GetNormalizedMousePosition(e);
            var target = (long)Math.Round(Math.Max(0, _historyLengthMs) * historyPosition);
            SwitchToHistory(target);
            e.Handled = true;
            return;
        }

        _dragging = true;
        _scrubbing = true;
        _fineScrubbing = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _fineScrubAnchorMs = Math.Clamp(Math.Max(0, _player.Time), 0, Math.Max(0, _player.Length));
        _fineScrubAnchorMouseX = e.GetPosition(Timeline).X;
        _scrubReleasePending = false;
        _scrubPausePending = false;
        _resumeAfterScrub = _player.IsPlaying;
        _mainMuteBeforeScrub = _player.Mute;

        _scrubFinishPauseTimer.Stop();
        _scrubHoldFreezeTimer.Stop();
        _scrubPauseWatchdogTimer.Stop();
        _scrubFinishTargetMs = -1;
        _scrubHoldTargetMs = -1;
        _scrubWakeUntilTimestamp = 0;

        TimelinePreviewPopup.IsOpen = false;
        SuspendHoverPreviewForPlayback();

        _scrubTargetDirty = true;
        _lastIssuedScrubTargetMs = -1;
        _lastScrubIssueTimestamp = 0;
        _lastScrubDiagnosticTimestamp = 0;

        Timeline.CaptureMouse();
        _scrubPauseWatchdogTimer.Start();

        var normalized = GetScrubNormalizedPosition(e);
        _pendingScrubPosition = normalized;

        _internalTimelineChange = true;
        Timeline.Value = normalized;
        _internalTimelineChange = false;

        try
        {
            // Keep the existing visible decoder alive while dragging. This
            // avoids paused-DASH seeks (which did not render) and avoids a
            // second MediaPlayer/Direct3D output window.
            _player.Mute = true;

            AppLogger.Write(
                $"Timeline scrub started on main player. target={NormalizedToMs(normalized)}ms " +
                $"actual={_player.Time}ms wasPlaying={_resumeAfterScrub} state={_player.State}");

            StartScrubPump();
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Timeline scrub start failed", ex);
        }

        e.Handled = true;
    }

    private void Timeline_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_scrubbing)
            return;

        var target = GetScrubNormalizedPosition(e);
        var targetMs = NormalizedToMs(target);
        var resume = _resumeAfterScrub;

        _pendingScrubPosition = target;

        _internalTimelineChange = true;
        Timeline.Value = target;
        _internalTimelineChange = false;

        if (Timeline.IsMouseCaptured)
            Timeline.ReleaseMouseCapture();

        _dragging = false;
        _scrubbing = false;
        _fineScrubbing = false;
        _scrubFrameTimer.Stop();
        _scrubHoldFreezeTimer.Stop();
        _scrubPauseWatchdogTimer.Stop();
        _scrubHoldTargetMs = -1;
        _scrubWakeUntilTimestamp = 0;
        _scrubTargetDirty = false;

        try
        {
            // Wake the decoder for one final exact frame if the held scrub
            // timer has already frozen it.
            if (!_player.IsPlaying)
                _player.SetPause(false);

            _player.Time = targetMs;
            _displayTimeMs = targetMs;
            TimeLabel.Text = $"{FormatMs(targetMs)} / {FormatMs(_player.Length)}";

            if (resume && !_mediaEnded)
            {
                _player.Mute = _mainMuteBeforeScrub;
                _resumeAfterScrub = false;

                AppLogger.Write(
                    $"Timeline scrub released; continuing playback at {targetMs}ms.");
            }
            else
            {
                // Keep decoding briefly so the selected frame is actually
                // presented, then pause/freeze on that location.
                _scrubFinishTargetMs = targetMs;
                _scrubFinishPauseTimer.Stop();
                _scrubFinishPauseTimer.Start();

                AppLogger.Write(
                    $"Timeline scrub released; freezing selected frame at {targetMs}ms after decode settle.");
            }
        }
        catch (Exception ex)
        {
            _player.Mute = _mainMuteBeforeScrub;
            _resumeAfterScrub = false;
            AppLogger.WriteException("Timeline scrub release failed", ex);
        }

        e.Handled = true;
    }

    private void Timeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Mouse scrubbing is handled explicitly in PreviewMouseMove. Keep
        // ValueChanged only as a fallback for keyboard/template-driven thumb
        // movement that may occur while a scrub is active.
        if (_internalTimelineChange || !_scrubbing)
            return;

        _pendingScrubPosition = e.NewValue;
        _scrubTargetDirty = true;

        if (!_scrubFrameTimer.IsEnabled)
            StartScrubPump();
    }

    private void StartScrubPump()
    {
        if (!_scrubbing)
            return;

        _scrubTargetDirty = true;

        if (!_scrubFrameTimer.IsEnabled)
            _scrubFrameTimer.Start();

        PumpScrubFrame(force: true);
    }

    private void ScrubFrameTimer_Tick(object? sender, EventArgs e)
    {
        if (!_scrubbing)
        {
            _scrubFrameTimer.Stop();
            return;
        }

        PumpScrubFrame(force: false);
    }

    private void PumpScrubFrame(bool force)
    {
        if (!_scrubTargetDirty || !_scrubbing)
            return;

        var length = _player.Length;
        if (length <= 0)
            return;

        var exactTargetMs = NormalizedToMs(_pendingScrubPosition);
        var longVideo = length > LongVideoThresholdMs;
        var targetMs = longVideo
            ? Math.Clamp(
                (long)Math.Round(exactTargetMs / (double)LongVideoPreviewSegmentMs) * LongVideoPreviewSegmentMs,
                0,
                length)
            : exactTargetMs;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();

        var elapsedSinceIssueMs = _lastScrubIssueTimestamp == 0
            ? double.MaxValue
            : (now - _lastScrubIssueTimestamp) * 1000d /
              System.Diagnostics.Stopwatch.Frequency;

        var actualMs = Math.Max(0, _player.Time);
        var distanceFromPreviousRequest =
            _lastIssuedScrubTargetMs < 0
                ? 0
                : Math.Abs(actualMs - _lastIssuedScrubTargetMs);

        // The player is actively decoding while scrubbing. Aim for about
        // 30 visible updates/sec when it is keeping up, and reduce pressure
        // if fragmented-DASH seeking begins to lag.
        var minimumIssueIntervalMs = longVideo
            ? 120d
            : distanceFromPreviousRequest <= 350 ? 33d : 70d;

        if (!force && elapsedSinceIssueMs < minimumIssueIntervalMs)
            return;

        if (!force &&
            _lastIssuedScrubTargetMs >= 0 &&
            Math.Abs(targetMs - _lastIssuedScrubTargetMs) < (longVideo ? LongVideoPreviewSegmentMs : 16))
        {
            _scrubTargetDirty = false;
            return;
        }

        try
        {
            _scrubTargetDirty = false;
            _lastIssuedScrubTargetMs = targetMs;
            _lastScrubIssueTimestamp = now;

            // This is the same MediaPlayer that permanently owns VideoView.
            // No HWND/output target is changed during scrubbing.
            //
            // Paused DASH seeks do not reliably repaint, so wake the decoder
            // briefly for each requested position and then freeze it again.
            // PumpScrubFrame is the ONLY code allowed to wake playback
            // while the mouse button remains held.
            var wakeNow = System.Diagnostics.Stopwatch.GetTimestamp();
            _scrubWakeUntilTimestamp =
                wakeNow +
                (long)(System.Diagnostics.Stopwatch.Frequency * 0.060);

            if (!_player.IsPlaying)
                _player.SetPause(false);

            _player.Time = targetMs;

            _scrubHoldTargetMs = targetMs;
            _scrubHoldFreezeTimer.Stop();
            _scrubHoldFreezeTimer.Start();

            _displayTimeMs = exactTargetMs;
            TimeLabel.Text = $"{FormatMs(exactTargetMs)} / {FormatMs(length)}";

            LogMainScrubLag(targetMs, actualMs, now);
        }
        catch (Exception ex)
        {
            _scrubTargetDirty = true;
            AppLogger.WriteException(
                $"Timeline main-player scrub seek failed. requested={targetMs}ms actual={_player.Time}ms state={_player.State}",
                ex);
        }
    }

    private void LogMainScrubLag(long targetMs, long actualMs, long now)
    {
        var sinceLogMs = _lastScrubDiagnosticTimestamp == 0
            ? double.MaxValue
            : (now - _lastScrubDiagnosticTimestamp) * 1000d /
              System.Diagnostics.Stopwatch.Frequency;

        if (sinceLogMs < 1000)
            return;

        var lag = Math.Abs(actualMs - targetMs);

        if (lag >= 750)
        {
            _lastScrubDiagnosticTimestamp = now;
            AppLogger.Write(
                $"Timeline main-player scrub lag: requested={targetMs}ms actual={actualMs}ms lag={lag}ms " +
                $"state={_player.State} fps={_player.Fps:F2}",
                "WARN");
        }
    }

    private void ScrubHoldFreezeTimer_Tick(object? sender, EventArgs e)
    {
        _scrubHoldFreezeTimer.Stop();

        if (!_scrubbing || _scrubHoldTargetMs < 0)
            return;

        var targetMs = _scrubHoldTargetMs;

        try
        {
            // The mouse is still down. Freeze on the frame that was just
            // decoded; another drag movement will wake the same player again.
            _scrubWakeUntilTimestamp = 0;
            _player.SetPause(true);

            _displayTimeMs = targetMs;
            TimeLabel.Text = $"{FormatMs(targetMs)} / {FormatMs(_player.Length)}";

            AppLogger.Write(
                $"Timeline scrub held-frame freeze. target={targetMs}ms actual={_player.Time}ms state={_player.State}");
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Could not freeze held scrub frame", ex);
        }
    }

    private void ScrubPauseWatchdogTimer_Tick(object? sender, EventArgs e)
    {
        if (!_scrubbing)
        {
            _scrubPauseWatchdogTimer.Stop();
            return;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();

        // The scrub pump opens a very short decode window after each new
        // target. Outside that window, the timeline owns playback state and
        // the player must remain frozen even if libVLC asynchronously
        // transitions back to Playing after a DASH seek.
        if (_scrubWakeUntilTimestamp != 0 &&
            now <= _scrubWakeUntilTimestamp)
        {
            return;
        }

        if (_player.IsPlaying)
        {
            try
            {
                _player.SetPause(true);
                AppLogger.Write(
                    $"Timeline scrub watchdog re-froze player. actual={_player.Time}ms state={_player.State}");
            }
            catch (Exception ex)
            {
                AppLogger.WriteException(
                    "Timeline scrub watchdog could not pause player",
                    ex);
            }
        }
    }

    private void ScrubFinishPauseTimer_Tick(object? sender, EventArgs e)
    {
        _scrubFinishPauseTimer.Stop();

        if (_scrubFinishTargetMs < 0)
            return;

        var targetMs = _scrubFinishTargetMs;
        _scrubFinishTargetMs = -1;

        try
        {
            // Reassert the target immediately before pausing so the frozen
            // frame stays as close as possible to the user's release point.
            _player.Time = targetMs;
            _player.SetPause(true);
            _player.Mute = _mainMuteBeforeScrub;
            _resumeAfterScrub = false;

            _displayTimeMs = targetMs;
            Timeline.Value = _player.Length > 0
                ? Math.Clamp(targetMs / (double)_player.Length, 0d, 1d)
                : 0d;
            TimeLabel.Text = $"{FormatMs(targetMs)} / {FormatMs(_player.Length)}";

            AppLogger.Write(
                $"Timeline scrub frozen on selected frame. target={targetMs}ms actual={_player.Time}ms state={_player.State}");
        }
        catch (Exception ex)
        {
            _player.Mute = _mainMuteBeforeScrub;
            _resumeAfterScrub = false;
            AppLogger.WriteException("Could not freeze final scrub frame", ex);
        }
    }

    private void Timeline_MouseEnter(object sender, MouseEventArgs e)
    {
        UpdateTimelinePreview(e, force: true);
    }

    private void Timeline_MouseLeave(object sender, MouseEventArgs e)
    {
        TimelinePreviewPopup.IsOpen = false;
        SuspendHoverPreviewForPlayback();
    }

    private void Timeline_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_scrubbing && e.LeftButton == MouseButtonState.Pressed)
        {
            var normalized = GetScrubNormalizedPosition(e);

            _internalTimelineChange = true;
            Timeline.Value = normalized;
            _internalTimelineChange = false;

            _pendingScrubPosition = normalized;
            _scrubTargetDirty = true;

            if (!_scrubFrameTimer.IsEnabled)
                StartScrubPump();

            e.Handled = true;
            return;
        }

        if (!_scrubbing)
            UpdateTimelinePreview(e, force: false);
    }

    private void UpdateTimelinePreview(MouseEventArgs e, bool force)
    {
        if (Timeline.ActualWidth <= 0 || _player.Length <= 0)
            return;

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsedMs = _lastPreviewSeekTimestamp == 0
            ? double.MaxValue
            : (now - _lastPreviewSeekTimestamp) * 1000d / System.Diagnostics.Stopwatch.Frequency;

        // Preview decoding is throttled just enough to avoid flooding
        // libVLC while still feeling responsive as the pointer moves.
        var point = e.GetPosition(Timeline);
        var normalized = Math.Clamp(point.X / Timeline.ActualWidth, 0d, 1d);
        var targetMs = Math.Clamp(
            (long)Math.Round(_player.Length * normalized),
            0,
            _player.Length);

        PositionPreviewPopup(point.X, targetMs);
        TimelinePreviewPopup.IsOpen = true;

        if (!force && elapsedMs < 16)
            return;

        _lastPreviewSeekTimestamp = now;

        EnsurePreviewStarted(targetMs);

        if (!_previewStarted)
            return;

        if (_player.IsPlaying)
            ShowStaticHoverFrame(targetMs);
        else
            ShowAnimatedHoverPreview(targetMs);
    }

    private void ShowAnimatedHoverPreview(long targetMs)
    {
        try
        {
            _hoverFramePauseTimer.Stop();
            _lastHoverPreviewTargetMs = targetMs;

            if (!_previewPlayer.IsPlaying)
                _previewPlayer.SetPause(false);

            _previewPlayer.Time = targetMs;

            LogPreviewProgressIfStalled(
                "hover-animated",
                _previewPlayer,
                targetMs,
                ref _lastHoverPreviewProgressLogTimestamp);
        }
        catch (Exception ex)
        {
            AppLogger.WriteException(
                $"Animated hover preview failed. requested={targetMs}ms actual={_previewPlayer.Time}ms",
                ex);
            RestartHoverPreview(targetMs);
        }
    }

    private void ShowStaticHoverFrame(long targetMs)
    {
        try
        {
            _lastHoverPreviewTargetMs = targetMs;
            _hoverStaticTargetMs = targetMs;

            // Wake the muted preview decoder only long enough to decode the
            // requested frame. A short timer pauses it again, avoiding a
            // continuously-running second DASH stream during main playback.
            if (!_previewPlayer.IsPlaying)
                _previewPlayer.SetPause(false);

            _previewPlayer.Time = targetMs;

            _hoverFramePauseTimer.Stop();
            _hoverFramePauseTimer.Start();

            LogPreviewProgressIfStalled(
                "hover-static",
                _previewPlayer,
                targetMs,
                ref _lastHoverPreviewProgressLogTimestamp);
        }
        catch (Exception ex)
        {
            AppLogger.WriteException(
                $"Static hover frame failed. requested={targetMs}ms actual={_previewPlayer.Time}ms",
                ex);
            RestartHoverPreview(targetMs);
        }
    }

    private void HoverFramePauseTimer_Tick(object? sender, EventArgs e)
    {
        _hoverFramePauseTimer.Stop();

        try
        {
            if (_previewStarted && _previewPlayer.IsPlaying)
            {
                _previewPlayer.SetPause(true);
                AppLogger.Write(
                    $"Static hover frame decoder paused. requested={_hoverStaticTargetMs}ms actual={_previewPlayer.Time}ms state={_previewPlayer.State}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Static hover frame pause failed", ex);
        }
    }

    private void SuspendHoverPreviewForPlayback()
    {
        _hoverFramePauseTimer.Stop();

        try
        {
            if (_previewStarted && _previewPlayer.IsPlaying)
                _previewPlayer.SetPause(true);
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Hover preview suspend failed", ex);
        }
    }

    private long NormalizedToMs(double normalized)
    {
        var length = Math.Max(0, _player.Length);
        if (length <= 0)
            return 0;

        return Math.Clamp(
            (long)Math.Round(length * Math.Clamp(normalized, 0d, 1d)),
            0,
            length);
    }

    private void EnsurePreviewStarted(long targetMs)
    {
        try
        {
            if (_previewStarted)
                return;

            if (!ConfigureNativeVideoHost(PreviewVideoView, _previewPlayer))
            {
                if (!_previewHostRetryPending && TimelinePreviewPopup.IsOpen)
                {
                    _previewHostRetryPending = true;
                    Dispatcher.BeginInvoke(
                        DispatcherPriority.Render,
                        new Action(() =>
                        {
                            _previewHostRetryPending = false;
                            if (!TimelinePreviewPopup.IsOpen)
                                return;
                            EnsurePreviewStarted(_lastHoverPreviewTargetMs >= 0
                                ? _lastHoverPreviewTargetMs
                                : targetMs);
                        }));
                }
                return;
            }

            _lastHoverPreviewTargetMs = targetMs;
            _previewStarted = _previewPlayer.Play(_previewMedia);

            if (!_previewStarted)
            {
                AppLogger.Write(
                    $"Hover preview failed to start. requested={targetMs}ms",
                    "ERROR");
                return;
            }

            AppLogger.Write($"Hover preview decoder started. requested={targetMs}ms");

            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => SeekHoverPreview(targetMs)));
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Hover preview start failed", ex);
            _previewStarted = false;
        }
    }

    private void SeekHoverPreview(long targetMs)
    {
        try
        {
            _lastHoverPreviewTargetMs = targetMs;

            if (!_previewPlayer.IsPlaying)
                _previewPlayer.SetPause(false);

            _previewPlayer.Time = targetMs;
            LogPreviewProgressIfStalled(
                "hover",
                _previewPlayer,
                targetMs,
                ref _lastHoverPreviewProgressLogTimestamp);
        }
        catch (Exception ex)
        {
            AppLogger.WriteException(
                $"Hover preview seek failed. requested={targetMs}ms actual={_previewPlayer.Time}ms",
                ex);

            RestartHoverPreview(targetMs);
        }
    }

    private void RestartHoverPreview(long targetMs)
    {
        try
        {
            AppLogger.Write(
                $"Restarting hover preview decoder. requested={targetMs}ms actual={_previewPlayer.Time}ms",
                "WARN");

            _previewPlayer.Stop();
            _previewStarted = _previewPlayer.Play(_previewMedia);

            if (!_previewStarted)
            {
                AppLogger.Write("Hover preview decoder restart failed.", "ERROR");
                return;
            }

            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() =>
                {
                    try
                    {
                        _previewPlayer.Time = targetMs;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.WriteException("Hover preview post-restart seek failed", ex);
                    }
                }));
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Hover preview restart failed", ex);
            _previewStarted = false;
        }
    }

    private void PreviewPlayer_EncounteredError(object? sender, EventArgs e)
    {
        AppLogger.Write(
            $"Hover preview libVLC EncounteredError. requested={_lastHoverPreviewTargetMs}ms actual={_previewPlayer.Time}ms state={_previewPlayer.State}",
            "ERROR");

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_lastHoverPreviewTargetMs >= 0)
                RestartHoverPreview(_lastHoverPreviewTargetMs);
        }));
    }

    private void PreviewPlayer_Playing(object? sender, EventArgs e)
    {
        AppLogger.Write(
            $"Hover preview libVLC Playing. actual={_previewPlayer.Time}ms state={_previewPlayer.State}");
    }

    private static void LogPreviewProgressIfStalled(
        string kind,
        MediaPlayer player,
        long targetMs,
        ref long lastLogTimestamp)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var sinceLastLogMs = lastLogTimestamp == 0
            ? double.MaxValue
            : (now - lastLogTimestamp) * 1000d /
              System.Diagnostics.Stopwatch.Frequency;

        if (sinceLastLogMs < 1000)
            return;

        var actual = player.Time;
        var distance = Math.Abs(actual - targetMs);

        // Do not spam the log for normal keyframe/DASH seek latency. Log only
        // when the decoder is materially far from the requested preview.
        if (distance >= 1500)
        {
            lastLogTimestamp = now;
            AppLogger.Write(
                $"{kind} preview appears stalled or delayed. requested={targetMs}ms actual={actual}ms distance={distance}ms playing={player.IsPlaying} state={player.State}",
                "WARN");
        }
    }

    private void PositionPreviewPopup(double mouseX, long targetMs)
    {
        PreviewTimeLabel.Text = FormatMs(targetMs);

        const double popupWidth = 334;
        const double popupHeight = 218;
        const double cursorGap = 8;

        // Get the exact pointer location in physical screen pixels.
        var localPoint = Mouse.GetPosition(Timeline);
        var screenPixels = Timeline.PointToScreen(localPoint);

        // WPF Window/Popup offsets use device-independent units. Convert the
        // screen pixel coordinate into the same coordinate space.
        var source = PresentationSource.FromVisual(this);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice
                         ?? System.Windows.Media.Matrix.Identity;
        var screenDip = fromDevice.Transform(screenPixels);

        // Placement=Absolute interprets these offsets directly from the
        // screen origin. No PlacementTarget/PlacementRectangle math and no
        // timeline-width clamping are involved.
        TimelinePreviewPopup.PlacementRectangle = Rect.Empty;
        TimelinePreviewPopup.HorizontalOffset =
            screenDip.X - (popupWidth / 2d);
        TimelinePreviewPopup.VerticalOffset =
            screenDip.Y - popupHeight - cursorGap;

        // Force an already-open Popup to update its HWND location immediately.
        var x = TimelinePreviewPopup.HorizontalOffset;
        TimelinePreviewPopup.HorizontalOffset = x + 0.01;
        TimelinePreviewPopup.HorizontalOffset = x;
    }

    private void Timeline_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                TogglePlayback("timeline Space");
                e.Handled = true;
                break;

            case Key.Left:
                SeekRelative(-5_000);
                e.Handled = true;
                break;

            case Key.Right:
                SeekRelative(5_000);
                e.Handled = true;
                break;

            case Key.PageDown:
                SeekRelative(-30_000);
                e.Handled = true;
                break;

            case Key.PageUp:
                SeekRelative(30_000);
                e.Handled = true;
                break;

            case Key.Home:
                SeekToNormalizedPosition(0);
                e.Handled = true;
                break;

            case Key.End:
                SeekToNormalizedPosition(1);
                e.Handled = true;
                break;
        }
    }

    private double GetNormalizedMousePosition(MouseEventArgs e)
    {
        if (Timeline.ActualWidth <= 0)
            return Timeline.Value;

        var point = e.GetPosition(Timeline);
        return Math.Clamp(point.X / Timeline.ActualWidth, 0d, 1d);
    }

    private double GetScrubNormalizedPosition(MouseEventArgs e)
    {
        if (!_fineScrubbing || Timeline.ActualWidth <= 0 || _player.Length <= 0)
            return GetNormalizedMousePosition(e);

        var mouseX = e.GetPosition(Timeline).X;
        var halfWidth = Math.Max(1d, Timeline.ActualWidth / 2d);
        var offsetMs = (long)Math.Round(
            (mouseX - _fineScrubAnchorMouseX) / halfWidth * FineScrubHalfWindowMs);
        var targetMs = Math.Clamp(_fineScrubAnchorMs + offsetMs, 0, _player.Length);
        return targetMs / (double)_player.Length;
    }

    private void SeekToNormalizedPosition(double normalized)
    {
        normalized = Math.Clamp(normalized, 0d, 1d);

        if (IsDynamicLivePlayback)
        {
            SwitchToHistory((long)Math.Round(Math.Max(0, _historyLengthMs) * normalized));
            return;
        }

        if (_player.Length > 0)
        {
            var targetMs = (long)Math.Round(_player.Length * normalized);
            targetMs = Math.Clamp(targetMs, 0, _player.Length);

            if (_mediaEnded)
            {
                RestartMediaAt(targetMs);
                return;
            }

            _player.Time = targetMs;
            BeginSeekSettlement(targetMs);
        }
        else
        {
            if (_mediaEnded)
            {
                RestartMediaAt(0);
                return;
            }

            _player.Position = (float)normalized;
            ResetDisplayClock(0);
        }

        Timeline.Value = normalized;
        UpdateTimeline();
    }

    private void SeekRelative(long deltaMs)
    {
        if (IsDynamicLivePlayback)
        {
            if (deltaMs >= 0)
                return;
            var historyTarget = Math.Clamp(_historyLengthMs + deltaMs, 0, Math.Max(0, _historyLengthMs));
            SwitchToHistory(historyTarget);
            return;
        }

        if (_player.Length <= 0)
            return;

        var basis = _mediaEnded ? _player.Length : Math.Max(0, _player.Time);
        var target = Math.Clamp(basis + deltaMs, 0, _player.Length);

        if (_mediaEnded)
        {
            RestartMediaAt(target);
            return;
        }

        _player.Time = target;
        Timeline.Value = (double)target / _player.Length;
        BeginSeekSettlement(target);
        UpdateTimeline();
    }

    private void RestartMediaAt(long targetMs)
    {
        var length = Math.Max(0, _player.Length);
        if (length > 0)
            targetMs = Math.Clamp(targetMs, 0, length);
        else
            targetMs = Math.Max(0, targetMs);

        _pendingRestartSeekMs = targetMs;
        _mediaEnded = false;
        ShowVideoTransitionCover("restarting media");
        BeginSeekSettlement(targetMs);

        // EndReached leaves libVLC in a terminal playback state. Starting the
        // Media again creates a fresh playback session; Player_Playing applies
        // the requested seek once that session is ready.
        _player.Stop();

        if (!_player.Play(_activeMedia))
        {
            _pendingRestartSeekMs = null;
            System.Windows.MessageBox.Show(
                this,
                "libVLC could not restart this recording.",
                "Playback error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SwitchToHistory(long targetMs)
    {
        _followingLive = false;
        _activeMedia = _media;
        _pendingRestartSeekMs = Math.Clamp(targetMs, 0, Math.Max(0, _historyLengthMs));
        _mediaEnded = false;
        ShowVideoTransitionCover("returning to historical playback");
        BeginSeekSettlement(_pendingRestartSeekMs.Value);
        AppLogger.Write($"Leaving live mode for combined history at {_pendingRestartSeekMs.Value}ms.");
        _player.Stop();
        if (!_player.Play(_activeMedia))
        {
            _pendingRestartSeekMs = null;
            AppLogger.Write("libVLC could not resume combined historical playback.", "ERROR");
        }
    }

    private void RestartDynamicLivePlayback(string reason)
    {
        if (!IsDynamicLivePlayback)
            return;

        _pendingRestartSeekMs = null;
        _mediaEnded = false;
        ShowVideoTransitionCover(reason);
        AppLogger.Write($"Restarting dynamic live playback without a seek: {reason}.", "DEBUG");
        _player.Stop();
        if (!_player.Play(_activeMedia))
            AppLogger.Write("libVLC could not restart dynamic live playback.", "ERROR");
    }

    private bool IsDynamicLivePlayback => _liveMedia is not null && ReferenceEquals(_activeMedia, _liveMedia);

    private void BeginSeekSettlement(long targetMs)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();

        _seekTargetMs = Math.Max(0, targetMs);
        _seekSettling = true;

        // 1.5 seconds is long enough for libVLC to establish the new DASH
        // decode position without making the UI feel detached from playback.
        _seekSettleDeadlineTimestamp =
            now + (long)(System.Diagnostics.Stopwatch.Frequency * 1.5);

        ResetDisplayClock(_seekTargetMs, now);
    }

    private void ResetDisplayClock(long mediaTime)
    {
        ResetDisplayClock(mediaTime, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private void ResetDisplayClock(long mediaTime, long timestamp)
    {
        _displayTimeMs = Math.Max(0, mediaTime);
        _displayClockTimestamp = timestamp;
        _lastCorrectionTimestamp = timestamp;
        _displayClockInitialized = true;
    }

    private static bool IsMouseOverThumb(MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;

        while (current is not null)
        {
            if (current is System.Windows.Controls.Primitives.Thumb)
                return true;

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void UpdateMetadataDisplay()
    {
        var hasDescription = !string.IsNullOrWhiteSpace(_item.Description);
        DescriptionText.Text = hasDescription
            ? _item.Description.Trim()
            : "Add a description to remember what happened in this recording.";
        DescriptionText.Foreground = new SolidColorBrush(hasDescription
            ? MediaColor.FromRgb(230, 234, 240)
            : MediaColor.FromRgb(111, 122, 138));

        TagsPanel.Children.Clear();

        if (_item.Tags is not { Count: > 0 })
        {
            TagsPanel.Children.Add(new TextBlock
            {
                Text = "Add tags to make this recording easier to find.",
                Foreground = new SolidColorBrush(MediaColor.FromRgb(111, 122, 138)),
                FontSize = 12,
                Margin = new Thickness(0, 3, 0, 0)
            });
            return;
        }

        foreach (var tag in _item.Tags)
        {
            var chip = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromRgb(25, 42, 57)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(52, 79, 101)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(0, 0, 6, 6),
                Child = new TextBlock
                {
                    Text = tag,
                    Foreground = new SolidColorBrush(MediaColor.FromRgb(141, 212, 255)),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold
                }
            };

            TagsPanel.Children.Add(chip);
        }
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        _item.IsFavorite = !_item.IsFavorite;
        _metadata.UpdateFrom(_item);
        UpdateFavoriteButton();
    }

    private void UpdateFavoriteButton()
    {
        FavoriteButton.Content = _item.IsFavorite ? "★ Favorited" : "☆ Favorite";
    }

    private void Description_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextEntryDialog(
            "Edit description",
            "Description:",
            _item.Description)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        _item.Description = dialog.Value.Trim();
        _metadata.UpdateFrom(_item);
        UpdateMetadataDisplay();
    }

    private void Tags_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextEntryDialog(
            "Edit tags",
            "Comma-separated tags:",
            string.Join(", ", _item.Tags))
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        _item.Tags = MetadataService.NormalizeTags(new[] { dialog.Value });
        _metadata.UpdateFrom(_item);
        UpdateMetadataDisplay();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var options = new ExportOptionsWindow(
            _vlc.GetVideoCodec(_item.Path),
            _item.DurationSeconds,
            _item.SizeBytes) { Owner = this };
        if (options.ShowDialog() != true)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export recording to MP4",
            Filter = "MP4 video (*.mp4)|*.mp4",
            FileName = $"{SafeFilePart(_item.GameName)} - {_item.Timestamp:yyyy-MM-dd_HH-mm-ss} - {SafeFilePart(options.SelectedCodecFileLabel)}.mp4"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var wasPlaying = _player.IsPlaying;
        if (wasPlaying)
            _player.Pause();

        var progressWindow = new ExportProgressWindow((progress, cancellationToken) =>
            _vlc.ExportMp4Async(_item, dialog.FileName, options.SelectedCodec,
                options.UseHardwareEncoding, progress, cancellationToken))
        {
            Owner = this
        };

        var completed = progressWindow.ShowDialog() == true;
        ClipInfoText.Text = $"{_item.GameName}  •  {_item.DisplayTime}  •  {_item.RecordingTypeLabel}";

        if (wasPlaying)
            _player.Play();

        if (completed)
        {
            System.Windows.MessageBox.Show(
                this,
                $"Export complete:\n\n{dialog.FileName}",
                "Steam Recording Browser",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private static string SafeFilePart(string value)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');

        return value.Trim();
    }

    private void TimelineTickCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _timelineTicksWidth = -1;
        UpdateTimelineTicks(Math.Max(0, _player.Length));
    }

    private void UpdateTimelineTicks(long lengthMs)
    {
        var width = TimelineTickCanvas.ActualWidth;
        if (lengthMs <= 0 || width <= 0)
            return;

        if (_timelineTicksLength == lengthMs && Math.Abs(_timelineTicksWidth - width) < 0.5)
            return;

        _timelineTicksLength = lengthMs;
        _timelineTicksWidth = width;
        Timeline.SmallChange = Math.Min(1d, 5_000d / lengthMs);
        Timeline.LargeChange = Math.Min(1d, 30_000d / lengthMs);
        TimelineTickCanvas.Children.Clear();

        var intervalMs = ChooseTimelineTickInterval(lengthMs);
        var tickTimes = new List<long>();
        for (long time = 0; time <= lengthMs; time += intervalMs)
            tickTimes.Add(time);

        if (tickTimes.Count == 0 || tickTimes[^1] != lengthMs)
            tickTimes.Add(lengthMs);

        var tickBrush = new SolidColorBrush(MediaColor.FromRgb(74, 89, 110));
        var labelBrush = new SolidColorBrush(MediaColor.FromRgb(152, 162, 179));

        foreach (var time in tickTimes)
        {
            var x = time / (double)lengthMs * width;
            var tick = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = 0,
                Y2 = 4,
                Stroke = tickBrush,
                StrokeThickness = 1
            };
            TimelineTickCanvas.Children.Add(tick);

            var label = new TextBlock
            {
                Text = FormatMs(time),
                Foreground = labelBrush,
                FontSize = 10
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Clamp(x - label.DesiredSize.Width / 2d, 0, Math.Max(0, width - label.DesiredSize.Width)));
            Canvas.SetTop(label, 4);
            TimelineTickCanvas.Children.Add(label);
        }

        if (_item.SessionStartOffsetsSeconds.Count > 1 && _item.DurationSeconds > 0)
        {
            var sessionBrush = new SolidColorBrush(MediaColor.FromRgb(102, 192, 244));
            for (var sessionIndex = 1; sessionIndex < _item.SessionStartOffsetsSeconds.Count; sessionIndex++)
            {
                var offsetSeconds = _item.SessionStartOffsetsSeconds[sessionIndex];
                var sessionStart = sessionIndex < _item.SessionStartTimes.Count
                    ? _item.SessionStartTimes[sessionIndex]
                    : _item.Timestamp.AddSeconds(offsetSeconds);
                var sessionToolTip = $"New gameplay session\n{sessionStart:MMM d, yyyy 'at' h:mm:ss tt}";
                var x = Math.Clamp(offsetSeconds / _item.DurationSeconds * width, 0, width);

                var hoverTarget = new System.Windows.Shapes.Rectangle
                {
                    Width = 18,
                    Height = 18,
                    Fill = System.Windows.Media.Brushes.Transparent,
                    ToolTip = sessionToolTip
                };
                Canvas.SetLeft(hoverTarget, Math.Clamp(x - hoverTarget.Width / 2d, 0, Math.Max(0, width - hoverTarget.Width)));
                Canvas.SetTop(hoverTarget, 0);
                TimelineTickCanvas.Children.Add(hoverTarget);

                TimelineTickCanvas.Children.Add(new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = 0,
                    Y2 = 18,
                    Stroke = sessionBrush,
                    StrokeThickness = 2.5,
                    IsHitTestVisible = false
                });
                var marker = new Polygon
                {
                    Fill = sessionBrush,
                    IsHitTestVisible = false,
                    Points = new System.Windows.Media.PointCollection
                    {
                        new(-4, 0), new(4, 0), new(0, 5)
                    }
                };
                Canvas.SetLeft(marker, x);
                Canvas.SetTop(marker, 0);
                TimelineTickCanvas.Children.Add(marker);
            }
        }
    }

    private static long ChooseTimelineTickInterval(long lengthMs)
    {
        long[] intervals =
        [
            5_000, 10_000, 15_000, 30_000,
            60_000, 2 * 60_000, 5 * 60_000, 10 * 60_000,
            15 * 60_000, 30 * 60_000, 60 * 60_000
        ];

        var desired = lengthMs / 12d;
        return intervals.FirstOrDefault(interval => interval >= desired, intervals[^1]);
    }

    private static bool TryParseTimestamp(string value, out long milliseconds)
    {
        milliseconds = 0;
        var parts = value.Trim().Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 3 ||
            parts.Any(part => !long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        var numbers = parts
            .Select(part => long.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture))
            .ToArray();

        long totalSeconds;
        try
        {
            totalSeconds = numbers.Length switch
            {
                1 => numbers[0],
                2 when numbers[1] < 60 => checked(numbers[0] * 60 + numbers[1]),
                3 when numbers[1] < 60 && numbers[2] < 60 =>
                    checked(numbers[0] * 3600 + numbers[1] * 60 + numbers[2]),
                _ => -1
            };

            if (totalSeconds < 0)
                return false;

            milliseconds = checked(totalSeconds * 1000);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static string FormatMs(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }
}
