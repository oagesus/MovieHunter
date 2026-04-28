using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using MovieHunter.Models;
using MovieHunter.Services;
using MovieHunter.ViewModels;

namespace MovieHunter.Views;

public partial class MainWindow : Window
{
    private readonly LibVLC _libvlc;
    private readonly MediaPlayer _player;
    private Media? _currentMedia;
    private VideoResult? _currentVideoResult;
    // Optimistic-highlight target: the PageUrl the user just clicked in
    // the Recently watched panel. Drives "Currently playing" on the
    // tapped card during the brief stream-URL-extraction delay before
    // OnPlayRequested actually sets _currentVideoResult.
    private string? _pendingPlayingPageUrl;
    // Which view the user was on when playback started — Back_Click restores it.
    private enum AppView { Search, RecentlyWatched, MyList, Settings }
    private AppView _originView = AppView.Search;
    // Currently-active sidebar tab. Tracks where the user is so PiP restore
    // can return playback to the right place.
    private AppView _activeTab = AppView.Search;

    // Picture-in-picture state.
    private bool _isPipMode;
    private bool _pipDragging;
    private Point _pipDragStart;
    private Thickness _pipMarginAtDragStart;
    private bool _pipResizing;
    private Point _pipResizeStart;
    private Size _pipSizeAtResizeStart;
    private Thickness _pipMarginAtResizeStart;
    private string _pipResizeCorner = "BR";
    private DispatcherTimer? _pipHideUiTimer;
    private const double PipPaddingFromEdges = 16;
    private const double PipWidth = 320;
    private const double PipHeight = 180;
    private const double PipAspect = PipWidth / PipHeight;
    private long _pendingSeekMs;
    private bool _isSliderUpdatingFromPlayer;
    private WindowState _preFullScreenState = WindowState.Normal;
    private readonly DispatcherTimer _hideUiTimer;
    private readonly DispatcherTimer _playbackWatchdog;
    private readonly DispatcherTimer _singleTapTimer;
    private bool _hasReceivedFrame;

    private WriteableBitmap? _bitmap;
    private ILockedFramebuffer? _currentFrame;
    private readonly object _bitmapLock = new();
    private int _volumeBeforeMute = 80;
    private bool _isMuted;
    private bool _volumeDragging;
    private bool _pointerOverMute;
    private bool _pointerOverVolumePopup;
    private readonly DispatcherTimer _volumePopupHideTimer;

    private MediaPlayer.LibVLCVideoFormatCb? _formatCb;
    private MediaPlayer.LibVLCVideoCleanupCb? _cleanupCb;
    private MediaPlayer.LibVLCVideoLockCb? _lockCb;
    private MediaPlayer.LibVLCVideoUnlockCb? _unlockCb;
    private MediaPlayer.LibVLCVideoDisplayCb? _displayCb;

    public MainWindow()
    {
        InitializeComponent();

        // Theme RadioButton state is synced in OnDataContextChanged,
        // once the VM (and its Sources.Theme) is available.

        Core.Initialize();
        _libvlc = new LibVLC(
            "--http-reconnect",
            "--network-caching=500",
            "--audio-desync=0",
            "--no-audio-time-stretch",
            "--clock-jitter=0",
            "--clock-synchro=0",
            "--aout=mmdevice");
        _player = new MediaPlayer(_libvlc) { Volume = 100 };

        SetupSoftwareRendering();

        _hideUiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _hideUiTimer.Tick += (_, _) => HideFullscreenUi();

        // If no frame arrives within 12s of Play(), assume the stream is dead
        // and show a clear error instead of leaving a black "Playing" screen.
        _playbackWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _playbackWatchdog.Tick += OnPlaybackWatchdogTick;

        WirePlayerEvents();

        PositionSlider.ValueChanged += OnPositionSliderChanged;
        PositionSlider.IsEnabled = false;
        PipPositionSlider.ValueChanged += OnPositionSliderChanged;
        PipPositionSlider.IsEnabled = false;

        // Track window resizes so the floating PiP frame keeps its
        // distance from the bottom-right edge instead of staying glued
        // to its old top-left coordinate (which clipped on shrink and
        // drifted away from the corner on grow).
        PipHost.SizeChanged += OnPipHostSizeChanged;

        // Highlight the initial sidebar tab (Search by default).
        UpdateActiveNavTab();

        VideoOverlayRoot.PointerMoved += OnOverlayPointerMoved;
        VideoOverlayRoot.PointerEntered += OnOverlayPointerMoved;



        // Single click on video → play/pause (delayed ~250ms so a double
        // click can suppress it). Double click → toggle fullscreen.
        _singleTapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _singleTapTimer.Tick += OnSingleTapTick;
        VideoOverlayRoot.Tapped += OnVideoTapped;
        VideoOverlayRoot.DoubleTapped += OnVideoDoubleTapped;

        // Netflix-style volume popup: opens on mute-button hover, stays
        // open while the pointer is over either the button or the popup
        // content, closes when both lose the pointer. A short timer
        // bridges the tiny gap the pointer traverses between them.
        _volumePopupHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _volumePopupHideTimer.Tick += (_, _) =>
        {
            _volumePopupHideTimer.Stop();
            // Never close while the slider thumb is being dragged —
            // otherwise the popup disappears mid-drag and subsequent
            // clicks land on the video (toggling play/pause).
            if (_volumeDragging) return;
            if (_pointerOverMute || _pointerOverVolumePopup) return;
            VolumePopup.IsOpen = false;
            TimelineRow.IsVisible = true;
        };

        // Drag tracking on the volume slider — used to keep the popup
        // open while the user is holding the thumb, even if the pointer
        // leaves the popup bounds during the drag.
        VolumeSlider.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(VolumeSlider).Properties.IsLeftButtonPressed)
            {
                _volumeDragging = true;
                _volumePopupHideTimer.Stop();
            }
        }, RoutingStrategies.Tunnel);
        VolumeSlider.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            _volumeDragging = false;
            // If the pointer ended up off both the button and popup
            // during the drag, schedule a close now that the drag is
            // done. Otherwise the popup just stays open.
            if (!_pointerOverMute && !_pointerOverVolumePopup)
            {
                _volumePopupHideTimer.Stop();
                _volumePopupHideTimer.Start();
            }
        }, RoutingStrategies.Tunnel);

        DataContextChanged += OnDataContextChanged;
        // Tunnel (preview) so we intercept arrow keys and space BEFORE any
        // focused Slider can handle them. handledEventsToo=true in case the
        // focused control already marked the event handled.
        AddHandler(KeyDownEvent, OnPreviewKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        // Blur search TextBoxes when the user clicks outside of them.
        AddHandler(PointerPressedEvent, OnRootPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerMoved += OnWindowPointerMoved;
        Closing  += OnClosing;
    }

    private void SetupSoftwareRendering()
    {
        _formatCb  = VideoFormat;
        _cleanupCb = VideoCleanup;
        _lockCb    = VideoLock;
        _unlockCb  = VideoUnlock;
        _displayCb = VideoDisplay;

        _player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        _player.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);
    }

    private uint VideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height,
                             ref uint pitches, ref uint lines)
    {
        var chromaBytes = Encoding.ASCII.GetBytes("RV32");
        Marshal.Copy(chromaBytes, 0, chroma, 4);

        var w = (int)width;
        var h = (int)height;
        pitches = (uint)(w * 4);
        lines   = (uint)h;

        var bmp = Dispatcher.UIThread.Invoke(() =>
        {
            var b = new WriteableBitmap(
                new PixelSize(w, h),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);
            VideoImage.Source = b;
            // PipImage shares the same bitmap so PiP mode can mirror frames
            // without an extra render pass.
            if (PipImage is not null) PipImage.Source = b;
            return b;
        });

        lock (_bitmapLock) { _bitmap = bmp; }
        return 1;
    }

    private void VideoCleanup(ref IntPtr opaque) { }

    private IntPtr VideoLock(IntPtr opaque, IntPtr planes)
    {
        Monitor.Enter(_bitmapLock);
        if (_bitmap is null)
        {
            Monitor.Exit(_bitmapLock);
            return IntPtr.Zero;
        }
        _currentFrame = _bitmap.Lock();
        Marshal.WriteIntPtr(planes, _currentFrame.Address);
        return IntPtr.Zero;
    }

    private void VideoUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        if (_currentFrame is not null)
        {
            _currentFrame.Dispose();
            _currentFrame = null;
        }
        Monitor.Exit(_bitmapLock);
    }

    private void VideoDisplay(IntPtr opaque, IntPtr picture)
    {
        Dispatcher.UIThread.Post(() =>
        {
            VideoImage.InvalidateVisual();
            if (_isPipMode) PipImage?.InvalidateVisual();
        });
    }

    private void WirePlayerEvents()
    {
        _player.Playing += (_, _) => UI(() =>
        {
            SetPlayPauseIcons(playing: true);
            // Frames are arriving — drop the loading overlays and flip
            // the bottom-bar status from "Loading…" to "Playing.".
            SetLoadingState(false);
            if (DataContext is MainWindowViewModel vmStatus)
                vmStatus.Status = "Playing.";
            // Apply a saved resume position once playback is underway and
            // the stream is seekable / we know its length.
            if (_pendingSeekMs > 0 && _player.IsSeekable && _player.Length > 0)
            {
                _player.Time = Math.Min(_pendingSeekMs, _player.Length - 1000);
                _pendingSeekMs = 0;
            }
        });
        _player.Paused += (_, _) => UI(() =>
        {
            SetPlayPauseIcons(playing: false);
            SaveCurrentPosition();
        });
        _player.EndReached += (_, _) => UI(() =>
        {
            SetPlayPauseIcons(playing: false);
            // Mark as fully watched. In-place update — the card's already
            // at the top from OnPlayRequested, so we don't need a re-insert
            // (which would destroy and recreate its visual and flicker).
            if (_currentVideoResult is not null && DataContext is MainWindowViewModel vm)
            {
                var len = _player.Length;
                vm.Recent.UpdatePositionAndSave(_currentVideoResult, len > 0 ? len : 0, len);
                vm.MyList.UpdatePositionAndSave(_currentVideoResult, len > 0 ? len : 0, len);
            }
        });
        _player.Stopped    += (_, _) => UI(() =>
        {
            // Only reset the transport when the user actually closed the
            // video (Back_Click / Close clears _currentVideoResult first).
            // For natural end-of-stream we leave the slider at 100% and
            // the time label as-is, so the media stays "loaded" visually
            // and the user can seek back to replay parts.
            if (_currentVideoResult is null) ResetTransport();
        });
        _player.TimeChanged   += OnTimeChanged;
        _player.LengthChanged += OnLengthChanged;
    }

    private static void UI(Action a) => Dispatcher.UIThread.Post(a);

    private void ResetTransport()
    {
        SetPlayPauseIcons(playing: false);
        SetSliderFromPlayer(0);
        DurationLabel.Text = "00:00";
        if (PipDurationLabel is not null) PipDurationLabel.Text = "00:00";
        PositionSlider.IsEnabled = false;
        if (PipPositionSlider is not null) PipPositionSlider.IsEnabled = false;
    }

    // Updates the play/pause icon on both the main transport bar and the
    // PiP mini-transport so they stay in lockstep with the player.
    private void SetPlayPauseIcons(bool playing)
    {
        PlayIcon.IsVisible = !playing;
        PauseIcon.IsVisible = playing;
        if (PipPlayIcon is not null) PipPlayIcon.IsVisible = !playing;
        if (PipPauseIcon is not null) PipPauseIcon.IsVisible = playing;
    }

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        _hasReceivedFrame = true;
        UI(() =>
        {
            _playbackWatchdog.Stop();
            var length = _player.Length;
            if (length > 0)
            {
                PositionSlider.IsEnabled = _player.IsSeekable;
                if (PipPositionSlider is not null)
                    PipPositionSlider.IsEnabled = _player.IsSeekable;
                SetSliderFromPlayer((double)e.Time / length * 1000.0);
                var remaining = FormatTime(
                    TimeSpan.FromMilliseconds(Math.Max(0, length - e.Time)));
                DurationLabel.Text = remaining;
                if (PipDurationLabel is not null) PipDurationLabel.Text = remaining;
                // Push the live position into the matching Recent entry so
                // its progress bar tracks playback. In-memory only — disk
                // save still happens on pause / close / end.
                if (_currentVideoResult is not null
                    && DataContext is MainWindowViewModel vm)
                {
                    vm.Recent.UpdatePositionInMemory(_currentVideoResult, e.Time, length);
                    vm.MyList.UpdatePositionInMemory(_currentVideoResult, e.Time, length);
                }
            }
            else
            {
                PositionSlider.IsEnabled = false;
                if (PipPositionSlider is not null) PipPositionSlider.IsEnabled = false;
            }
        });
    }

    private void OnPlaybackWatchdogTick(object? sender, EventArgs e)
    {
        _playbackWatchdog.Stop();
        if (_hasReceivedFrame) return;

        UI(() =>
        {
            _player.Stop();
            StatusText.Text = "Stream unavailable — try another result or click the globe icon to open in browser.";
            ResetTransport();
        });
    }

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        UI(() =>
        {
            if (e.Length > 0)
            {
                // Remaining-time display: at load, remaining = total length.
                var total = FormatTime(TimeSpan.FromMilliseconds(e.Length));
                DurationLabel.Text = total;
                if (PipDurationLabel is not null) PipDurationLabel.Text = total;
                PositionSlider.IsEnabled = _player.IsSeekable;
                if (PipPositionSlider is not null)
                    PipPositionSlider.IsEnabled = _player.IsSeekable;
            }
            else
            {
                DurationLabel.Text = "—";
                if (PipDurationLabel is not null) PipDurationLabel.Text = "—";
                PositionSlider.IsEnabled = false;
                if (PipPositionSlider is not null) PipPositionSlider.IsEnabled = false;
            }
        });
    }

    private void SetSliderFromPlayer(double value)
    {
        _isSliderUpdatingFromPlayer = true;
        PositionSlider.Value = value;
        if (PipPositionSlider is not null) PipPositionSlider.Value = value;
        _isSliderUpdatingFromPlayer = false;
    }

    private void OnPositionSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isSliderUpdatingFromPlayer) return;

        // Mirror the dragged value to the other transport's slider so
        // both stay in sync. During playback VLC's TimeChanged drives
        // SetSliderFromPlayer which already keeps them aligned; during
        // pause TimeChanged is silent, so without this a seek on one
        // slider would leave the other showing the old position until
        // playback resumes. The flag suppresses re-entry from the other
        // slider's ValueChanged.
        _isSliderUpdatingFromPlayer = true;
        if (ReferenceEquals(sender, PositionSlider))
        {
            if (PipPositionSlider is not null) PipPositionSlider.Value = e.NewValue;
        }
        else if (ReferenceEquals(sender, PipPositionSlider))
        {
            PositionSlider.Value = e.NewValue;
        }
        _isSliderUpdatingFromPlayer = false;

        var fraction = e.NewValue / 1000.0;

        // After end-of-stream the player is in Ended / Stopped state and
        // setting Position alone won't resume — we have to re-Play the
        // media. The Playing event handler picks up _pendingSeekMs and
        // applies the seek there, which means even during the buffering
        // gap the user's latest slider drag wins.
        if (_player.State == VLCState.Ended || _player.State == VLCState.Stopped)
        {
            if (_currentMedia is null) return;
            if (_player.Length > 0)
                _pendingSeekMs = (long)(fraction * _player.Length);
            _player.Play(_currentMedia);
            return;
        }

        if (_player.Length <= 0 || !_player.IsSeekable) return;

        _player.Position = (float)fraction;

        // TimeChanged only fires during active playback, so when seeking
        // while paused we update the remaining-time label and the Recent
        // entry's progress ourselves.
        var newTimeMs = (long)(fraction * _player.Length);
        var remaining = FormatTime(
            TimeSpan.FromMilliseconds(Math.Max(0, _player.Length - newTimeMs)));
        DurationLabel.Text = remaining;
        if (PipDurationLabel is not null) PipDurationLabel.Text = remaining;
        if (_currentVideoResult is not null && DataContext is MainWindowViewModel vm)
        {
            vm.Recent.UpdatePositionInMemory(_currentVideoResult, newTimeMs, _player.Length);
            vm.MyList.UpdatePositionInMemory(_currentVideoResult, newTimeMs, _player.Length);
        }
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PlayRequested += OnPlayRequested;

            // Restore saved volume (triggers Volume_Changed which updates
            // the player volume and mute icon).
            VolumeSlider.Value = vm.Sources.Volume;

            // Empty-state hint in the recently-watched overlay — visible
            // only while the list is empty.
            void RefreshRecentEmpty() =>
                RecentEmptyLabel.IsVisible = vm.Recent.Items.Count == 0;
            RefreshRecentEmpty();
            vm.Recent.Items.CollectionChanged += (_, _) =>
            {
                RefreshRecentEmpty();
                // Re-apply the "currently playing" highlight: SaveCurrentPosition
                // (called on pause / seek) re-inserts the card at the top,
                // which destroys and recreates its visual container — so the
                // highlight on the new container needs to be set fresh.
                RefreshRecentPlayingHighlight();
            };

            // Same pattern for the search results panel — show the empty
            // hint while the user hasn't searched / no results came back.
            void RefreshSearchEmpty() =>
                SearchResultsEmptyLabel.IsVisible = vm.Results.Count == 0;
            RefreshSearchEmpty();
            vm.Results.CollectionChanged += (_, _) =>
            {
                RefreshSearchEmpty();
                // New search results need their saved-state populated
                // from MyList so the chip renders the right glyph.
                RefreshAllIsInMyListFromMyList();
            };

            // Sync poster-chip state once on load and again whenever the
            // saved list changes (so chips on Recent / Search update
            // even if they're not the surface that triggered the toggle).
            RefreshAllIsInMyListFromMyList();
            vm.MyList.Items.CollectionChanged += (_, _) =>
            {
                RefreshAllIsInMyListFromMyList();
                if (MyListEmptyLabel is not null)
                    MyListEmptyLabel.IsVisible = vm.MyList.Items.Count == 0;
            };

            switch (vm.Sources.Theme)
            {
                case "Dracula": DraculaThemeRadio.IsChecked = true; break;
                case "Netflix": NetflixThemeRadio.IsChecked = true; break;
                case "PrimeVideo": PrimeVideoThemeRadio.IsChecked = true; break;
                case "DisneyPlus": DisneyPlusThemeRadio.IsChecked = true; break;
                case "Catppuccin": CatppuccinThemeRadio.IsChecked = true; break;
                case "LightMint": LightMintThemeRadio.IsChecked = true; break;
                case "LightApricot": LightApricotThemeRadio.IsChecked = true; break;
                // Includes "LightLavender" and any legacy / unrecognized
                // value (System / Light / Dark from previous saves).
                default: LightLavenderThemeRadio.IsChecked = true; break;
            }
        }
    }

    private void OnPlayRequested(StreamResult s, VideoResult source, long startPosMs)
    {
        UI(() =>
        {
            // Save the movie we're leaving before overwriting _currentVideoResult.
            SaveCurrentPosition();

            // Clear the previous movie's last frame so the loading window
            // (full-size and PiP) shows a clean black surface instead of
            // the old video. The new bitmap is assigned by VLC's
            // VideoFormat callback once the new media starts decoding.
            VideoImage.Source = null;
            if (PipImage is not null) PipImage.Source = null;

            _currentVideoResult = source;
            _pendingSeekMs = startPosMs;
            // Real playback has begun — drop the optimistic-highlight URL
            // so subsequent updates flow through _currentVideoResult.
            _pendingPlayingPageUrl = null;
            // Insert into Recently watched the moment playback starts so
            // the card shows up right away (instead of only after pause /
            // close, which is when SaveCurrentPosition runs). The entry
            // floats to the top and gets a real position/length on the
            // next save.
            if (DataContext is MainWindowViewModel vmAdd)
                vmAdd.Recent.UpsertAndSave(source, Math.Max(0, startPosMs), 0);

            // Highlight the now-playing card in the Recently watched panel
            // (deferred to next tick so the freshly-upserted item has had
            // a layout pass to materialise its visual).
            RefreshRecentPlayingHighlight();
            // Capture which view the user was on so Back_Click can return
            // there — playback from the Recently watched tab should *stay*
            // in that tab, not jump to Search.
            _originView = RecentlyWatchedPanel.IsVisible
                ? AppView.RecentlyWatched
                : MyListPanel.IsVisible ? AppView.MyList
                : SettingsPanel.IsVisible ? AppView.Settings : AppView.Search;
            _activeTab = _originView;
            // Starting fresh playback — make sure any previous PiP overlay
            // is closed so the video shows full size.
            ExitPipMode();
            // Mount the full-width video stage. Search bar + results panel
            // are hidden during playback regardless of origin tab.
            ShowPlaybackView(_originView);
            if (BackButton is not null) BackButton.IsVisible = true;
            if (PipButton is not null) PipButton.IsVisible = true;
            if (TopGradient is not null) TopGradient.IsVisible = true;
            if (BottomGradient is not null) BottomGradient.IsVisible = true;
            if (TransportBarContainer is not null) TransportBarContainer.IsVisible = true;
            if (VideoImage is not null) VideoImage.IsVisible = true;
            // Show the loading overlay until the player's Playing event hides it.
            // Both the full-size and PiP spinners track the same flag so the
            // user sees a spinner regardless of which mode they're in.
            SetLoadingState(true);
            // Round the video surface while a movie is playing; black
            // background so letterbox bars render correctly.
            VideoBorder.CornerRadius = new CornerRadius(4);
            VideoBorder.Background = Avalonia.Media.Brushes.Black;
            if (CurrentMovieTitle is not null)
            {
                CurrentMovieTitle.Text = source.Title;
                CurrentMovieTitle.IsVisible = true;
            }
            // Start the 3s auto-hide timer so the bar fades out if the
            // user doesn't move the mouse.
            _hideUiTimer.Stop();
            _hideUiTimer.Start();
            ResetTransport();
            _hasReceivedFrame = false;
            _playbackWatchdog.Stop();
            _playbackWatchdog.Start();
            var next = new Media(_libvlc, new Uri(s.Url));

            if (!string.IsNullOrWhiteSpace(s.HttpUserAgent))
                next.AddOption($":http-user-agent={s.HttpUserAgent}");
            if (!string.IsNullOrWhiteSpace(s.HttpReferer))
            {
                // VLC accepts either spelling depending on version
                next.AddOption($":http-referrer={s.HttpReferer}");
                next.AddOption($":http-referer={s.HttpReferer}");
            }
            System.Diagnostics.Debug.WriteLine(
                $"[Play] url={s.Url} UA={s.HttpUserAgent} Ref={s.HttpReferer}");

            if (!string.IsNullOrWhiteSpace(s.AudioUrl))
                next.AddSlave(MediaSlaveType.Audio, 4, s.AudioUrl!);

            _player.Play(next);
            _currentMedia?.Dispose();
            _currentMedia = next;
        });
    }

    private void PlayPause_Click(object? sender, RoutedEventArgs e)
    {
        // Belt-and-suspenders: if no media is loaded (e.g. after Back)
        // there's nothing to play/pause, and _player.Play() with no arg
        // could resume VLC's internally-cached last media on some
        // backends.
        if (_currentMedia is null) return;
        if (_player.State == VLCState.Playing) { _player.Pause(); return; }
        // After end-of-stream the no-arg Play() can resume from VLC's
        // internal cached position (which produced the "random X minutes
        // remaining" behaviour). Restart explicitly with the current
        // media and a zeroed pending seek so we always replay from zero.
        if (_player.State == VLCState.Ended || _player.State == VLCState.Stopped)
        {
            _pendingSeekMs = 0;
            _player.Play(_currentMedia);
            return;
        }
        _player.Play();
    }

    private void Back10_Click(object? sender, RoutedEventArgs e) =>
        SeekRelative(-10_000);

    private void Forward10_Click(object? sender, RoutedEventArgs e) =>
        SeekRelative(+10_000);

    private void OnVideoTapped(object? sender, TappedEventArgs e)
    {
        if (_currentVideoResult is null) return;
        if (IsInsideTransportBar(e.Source)) return;
        _singleTapTimer.Stop();
        _singleTapTimer.Start();
    }

    private void OnVideoDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_currentVideoResult is null) return;
        if (IsInsideTransportBar(e.Source)) return;
        _singleTapTimer.Stop();
        ToggleFullscreen();
    }

    private void OnSingleTapTick(object? sender, EventArgs e)
    {
        _singleTapTimer.Stop();
        if (_currentMedia is null) return;
        if (_player.State == VLCState.Playing) _player.Pause();
        else _player.Play();
    }

    private bool IsInsideTransportBar(object? source)
    {
        if (source is not Visual v) return false;
        Visual? cur = v;
        while (cur is not null)
        {
            if (cur == TransportBarContainer) return true;
            if (cur == BackButton) return true;
            if (cur == PipButton) return true;
            cur = cur.GetVisualParent();
        }
        return false;
    }

    private void Volume_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _player.Volume = (int)e.NewValue;
        if (_isMuted && e.NewValue > 0)
        {
            _isMuted = false;
            _player.Mute = false;
        }
        UpdateMuteIcon();

        // Keep the live popup in sync while hovering / dragging.
        if (VolumePopupText is not null)
            VolumePopupText.Text = $"{(int)e.NewValue}%";

        // Persist across restarts. Save() fires automatically via the
        // ObservableProperty PropertyChanged → Save pipeline.
        if (DataContext is MainWindowViewModel vm)
            vm.Sources.Volume = (int)e.NewValue;
    }

    private void Mute_PointerEntered(object? sender, PointerEventArgs e)
    {
        _pointerOverMute = true;
        _volumePopupHideTimer.Stop();
        ShowVolumePopup();
    }

    private void Mute_PointerExited(object? sender, PointerEventArgs e)
    {
        _pointerOverMute = false;
        _volumePopupHideTimer.Stop();
        _volumePopupHideTimer.Start();
    }

    private void VolumePopupContent_PointerEntered(object? sender, PointerEventArgs e)
    {
        _pointerOverVolumePopup = true;
        _volumePopupHideTimer.Stop();
    }

    private void VolumePopupContent_PointerExited(object? sender, PointerEventArgs e)
    {
        _pointerOverVolumePopup = false;
        _volumePopupHideTimer.Stop();
        _volumePopupHideTimer.Start();
    }

    private void ShowVolumePopup()
    {
        VolumePopupText.Text = $"{(int)VolumeSlider.Value}%";
        VolumePopup.IsOpen = true;
        // Hide the timeline row while the volume popup is on-screen so
        // nothing competes visually with the vertical slider.
        TimelineRow.IsVisible = false;
    }

    private void Mute_Click(object? sender, RoutedEventArgs e)
    {
        if (_isMuted)
        {
            _isMuted = false;
            _player.Mute = false;
            VolumeSlider.Value = _volumeBeforeMute > 0 ? _volumeBeforeMute : 100;
        }
        else if (VolumeSlider.Value == 0)
        {
            VolumeSlider.Value = 100;
        }
        else
        {
            _volumeBeforeMute = (int)VolumeSlider.Value;
            _isMuted = true;
            _player.Mute = true;
            VolumeSlider.Value = 0;
        }
        UpdateMuteIcon();
    }

    private void UpdateMuteIcon()
    {
        var muted = _isMuted || VolumeSlider.Value == 0;
        VolumeOnIcon.IsVisible = !muted;
        VolumeOffIcon.IsVisible = muted;
    }

    private void Fullscreen_Click(object? sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    // Title-bar theme button → centered modal overlay (instead of an
    // anchored Flyout). Backdrop click closes; clicks on the card are
    // suppressed via Handled so they don't bubble to the backdrop.
    private void ThemeBtn_Click(object? sender, RoutedEventArgs e)
        => ThemePickerOverlay.IsVisible = true;

    private void ThemePickerOverlay_BackdropClicked(object? sender, PointerPressedEventArgs e)
        => ThemePickerOverlay.IsVisible = false;

    private void ThemePickerCard_Pressed(object? sender, PointerPressedEventArgs e)
        => e.Handled = true;

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaxRestore_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseWindow_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void DraculaTheme_Checked(object? sender, RoutedEventArgs e)
        => SelectTheme(sender, "Dracula");

    private void NetflixTheme_Checked(object? sender, RoutedEventArgs e)
        => SelectTheme(sender, "Netflix");

    private void PrimeVideoTheme_Checked(object? sender, RoutedEventArgs e)
        => SelectTheme(sender, "PrimeVideo");

    private void DisneyPlusTheme_Checked(object? sender, RoutedEventArgs e)
        => SelectTheme(sender, "DisneyPlus");

    private void CatppuccinTheme_Checked(object? sender, RoutedEventArgs e)
        => SelectTheme(sender, "Catppuccin");

    private void LightLavenderTheme_Checked(object? sender, RoutedEventArgs e)
        => SelectTheme(sender, "LightLavender");

    private void LightMintTheme_Checked(object? sender, RoutedEventArgs e)
        => SelectTheme(sender, "LightMint");

    private void LightApricotTheme_Checked(object? sender, RoutedEventArgs e)
        => SelectTheme(sender, "LightApricot");

    private bool _syncingThemePills;

    private void SelectTheme(object? sender, string theme)
    {
        if (sender is not RadioButton { IsChecked: true }) return;
        // Re-entry guard: SyncThemePills sets IsChecked on the matching
        // pill in the *other* group, which re-fires this handler. The
        // theme is already applied — short-circuit.
        if (_syncingThemePills) return;

        App.ApplyTheme(theme);
        if (DataContext is ViewModels.MainWindowViewModel vm)
            vm.Sources.Theme = theme;
        SyncThemePills(theme);
        // Dismiss the title-bar theme picker after a selection — instant
        // visual feedback that the theme has been applied.
        if (ThemePickerOverlay is not null) ThemePickerOverlay.IsVisible = false;
    }

    // Two pill groups exist for the same set of themes (title-bar palette
    // flyout + Settings panel). Whichever the user clicks, both sets must
    // reflect the new selection: we set IsChecked on every matching pill
    // and paint accent/accent-soft imperatively (DynamicResource inside
    // Flyout content lags behind merged-dictionary swaps; setting the
    // brushes directly with the just-applied resources avoids that).
    private void SyncThemePills(string theme)
    {
        var accent = ResolveBrush("SystemAccentColorBrush", this);
        var accentSoft = ResolveBrush("AccentSoftBrush", this);

        _syncingThemePills = true;
        try
        {
            foreach (var (pill, t) in AllThemePills())
            {
                if (t == theme)
                {
                    if (pill.IsChecked != true) pill.IsChecked = true;
                    if (accent is not null) pill.BorderBrush = accent;
                    if (accentSoft is not null) pill.Background = accentSoft;
                }
                else
                {
                    if (pill.IsChecked == true) pill.IsChecked = false;
                    pill.ClearValue(Avalonia.Controls.Primitives.TemplatedControl.BorderBrushProperty);
                    pill.ClearValue(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty);
                }
            }
        }
        finally { _syncingThemePills = false; }
    }

    private IEnumerable<(RadioButton pill, string theme)> AllThemePills()
    {
        // Title-bar palette flyout group.
        yield return (LightApricotThemeRadio,    "LightApricot");
        yield return (CatppuccinThemeRadio,      "Catppuccin");
        yield return (DisneyPlusThemeRadio,      "DisneyPlus");
        yield return (DraculaThemeRadio,         "Dracula");
        yield return (LightLavenderThemeRadio,   "LightLavender");
        yield return (LightMintThemeRadio,       "LightMint");
        yield return (NetflixThemeRadio,         "Netflix");
        yield return (PrimeVideoThemeRadio,      "PrimeVideo");
        // Settings panel group.
        yield return (SettingsLightApricotRadio, "LightApricot");
        yield return (SettingsCatppuccinRadio,   "Catppuccin");
        yield return (SettingsDisneyPlusRadio,   "DisneyPlus");
        yield return (SettingsDraculaRadio,      "Dracula");
        yield return (SettingsLightLavenderRadio,"LightLavender");
        yield return (SettingsLightMintRadio,    "LightMint");
        yield return (SettingsNetflixRadio,      "Netflix");
        yield return (SettingsPrimeVideoRadio,   "PrimeVideo");
    }

    private static Avalonia.Media.IBrush? ResolveBrush(string key, Control origin)
    {
        if (origin.TryFindResource(key, origin.ActualThemeVariant, out var v)
            && v is Avalonia.Media.IBrush b)
            return b;
        if (Application.Current?.TryFindResource(key, origin.ActualThemeVariant, out var v2) == true
            && v2 is Avalonia.Media.IBrush b2)
            return b2;
        return null;
    }

    private void PlayResult_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: VideoResult vr }
            && DataContext is MainWindowViewModel vm)
        {
            // Resume from saved position if we've watched this before,
            // otherwise start from zero.
            var startMs = vm.Recent.Find(vr.PageUrl)?.PositionMs ?? 0;
            vm.SelectedResult = vr;
            _ = vm.PlayResultAsync(vr, startMs);
        }
    }

    private void RecentWatch_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecentWatch rw }
            && DataContext is MainWindowViewModel vm)
        {
            // If the user clicked the card for the movie that's already
            // playing (in PiP mode while they're browsing Recently watched),
            // don't restart it — just bring the video back to full size.
            if (TryRestorePipForPageUrl(rw.PageUrl, sender, e)) return;
            // Optimistic highlight: light up the clicked card with
            // "Currently playing" right away while the stream URL is
            // being extracted in the background.
            _pendingPlayingPageUrl = string.IsNullOrEmpty(rw.PageUrl) ? null : rw.PageUrl;
            ApplyRecentPlayingHighlightNow();
            _ = vm.PlayRecentAsync(rw);
        }
        // Stop the routed Click from bubbling to the parent recent-card
        // when the inline play chip is the source — without this the
        // card's own RecentWatch_Click would fire a second time.
        e.Handled = true;
    }

    // Click on a card in the My-List panel — same flow as the
    // recently-watched click, except we have a MyListEntry to convert.
    // Picks the higher of the entry's own saved position and any
    // matching Recently-watched entry's position so playback always
    // resumes from the latest known offset, even if the user added the
    // movie to My-list AFTER they started watching it.
    private void MyListEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MyListEntry entry }
            || DataContext is not MainWindowViewModel vm) return;
        if (TryRestorePipForPageUrl(entry.PageUrl, sender, e)) return;
        _pendingPlayingPageUrl = string.IsNullOrEmpty(entry.PageUrl) ? null : entry.PageUrl;
        ApplyRecentPlayingHighlightNow();
        var resumeMs = Math.Max(entry.PositionMs, vm.Recent.Find(entry.PageUrl)?.PositionMs ?? 0);
        _ = vm.PlayResultAsync(entry.ToVideoResult(), resumeMs);
        e.Handled = true;
    }

    // Shared "is this card already in PiP playback?" branch used by both
    // RecentWatch_Click and MyListEntry_Click. Returns true if PiP was
    // restored (caller should bail out instead of restarting playback).
    // URL match is case-insensitive and ignores a trailing slash so
    // trivial variants between the saved entry and the active video
    // result still resolve to the same item.
    private bool TryRestorePipForPageUrl(string? pageUrl, object? sender, RoutedEventArgs e)
    {
        if (!_isPipMode
            || _currentVideoResult is null
            || !PageUrlEquals(_currentVideoResult.PageUrl, pageUrl)) return false;
        PipRestore_Click(sender, e);
        e.Handled = true;
        return true;
    }

    private static bool PageUrlEquals(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    // The chip on a recently-watched / search-result card — toggles
    // membership in MyList and syncs IsInMyList across every visible
    // surface so all chips referring to the same PageUrl flip together.
    private void ToggleMyList_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || DataContext is not MainWindowViewModel vm) return;
        VideoResult? source = btn.Tag switch
        {
            VideoResult v => v,
            RecentWatch rw => rw.ToVideoResult(),
            MyListEntry m => m.ToVideoResult(),
            _ => null,
        };
        if (source is null || string.IsNullOrEmpty(source.PageUrl)) return;
        // If this movie is already in Recently watched, seed the new
        // My-list entry with the saved position so its progress bar (and
        // resume-from-saved-time) work immediately.
        var existingProgress = vm.Recent.Find(source.PageUrl);
        var nowSaved = vm.MyList.Toggle(
            source,
            existingProgress?.PositionMs ?? 0,
            existingProgress?.LengthMs ?? 0);
        SyncIsInMyList(source.PageUrl, nowSaved);
        e.Handled = true;
    }

    // The chip in the My-list panel — same as ToggleMyList_Click but
    // always removes (the panel only shows saved items).
    private void MyListRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MyListEntry entry }
            || DataContext is not MainWindowViewModel vm) return;
        if (string.IsNullOrEmpty(entry.PageUrl)) return;
        vm.MyList.Remove(entry.PageUrl);
        SyncIsInMyList(entry.PageUrl, false);
        if (MyListEmptyLabel is not null)
            MyListEmptyLabel.IsVisible = vm.MyList.Items.Count == 0;
        e.Handled = true;
    }

    // Mirror MyList membership onto Recent + search Results so all
    // chips referring to the same movie reflect the new state.
    private void SyncIsInMyList(string pageUrl, bool isSaved)
    {
        if (DataContext is not MainWindowViewModel vm || string.IsNullOrEmpty(pageUrl)) return;
        foreach (var rw in vm.Recent.Items)
            if (rw.PageUrl == pageUrl) rw.IsInMyList = isSaved;
        foreach (var vr in vm.Results)
            if (vr.PageUrl == pageUrl) vr.IsInMyList = isSaved;
    }

    // Bulk-sync after load — runs once OnDataContextChanged, and again
    // when MyList.Items changes, so existing Recent + Results entries
    // pick up the saved-state on app start.
    private void RefreshAllIsInMyListFromMyList()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        foreach (var rw in vm.Recent.Items)
            rw.IsInMyList = vm.MyList.Contains(rw.PageUrl);
        foreach (var vr in vm.Results)
            vr.IsInMyList = vm.MyList.Contains(vr.PageUrl);
    }

    // ── Sidebar nav handlers ──────────────────────────────────────────
    // Three views share the main area: Search (default — search row +
    // results panel + video stage), Recently watched (just the video
    // stage with the recently-watched overlay), and Settings (embedded
    // settings panel replaces the content grid). Helpers below toggle
    // visibility.

    private void NavSearch_Click(object? sender, RoutedEventArgs e)
    {
        _activeTab = AppView.Search;
        UpdateActiveNavTab();
        if (_currentVideoResult is not null) EnterPipMode();
        ShowSearchView();
        ResetStatusForActiveTab();
    }

    private void NavRecent_Click(object? sender, RoutedEventArgs e)
    {
        _activeTab = AppView.RecentlyWatched;
        UpdateActiveNavTab();
        if (_currentVideoResult is not null) EnterPipMode();
        ShowRecentlyWatchedView();
        ResetStatusForActiveTab();
    }

    private void NavMyList_Click(object? sender, RoutedEventArgs e)
    {
        _activeTab = AppView.MyList;
        UpdateActiveNavTab();
        if (_currentVideoResult is not null) EnterPipMode();
        ShowMyListView();
        ResetStatusForActiveTab();
    }

    private void NavSettings_Click(object? sender, RoutedEventArgs e)
    {
        _activeTab = AppView.Settings;
        UpdateActiveNavTab();
        if (_currentVideoResult is not null) EnterPipMode();
        ShowSettingsView();
        ResetStatusForActiveTab();
    }

    // Picks the right per-tab default for the bottom status bar. On the
    // Search tab it surfaces the last search summary if there were
    // results; otherwise the prompt to enter a title.
    private void ResetStatusForActiveTab()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.Status = _activeTab switch
        {
            AppView.RecentlyWatched => "Click a movie to continue watching.",
            AppView.MyList => "Click a movie to watch from your list.",
            AppView.Settings => "Settings save automatically.",
            _ => vm.Results.Count > 0 && !string.IsNullOrEmpty(vm.LastSearchTitle)
                ? $"Found {vm.Results.Count} search results for '{vm.LastSearchTitle}'."
                : "Enter a title and click Search.",
        };
    }

    // Toggles the .active CSS-style class on the four sidebar nav
    // buttons so the icon stroke flips to the accent color on the
    // currently-active tab — visual cue for "where am I".
    private void UpdateActiveNavTab()
    {
        SetActive(NavSearchBtn, _activeTab == AppView.Search);
        SetActive(NavRecentBtn, _activeTab == AppView.RecentlyWatched);
        SetActive(NavMyListBtn, _activeTab == AppView.MyList);
        SetActive(SettingsBtn, _activeTab == AppView.Settings);
    }

    private static void SetActive(Control c, bool active)
    {
        if (active)
        {
            if (!c.Classes.Contains("active")) c.Classes.Add("active");
        }
        else
        {
            c.Classes.Remove("active");
        }
    }

    private void ShowSearchView()
    {
        // Search tab without active playback: search bar at the top,
        // ResultsPanel takes the full content width. The video stage
        // (Column 1) is collapsed entirely — it only ever appears
        // during full-screen playback (ShowPlaybackView).
        SearchBar.IsVisible = true;
        ContentGrid.IsVisible = true;
        ContentGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        ContentGrid.ColumnDefinitions[1].Width = new GridLength(0);
        ResultsPanel.IsVisible = true;
        RecentlyWatchedPanel.IsVisible = false;
        MyListPanel.IsVisible = false;
        SettingsPanel.IsVisible = false;
    }

    private void ShowRecentlyWatchedView()
    {
        // Hide everything else; the recently-watched card panel takes
        // over the main content area entirely.
        SearchBar.IsVisible = false;
        ContentGrid.IsVisible = false;
        RecentlyWatchedPanel.IsVisible = true;
        MyListPanel.IsVisible = false;
        SettingsPanel.IsVisible = false;
        // Re-apply playing highlight in case cards just materialised.
        RefreshRecentPlayingHighlight();
    }

    private void ShowMyListView()
    {
        // Saved-for-later card grid. Same shell as RecentlyWatchedPanel
        // but bound to MyList.Items.
        SearchBar.IsVisible = false;
        ContentGrid.IsVisible = false;
        RecentlyWatchedPanel.IsVisible = false;
        MyListPanel.IsVisible = true;
        SettingsPanel.IsVisible = false;
        if (DataContext is MainWindowViewModel vm)
            MyListEmptyLabel.IsVisible = vm.MyList.Items.Count == 0;
    }

    private void ShowSettingsView()
    {
        // Replace the entire content grid (search + results + video)
        // with the embedded Settings panel.
        SearchBar.IsVisible = false;
        ContentGrid.IsVisible = false;
        RecentlyWatchedPanel.IsVisible = false;
        MyListPanel.IsVisible = false;
        SettingsPanel.IsVisible = true;
    }

    // Layout for the video stage during playback. Always full-width video
    // (search bar + results panel hidden), regardless of which tab the
    // user came from — playback feels uniform across tabs.
    private void ShowPlaybackView(AppView origin)
    {
        _ = origin;
        ContentGrid.IsVisible = true;
        RecentlyWatchedPanel.IsVisible = false;
        MyListPanel.IsVisible = false;
        SettingsPanel.IsVisible = false;
        SearchBar.IsVisible = false;
        ContentGrid.ColumnDefinitions[0].Width = new GridLength(0);
        ContentGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        ResultsPanel.IsVisible = false;
    }

    // ── Picture-in-picture ────────────────────────────────────────────
    // While a video is playing and the user navigates to a tab without
    // a video stage (or to a different layout), the video shrinks into
    // PipFrame — a draggable mini-player anchored bottom-right by default.
    // The full-size video chrome (gradients, back arrow, title, transport
    // bar) is hidden during PiP; the mini-transport inside PipFrame mirrors
    // the player state via SetPlayPauseIcons / SetSliderFromPlayer.

    private void EnterPipMode()
    {
        if (_isPipMode || _currentVideoResult is null) return;
        // Drop out of fullscreen first so the PiP frame floats over a
        // normal-windowed app instead of leaving the OS in fullscreen
        // with a tiny mini-player on top of an otherwise blank screen.
        // Covers every entry path: PiP toolbar button + sidebar tab
        // clicks that auto-PiP a playing video.
        if (WindowState == WindowState.FullScreen) ToggleFullscreen();
        _isPipMode = true;
        if (DataContext is MainWindowViewModel vmPip) vmPip.IsPipActive = true;

        // Hide the full-size video chrome — only the PiP frame is visible.
        TopGradient.IsVisible = false;
        BottomGradient.IsVisible = false;
        BackButton.IsVisible = false;
        PipButton.IsVisible = false;
        CurrentMovieTitle.IsVisible = false;
        TransportBarContainer.IsVisible = false;
        // VideoBorder is inside ContentGrid. ContentGrid is hidden by the
        // active view (Recently watched / Settings), so the underlying
        // video stage doesn't show through.

        // Always start PiP at the default minimum size — don't carry the
        // previous session's resize across opens.
        PipFrame.Width = PipWidth;
        PipFrame.Height = PipHeight;

        // Position bottom-right with edge padding. Defer to next layout
        // pass if PipHost hasn't been measured yet.
        PipFrame.IsVisible = true;
        PositionPipBottomRight();

        PulsePipUi();
    }

    private void ExitPipMode()
    {
        if (!_isPipMode) return;
        _isPipMode = false;
        if (DataContext is MainWindowViewModel vmPip) vmPip.IsPipActive = false;
        PipFrame.IsVisible = false;
        PipRestoreBtn.IsVisible = false;
        PipCloseBtn.IsVisible = false;
        PipTransport.IsVisible = false;
        PipTopGradient.IsVisible = false;
        PipBottomGradient.IsVisible = false;
        SetPipResizeGripsVisible(false);
        _pipHideUiTimer?.Stop();

        // Bring the full-size chrome back if a video is still loaded.
        if (_currentVideoResult is not null)
        {
            TopGradient.IsVisible = true;
            BottomGradient.IsVisible = true;
            BackButton.IsVisible = true;
            PipButton.IsVisible = true;
            CurrentMovieTitle.IsVisible = true;
            TransportBarContainer.IsVisible = true;
        }
    }

    private void PositionPipBottomRight()
    {
        var hostW = PipHost.Bounds.Width;
        var hostH = PipHost.Bounds.Height;
        if (hostW <= 0 || hostH <= 0)
        {
            // Layout pass hasn't happened yet — try again after layout.
            Dispatcher.UIThread.Post(PositionPipBottomRight, DispatcherPriority.Loaded);
            return;
        }
        var left = Math.Max(PipPaddingFromEdges, hostW - PipWidth - PipPaddingFromEdges);
        var top = Math.Max(PipPaddingFromEdges, hostH - PipHeight - PipPaddingFromEdges);
        PipFrame.Margin = new Thickness(left, top, 0, 0);
    }

    // PipHost resized (window grew, shrank, sidebar tab changed, etc.) —
    // reposition PipFrame so it keeps the SAME distance from the
    // bottom-right edge it had before the resize. Without this, the
    // frame stays anchored to its old absolute top-left margin: shrink
    // the window and the frame clips off the edge; grow it and the
    // frame drifts away from the corner.
    private void OnPipHostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_isPipMode) return;
        if (e.PreviousSize.Width <= 0 || e.PreviousSize.Height <= 0) return;
        var frameW = PipFrame.Bounds.Width;
        var frameH = PipFrame.Bounds.Height;
        if (frameW <= 0 || frameH <= 0) return;

        // Distance from the right / bottom edges before the resize.
        var distFromRight = e.PreviousSize.Width - PipFrame.Margin.Left - frameW;
        var distFromBottom = e.PreviousSize.Height - PipFrame.Margin.Top - frameH;

        var newLeft = e.NewSize.Width - distFromRight - frameW;
        var newTop = e.NewSize.Height - distFromBottom - frameH;

        // Clamp so the frame stays fully within bounds with the standard
        // edge padding — protects against the new size being smaller
        // than the previous distance-from-corner.
        var maxLeft = Math.Max(PipPaddingFromEdges,
            e.NewSize.Width - frameW - PipPaddingFromEdges);
        var maxTop = Math.Max(PipPaddingFromEdges,
            e.NewSize.Height - frameH - PipPaddingFromEdges);
        newLeft = Math.Clamp(newLeft, PipPaddingFromEdges, maxLeft);
        newTop = Math.Clamp(newTop, PipPaddingFromEdges, maxTop);

        PipFrame.Margin = new Thickness(newLeft, newTop, 0, 0);
    }

    // Press on the scrollbar's track lane → jump the scrollbar to that
    // position, capture the pointer, and keep updating Value as the
    // user drags so they can scrub continuously without releasing.
    // Track has no DecreaseButton/IncreaseButton in our custom template,
    // so the lane Border is the only thing receiving the press. While
    // the drag is active we mark the Thumb with a "scrolling" class so
    // it stays at full opacity even when the cursor leaves the
    // scrollbar — same feel as a video timeline scrub.
    private ScrollBar? _trackDragScrollBar;
    private Track? _trackDragTrack;
    private Thumb? _trackDragThumb;
    // Distance between the initial click coordinate and the thumb's
    // top edge (in track-local coords). Drag updates compute the new
    // thumb top as cursor − this offset, so the drag tracks 1:1 just
    // like Avalonia's native thumb drag.
    private double _trackDragOffsetFromThumbTop;

    private void OnScrollBarTrackClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control source) return;
        if (!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) return;
        var scrollBar = source.FindAncestorOfType<ScrollBar>();
        if (scrollBar is null) return;

        Track? track = null;
        Thumb? thumb = null;
        foreach (var d in scrollBar.GetVisualDescendants())
        {
            if (track is null && d is Track t) track = t;
            else if (thumb is null && d is Thumb th) thumb = th;
            if (track is not null && thumb is not null) break;
        }
        if (track is null || thumb is null) return;

        var isVertical = scrollBar.Orientation == Avalonia.Layout.Orientation.Vertical;
        var trackSize = isVertical ? track.Bounds.Height : track.Bounds.Width;
        var thumbSize = isVertical ? thumb.Bounds.Height : thumb.Bounds.Width;
        var usable = trackSize - thumbSize;
        if (usable <= 0) return;

        var range = scrollBar.Maximum - scrollBar.Minimum;
        var posInTrack = e.GetPosition(track);
        var coord = isVertical ? posInTrack.Y : posInTrack.X;

        // Where is the thumb sitting right now (top edge in track-local
        // coords)? We need this to decide whether the click landed
        // within the thumb's vertical/horizontal extent.
        var currentFraction = range > 0
            ? (scrollBar.Value - scrollBar.Minimum) / range
            : 0;
        var thumbTop = currentFraction * usable;
        var thumbBottom = thumbTop + thumbSize;

        // Click on (or visually next to) the thumb at the same Y/X →
        // do NOT jump. Mirrors native thumb-drag behavior: the thumb
        // stays put and starts a drag from its current position.
        // Click outside the thumb's current extent → jump so the
        // thumb's center lands on the click, then drag from there.
        if (coord < thumbTop || coord > thumbBottom)
        {
            var desiredTop = coord - thumbSize / 2.0;
            var newFraction = Math.Clamp(desiredTop / usable, 0.0, 1.0);
            scrollBar.Value = scrollBar.Minimum + newFraction * range;
            thumbTop = newFraction * usable;
        }

        _trackDragOffsetFromThumbTop = coord - thumbTop;
        _trackDragScrollBar = scrollBar;
        _trackDragTrack = track;
        _trackDragThumb = thumb;
        thumb.Classes.Add("scrolling");
        e.Pointer.Capture(source);
        e.Handled = true;
    }

    private void OnScrollBarTrackPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_trackDragScrollBar is null
            || _trackDragTrack is null
            || _trackDragThumb is null) return;

        var isVertical = _trackDragScrollBar.Orientation == Avalonia.Layout.Orientation.Vertical;
        var trackSize = isVertical ? _trackDragTrack.Bounds.Height : _trackDragTrack.Bounds.Width;
        var thumbSize = isVertical ? _trackDragThumb.Bounds.Height : _trackDragThumb.Bounds.Width;
        var usable = trackSize - thumbSize;
        if (usable <= 0) return;

        var posInTrack = e.GetPosition(_trackDragTrack);
        var coord = isVertical ? posInTrack.Y : posInTrack.X;

        var newThumbTop = coord - _trackDragOffsetFromThumbTop;
        var fraction = Math.Clamp(newThumbTop / usable, 0.0, 1.0);
        var range = _trackDragScrollBar.Maximum - _trackDragScrollBar.Minimum;
        _trackDragScrollBar.Value = _trackDragScrollBar.Minimum + fraction * range;
        e.Handled = true;
    }

    private void OnScrollBarTrackPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_trackDragScrollBar is null) return;
        _trackDragThumb?.Classes.Remove("scrolling");
        _trackDragThumb = null;
        _trackDragTrack = null;
        _trackDragScrollBar = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // Single PiP toggle wired to the transport-bar ToggleButton. If
    // playback is in PiP, restore the full-size player; otherwise drop
    // into PiP and show whichever tab is currently active behind it.
    private void TogglePip_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentVideoResult is null) return;
        if (_isPipMode)
        {
            // Exit PiP: restore the playback layout that was up before
            // (or the active tab if Settings doesn't fit).
            var target = _activeTab == AppView.Settings ? _originView : _activeTab;
            if (target == AppView.Settings) target = AppView.Search;
            ExitPipMode();
            ShowPlaybackView(target);
            return;
        }
        EnterPipMode();
        switch (_activeTab)
        {
            case AppView.RecentlyWatched: ShowRecentlyWatchedView(); break;
            case AppView.MyList: ShowMyListView(); break;
            case AppView.Settings: ShowSettingsView(); break;
            default: ShowSearchView(); break;
        }
    }

    private void PipRestore_Click(object? sender, RoutedEventArgs e)
    {
        // Pick the layout to restore into. Settings has no video place, so
        // fall back to where the video originally started; otherwise the
        // currently-active tab wins (Search or Recently watched).
        var target = _activeTab == AppView.Settings ? _originView : _activeTab;
        if (target == AppView.Settings) target = AppView.Search;
        ExitPipMode();
        ShowPlaybackView(target);
    }

    private void PipPlayPause_Click(object? sender, RoutedEventArgs e)
        => PlayPause_Click(sender, e);

    // Tear down playback entirely from the PiP — same effect as the
    // full-size BackButton, but anchored to the user's *current* tab,
    // not wherever the video originally started. Without this, closing
    // PiP from Recently watched while the video had started in Search
    // would briefly switch the layout back to Search → visible flicker.
    private void PipClose_Click(object? sender, RoutedEventArgs e)
    {
        // Stay on the tab the user is currently viewing.
        _originView = _activeTab;
        ExitPipMode();
        Dispatcher.UIThread.Post(() => Back_Click(sender, e));
    }


    // ── PiP drag handling ────────────────────────────────────────────
    // PointerPressed on empty PiP space starts a drag (clicks on the
    // restore button / play / pause / slider must NOT start a drag, so
    // we walk the source's ancestors and bail on any interactive child).

    private void PipFrame_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual v && IsInsidePipControl(v)) return;
        if (!e.GetCurrentPoint(PipFrame).Properties.IsLeftButtonPressed) return;
        _pipDragging = true;
        _pipDragStart = e.GetPosition(PipHost);
        _pipMarginAtDragStart = PipFrame.Margin;
        e.Pointer.Capture(PipFrame);
    }

    private void PipFrame_PointerMoved(object? sender, PointerEventArgs e)
    {
        PulsePipUi();
        if (!_pipDragging) return;

        var current = e.GetPosition(PipHost);
        var dx = current.X - _pipDragStart.X;
        var dy = current.Y - _pipDragStart.Y;

        var hostW = PipHost.Bounds.Width;
        var hostH = PipHost.Bounds.Height;
        var maxLeft = Math.Max(PipPaddingFromEdges,
            hostW - PipFrame.Bounds.Width - PipPaddingFromEdges);
        var maxTop = Math.Max(PipPaddingFromEdges,
            hostH - PipFrame.Bounds.Height - PipPaddingFromEdges);

        var newLeft = Math.Clamp(_pipMarginAtDragStart.Left + dx, PipPaddingFromEdges, maxLeft);
        var newTop = Math.Clamp(_pipMarginAtDragStart.Top + dy, PipPaddingFromEdges, maxTop);
        PipFrame.Margin = new Thickness(newLeft, newTop, 0, 0);
    }

    private void PipFrame_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pipDragging) return;
        _pipDragging = false;
        e.Pointer.Capture(null);
    }

    // ── PiP resize ───────────────────────────────────────────────────
    // Four grips (one per corner) share these handlers. Each grip's Tag
    // ("TL"/"TR"/"BL"/"BR") identifies which corner the user grabbed; the
    // OPPOSITE corner stays anchored, so the frame grows / shrinks toward
    // the dragged edge. 16:9 aspect ratio is preserved; min size is the
    // default 320×180; max is whatever fits inside PipHost from the
    // anchored edge minus the standard edge padding.
    private void PipResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border grip) return;
        if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
        _pipResizing = true;
        _pipResizeStart = e.GetPosition(PipHost);
        _pipSizeAtResizeStart = new Size(PipFrame.Bounds.Width, PipFrame.Bounds.Height);
        _pipMarginAtResizeStart = PipFrame.Margin;
        _pipResizeCorner = (grip.Tag as string) ?? "BR";
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void PipResizeGrip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pipResizing) return;
        var current = e.GetPosition(PipHost);
        var dx = current.X - _pipResizeStart.X;
        var dy = current.Y - _pipResizeStart.Y;

        // Per-axis sign by grip tag. +1 = grow with positive delta (right
        // / bottom side), -1 = grow with negative delta (left / top side
        // — opposite edge stays anchored), 0 = axis is locked (edge grip
        // that doesn't touch this axis). Width and height resize
        // independently: corners change both axes, edges only one,
        // letting the user squash or stretch the PiP frame freely.
        int xSign = _pipResizeCorner switch
        {
            "TR" or "BR" or "R" => 1,
            "TL" or "BL" or "L" => -1,
            _ => 0,
        };
        int ySign = _pipResizeCorner switch
        {
            "BR" or "BL" or "B" => 1,
            "TR" or "TL" or "T" => -1,
            _ => 0,
        };

        var newWidth = _pipSizeAtResizeStart.Width + (xSign == 0 ? 0 : dx * xSign);
        var newHeight = _pipSizeAtResizeStart.Height + (ySign == 0 ? 0 : dy * ySign);

        // Max bounds depend on which edge stays fixed. Right-side grip ⇒
        // left edge is anchored, so the frame can grow to the right edge
        // of PipHost. Left-side grip ⇒ right edge is anchored. Locked
        // axis keeps the starting size as both min and max.
        double maxWidth = xSign switch
        {
            1 => Math.Max(PipWidth,
                PipHost.Bounds.Width - _pipMarginAtResizeStart.Left - PipPaddingFromEdges),
            -1 => Math.Max(PipWidth,
                _pipMarginAtResizeStart.Left + _pipSizeAtResizeStart.Width - PipPaddingFromEdges),
            _ => _pipSizeAtResizeStart.Width,
        };
        double maxHeight = ySign switch
        {
            1 => Math.Max(PipHeight,
                PipHost.Bounds.Height - _pipMarginAtResizeStart.Top - PipPaddingFromEdges),
            -1 => Math.Max(PipHeight,
                _pipMarginAtResizeStart.Top + _pipSizeAtResizeStart.Height - PipPaddingFromEdges),
            _ => _pipSizeAtResizeStart.Height,
        };

        var minWidth = xSign == 0 ? _pipSizeAtResizeStart.Width : PipWidth;
        var minHeight = ySign == 0 ? _pipSizeAtResizeStart.Height : PipHeight;
        newWidth = Math.Clamp(newWidth, minWidth, maxWidth);
        newHeight = Math.Clamp(newHeight, minHeight, maxHeight);

        // Anchor the opposite edge: Margin.Left/Top only changes for grips
        // on the left / top side (sign == -1).
        var newLeft = _pipMarginAtResizeStart.Left;
        var newTop = _pipMarginAtResizeStart.Top;
        if (xSign == -1)
        {
            var rightEdge = _pipMarginAtResizeStart.Left + _pipSizeAtResizeStart.Width;
            newLeft = rightEdge - newWidth;
        }
        if (ySign == -1)
        {
            var bottomEdge = _pipMarginAtResizeStart.Top + _pipSizeAtResizeStart.Height;
            newTop = bottomEdge - newHeight;
        }

        PipFrame.Width = newWidth;
        PipFrame.Height = newHeight;
        PipFrame.Margin = new Thickness(newLeft, newTop, 0, 0);
        PulsePipUi();
        e.Handled = true;
    }

    private void PipResizeGrip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pipResizing) return;
        _pipResizing = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void PipFrame_PointerEntered(object? sender, PointerEventArgs e)
        => PulsePipUi();

    private void PipFrame_PointerExited(object? sender, PointerEventArgs e)
    {
        // Don't hide chrome mid-drag — the grip would disappear under the
        // cursor and the resize would silently end.
        if (_pipResizing) return;
        // Hide controls immediately when the pointer leaves so the PiP
        // frame goes back to a clean video-only look without waiting for
        // the 3-second timeout.
        _pipHideUiTimer?.Stop();
        PipRestoreBtn.IsVisible = false;
        PipCloseBtn.IsVisible = false;
        PipTransport.IsVisible = false;
        PipTopGradient.IsVisible = false;
        PipBottomGradient.IsVisible = false;
        SetPipResizeGripsVisible(false);
    }

    private bool IsInsidePipControl(Visual origin)
    {
        Visual? cur = origin;
        while (cur is not null && cur != PipFrame)
        {
            if (cur is Button || cur is Slider) return true;
            if (cur == PipResizeGripTL || cur == PipResizeGripTR
                || cur == PipResizeGripBL || cur == PipResizeGripBR) return true;
            cur = cur.GetVisualParent();
        }
        return false;
    }

    private void SetPipResizeGripsVisible(bool visible)
    {
        PipResizeGripTL.IsVisible = visible;
        PipResizeGripTR.IsVisible = visible;
        PipResizeGripBL.IsVisible = visible;
        PipResizeGripBR.IsVisible = visible;
        PipResizeGripT.IsVisible = visible;
        PipResizeGripB.IsVisible = visible;
        PipResizeGripL.IsVisible = visible;
        PipResizeGripR.IsVisible = visible;
    }

    // Drives both loading spinners (full-size + PiP) from a single call.
    // Showing both, regardless of whether the user is currently in PiP,
    // means dropping into the mini player while the stream is still
    // buffering still surfaces a spinner over the black PipImage.
    private void SetLoadingState(bool loading)
    {
        if (LoadingOverlay is not null) LoadingOverlay.IsVisible = loading;
        if (PipLoadingOverlay is not null) PipLoadingOverlay.IsVisible = loading;
        // Gate the spinner animation on a class so it only runs while
        // loading. Avalonia's IterationCount="INFINITE" keeps
        // invalidating the target even when the parent is hidden, which
        // leaks a faint ghost square into the video area; toggling the
        // class detaches the animation outright when not loading.
        if (LoadingSpinner is not null) LoadingSpinner.Classes.Set("spinning", loading);
        if (PipLoadingSpinner is not null) PipLoadingSpinner.Classes.Set("spinning", loading);
    }

    // Auto-hide UI: show the restore button + mini transport while there's
    // pointer activity, hide them ~3s after the pointer goes idle.
    private void PulsePipUi()
    {
        if (!_isPipMode) return;
        PipRestoreBtn.IsVisible = true;
        PipCloseBtn.IsVisible = true;
        PipTransport.IsVisible = true;
        PipTopGradient.IsVisible = true;
        PipBottomGradient.IsVisible = true;
        SetPipResizeGripsVisible(true);
        _pipHideUiTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _pipHideUiTimer.Tick -= OnPipHideUiTick;
        _pipHideUiTimer.Tick += OnPipHideUiTick;
        _pipHideUiTimer.Stop();
        _pipHideUiTimer.Start();
    }

    private void OnPipHideUiTick(object? sender, EventArgs e)
    {
        _pipHideUiTimer?.Stop();
        if (!_isPipMode) return;
        PipRestoreBtn.IsVisible = false;
        PipCloseBtn.IsVisible = false;
        PipTransport.IsVisible = false;
        PipTopGradient.IsVisible = false;
        PipBottomGradient.IsVisible = false;
        SetPipResizeGripsVisible(false);
    }

    // Clicks on the title-strip text area should NOT start playback —
    // playback is reserved for the poster image and the inline play
    // button. Marking PointerPressed handled here prevents the parent
    // recent-card Button's press tracking, so it never raises Click on
    // release. Inner action chips raise their own Click on release as
    // usual. Hover events (PointerEntered/Exited on the recent-card)
    // are unaffected, so the strip still triggers hover state.
    private void TitleStrip_PointerPressed(object? sender, PointerPressedEventArgs e)
        => e.Handled = true;

    private void RecentCard_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Button btn) return;
        ApplyRecentCardVisuals(btn, hovered: true);
    }

    private void RecentCard_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Button btn) return;
        // Don't strip the highlight on the playing card — its hover state
        // is "always on" so users can spot it at a glance without hovering.
        ApplyRecentCardVisuals(btn, hovered: false);
    }

    // Shared visual update for a recent card. The "currently-playing"
    // card keeps its accent border and shows the inline badge regardless
    // of pointer position; other cards follow normal hover-on / hover-off
    // rules for the accent border alone.
    private void ApplyRecentCardVisuals(Button card, bool hovered)
    {
        var isPlaying = IsRecentCardPlaying(card);
        var show = hovered || isPlaying;

        if (FindBorderByClass(card, "poster") is Border poster)
            poster.BorderBrush = show
                ? ResolveAccentBrush(card)
                : Avalonia.Media.Brushes.Transparent;

        // Scrim darkens the poster image on hover OR when playing —
        // gives every hovered card the same "active" look as the
        // currently-playing one.
        if (FindBorderByClass(card, "playing-scrim") is Border scrim)
            scrim.IsVisible = show;

        // Badge shows in both states: "Currently playing" stays visible
        // while the card represents the active video; on hover (when
        // not playing) it flips to "Continue playing" so the user knows
        // a click will resume from the saved position.
        if (FindByClass(card, "playing-badge") is TextBlock badge)
        {
            badge.IsVisible = show;
            badge.Text = isPlaying ? "Currently playing" : "Continue playing";
        }
    }

    // True if this card represents the movie currently being played.
    // Recognises both Recently watched (RecentWatch) and My list
    // (MyListEntry) tags; pending click takes precedence so a freshly
    // tapped card lights up immediately, even before the player has
    // actually started.
    private bool IsRecentCardPlaying(Button card)
    {
        var pageUrl = card.Tag switch
        {
            RecentWatch rw => rw.PageUrl,
            MyListEntry me => me.PageUrl,
            _ => null,
        };
        if (string.IsNullOrEmpty(pageUrl)) return false;
        var activeUrl = _pendingPlayingPageUrl ?? _currentVideoResult?.PageUrl;
        return activeUrl is not null && pageUrl == activeUrl;
    }

    // Sweeps every materialised recent-card across BOTH the Recently
    // watched and My list panels and re-applies its visual state
    // synchronously. Use this when the cards are already in the visual
    // tree (e.g. tearing down the highlight on close).
    private void ApplyRecentPlayingHighlightNow()
    {
        SweepRecentCards(RecentlyWatchedPanel);
        SweepRecentCards(MyListPanel);
    }

    private void SweepRecentCards(Control? root)
    {
        if (root is null) return;
        foreach (var d in root.GetVisualDescendants())
        {
            if (d is Button btn && btn.Classes.Contains("recent-card"))
                ApplyRecentCardVisuals(btn, hovered: false);
        }
    }

    // Deferred wrapper — needed when the call follows a CollectionChanged
    // (a new container needs a layout pass to materialise before we can
    // find it in the visual tree).
    private void RefreshRecentPlayingHighlight()
        => Dispatcher.UIThread.Post(ApplyRecentPlayingHighlightNow);

    private static Border? FindBorderByClass(Control root, string className)
    {
        foreach (var d in root.GetVisualDescendants())
            if (d is Border b && b.Classes.Contains(className)) return b;
        return null;
    }

    private static TextBlock? FindFirstTextBlock(Visual root)
    {
        foreach (var d in root.GetVisualDescendants())
            if (d is TextBlock tb) return tb;
        return null;
    }

    private static TextBlock? FindTextBlockByClass(Control root, string className)
    {
        foreach (var d in root.GetVisualDescendants())
            if (d is TextBlock tb && tb.Classes.Contains(className)) return tb;
        return null;
    }

    // Generic version — used when the bearer of the class can be any
    // Control (e.g. a StackPanel that wraps an icon + label).
    private static Control? FindByClass(Control root, string className)
    {
        foreach (var d in root.GetVisualDescendants())
            if (d is Control c && c.Classes.Contains(className)) return c;
        return null;
    }

    // FluentTheme's accent resource naming has changed across versions
    // (SystemAccentColorBrush vs. AccentFillColorDefaultBrush vs. just
    // deriving a brush from SystemAccentColor). Try each in order, using
    // the control's actual ThemeVariant so variant-specific overrides
    // (Dracula / Netflix / etc.) resolve correctly. Fall back to a
    // hardcoded color so the hover is never invisible.
    private static Avalonia.Media.IBrush ResolveAccentBrush(Control origin)
    {
        var variant = origin.ActualThemeVariant;
        string[] brushKeys = { "SystemAccentColorBrush", "AccentFillColorDefaultBrush" };
        foreach (var key in brushKeys)
        {
            if (origin.TryFindResource(key, variant, out var v) && v is Avalonia.Media.IBrush b)
                return b;
        }
        string[] colorKeys = { "SystemAccentColor", "AccentFillColorDefault" };
        foreach (var key in colorKeys)
        {
            if (origin.TryFindResource(key, variant, out var v) && v is Avalonia.Media.Color c)
                return new Avalonia.Media.SolidColorBrush(c);
        }
        return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0xE5, 0x09, 0x14));
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        // Save current position before tearing down playback, then fully
        // release the media so a stray Play() can't re-start audio from
        // the beginning.
        SaveCurrentPosition();
        _player.Stop();
        _currentMedia?.Dispose();
        _currentMedia = null;
        _currentVideoResult = null;
        _pendingSeekMs = 0;
        _hasReceivedFrame = false;
        _playbackWatchdog.Stop();
        // Stop the auto-hide timer — otherwise its next tick would fire
        // HideFullscreenUi *after* we've torn down playback, leaving
        // the timer running with no video to hide UI over.
        _hideUiTimer.Stop();
        ResetTransport();

        BackButton.IsVisible = false;
        PipButton.IsVisible = false;
        TopGradient.IsVisible = false;
        BottomGradient.IsVisible = false;
        TransportBarContainer.IsVisible = false;
        CurrentMovieTitle.IsVisible = false;
        SetLoadingState(false);
        VideoImage.IsVisible = false;
        VideoImage.Source = null;
        if (PipImage is not null) PipImage.Source = null;
        VideoBorder.CornerRadius = new CornerRadius(0);
        VideoBorder.Background = Avalonia.Media.Brushes.Transparent;
        // Tear down PiP if it was up — no video to mirror anymore.
        ExitPipMode();

        // No active playback now → strip the highlight from whatever
        // recent-watched card was showing it. Synchronous so it doesn't
        // linger for a tick after close.
        _pendingPlayingPageUrl = null;
        ApplyRecentPlayingHighlightNow();

        // Return to whichever view the user came from, and keep the
        // sidebar's active-tab highlight in sync.
        switch (_originView)
        {
            case AppView.RecentlyWatched: ShowRecentlyWatchedView(); break;
            case AppView.MyList: ShowMyListView(); break;
            case AppView.Settings: ShowSettingsView(); break;
            default: ShowSearchView(); break;
        }
        _activeTab = _originView;
        UpdateActiveNavTab();
        // Status bar was sitting on "Playing." — refresh to the per-tab
        // default (or the last search summary on the Search tab).
        ResetStatusForActiveTab();

        // Leaving fullscreen when returning to the overlay — the overlay
        // isn't a playback surface, so fullscreen makes no sense there.
        if (WindowState == WindowState.FullScreen)
            ToggleFullscreen();
    }

    private void SaveCurrentPosition()
    {
        if (_currentVideoResult is null) return;
        if (DataContext is not MainWindowViewModel vm) return;

        var pos = _player.Time;
        var len = _player.Length;
        // Ignore obviously-invalid reads (e.g. immediately after stream
        // tear-down). Save only when we have a real time.
        if (pos <= 0) return;
        // In-place update so the card's visual container isn't destroyed
        // and recreated — recreation flickered the "Currently playing"
        // overlay every time we paused / closed.
        vm.Recent.UpdatePositionAndSave(_currentVideoResult, pos, Math.Max(0, len));
        vm.MyList.UpdatePositionAndSave(_currentVideoResult, pos, Math.Max(0, len));
    }

    private void OpenSource_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: VideoResult vr } && !string.IsNullOrWhiteSpace(vr.PageUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = vr.PageUrl,
                    UseShellExecute = true
                });
            }
            catch
            {
                // swallow; browser launch failures are not fatal
            }
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Let TextBox (search field) keep all keyboard input for typing.
        if (e.Source is TextBox) return;

        // No video loaded → don't intercept media shortcuts. Pressing F
        // while on an empty stage would otherwise take the app into
        // fullscreen over a blank player surface.
        if (_currentVideoResult is null) return;

        switch (e.Key)
        {
            case Key.F:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Escape:
                if (WindowState == WindowState.FullScreen) ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Space:
                PlayPause_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.M:
                Mute_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Up:
                AdjustVolume(+5);
                e.Handled = true;
                break;
            case Key.Down:
                AdjustVolume(-5);
                e.Handled = true;
                break;
            case Key.Left:
                SeekRelative(-10_000);
                e.Handled = true;
                break;
            case Key.Right:
                SeekRelative(+10_000);
                e.Handled = true;
                break;
        }

        // After any handled shortcut, move focus off any focused Button to
        // the (non-keyboard-consuming) video area, so the same key press
        // won't re-activate the button AND subsequent Space presses still
        // flow through our handler rather than inserting into a TextBox.
        if (e.Handled && FocusManager?.GetFocusedElement() is Button)
        {
            VideoOverlayRoot?.Focus();
        }

        // Any shortcut in fullscreen briefly reveals the transport bar
        // (mirrors mouse-hover behavior) and resets the 3s hide timer.
        if (e.Handled)
        {
            PulseFullscreenUi();
        }
    }

    private void AdjustVolume(int delta)
    {
        var newValue = Math.Clamp(
            VolumeSlider.Value + delta,
            VolumeSlider.Minimum,
            VolumeSlider.Maximum);
        VolumeSlider.Value = newValue;
        // Volume_Changed will update the player and mute icon.
    }

    private void SeekRelative(long deltaMs)
    {
        if (_player.Length <= 0 || !_player.IsSeekable) return;
        var newTime = Math.Clamp(_player.Time + deltaMs, 0L, _player.Length);
        _player.Time = newTime;

        // TimeChanged only fires during active playback; update UI directly
        // so the remaining-time label and slider reflect the seek even
        // while paused.
        DurationLabel.Text = FormatTime(
            TimeSpan.FromMilliseconds(Math.Max(0, _player.Length - newTime)));
        SetSliderFromPlayer((double)newTime / _player.Length * 1000.0);
    }

    private void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _preFullScreenState;
            TitleBar.IsVisible = true;
            StatusFooter.IsVisible = true;
            SidebarRail.IsVisible = true;
            BodyGrid.ColumnDefinitions[0].Width = new GridLength(64);
            RootGrid.Margin = new Thickness(12);
            ContentGrid.Margin = new Thickness(0, 12, 0, 0);
            Cursor = Cursor.Default;

            // Restore the right view: full-size playback if a video is
            // still loaded, otherwise the idle Search tab.
            var hasVideo = _currentVideoResult is not null;
            if (hasVideo) ShowPlaybackView(_originView);
            else ShowSearchView();

            // Only re-show video-specific UI if a video is actually loaded.
            // Otherwise (e.g. exiting fullscreen as part of Back_Click)
            // keep the transport + back arrow hidden.
            TransportBarContainer.IsVisible = hasVideo;
            BackButton.IsVisible = hasVideo;
            PipButton.IsVisible = hasVideo;
            TopGradient.IsVisible = hasVideo;
            BottomGradient.IsVisible = hasVideo;
            CurrentMovieTitle.IsVisible = hasVideo;
            if (hasVideo) VideoBorder.CornerRadius = new CornerRadius(4);

            _hideUiTimer.Stop();
            if (hasVideo) _hideUiTimer.Start();
        }
        else
        {
            // Pressing F (or otherwise entering fullscreen) while in PiP
            // should fullscreen the actual video, not the small PiP frame.
            // Exit PiP first so the full-size video stage is mounted, then
            // enter fullscreen against that.
            if (_isPipMode)
            {
                ExitPipMode();
                if (_currentVideoResult is not null)
                    ShowPlaybackView(_originView);
            }

            _preFullScreenState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState;
            WindowState = WindowState.FullScreen;
            TitleBar.IsVisible = false;
            SearchBar.IsVisible = false;
            ResultsPanel.IsVisible = false;
            StatusFooter.IsVisible = false;
            SidebarRail.IsVisible = false;
            BodyGrid.ColumnDefinitions[0].Width = new GridLength(0);
            ContentGrid.ColumnDefinitions[0].Width = new GridLength(0);
            RootGrid.Margin = new Thickness(0);
            ContentGrid.Margin = new Thickness(0);
            VideoBorder.CornerRadius = new CornerRadius(0);
            TransportBarContainer.IsVisible = true;
            _hideUiTimer.Stop();
            _hideUiTimer.Start();
        }
    }

    private void OnOverlayPointerMoved(object? sender, PointerEventArgs e)
    {
        PulseFullscreenUi();
    }

    // Transport bar + back arrow auto-hide after 3s of inactivity both in
    // fullscreen and windowed mode. Cursor hiding is still fullscreen-only
    // — in windowed mode the user may want to keep interacting with the
    // search bar / results while the video plays.
    private void PulseFullscreenUi()
    {
        if (_currentVideoResult is null) return;
        TransportBarContainer.IsVisible = true;
        BackButton.IsVisible = true;
        PipButton.IsVisible = true;
        TopGradient.IsVisible = true;
        BottomGradient.IsVisible = true;
        CurrentMovieTitle.IsVisible = true;
        Cursor = Cursor.Default;
        _hideUiTimer.Stop();
        _hideUiTimer.Start();
    }

    private void HideFullscreenUi()
    {
        _hideUiTimer.Stop();
        if (_currentVideoResult is null) return;
        TransportBarContainer.IsVisible = false;
        BackButton.IsVisible = false;
        PipButton.IsVisible = false;
        TopGradient.IsVisible = false;
        BottomGradient.IsVisible = false;
        CurrentMovieTitle.IsVisible = false;
        if (WindowState == WindowState.FullScreen)
            Cursor = new Cursor(StandardCursorType.None);
    }

    private bool _flyoutHandlerAttached;

    private void SourcesFlyout_Opened(object? sender, EventArgs e)
    {
        // Park focus on the panel itself so the NumericUpDown doesn't
        // auto-grab it when the flyout appears.
        SourcesFlyoutPanel?.Focus();

        // Register the tunneling click handler once. Done lazily here
        // because the panel isn't part of the visual tree until the
        // flyout opens the first time.
        if (!_flyoutHandlerAttached && SourcesFlyoutPanel is not null)
        {
            SourcesFlyoutPanel.AddHandler(
                PointerPressedEvent,
                SourcesFlyoutPanel_PointerPressed,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            _flyoutHandlerAttached = true;
        }

    }

    private void SourcesFlyoutPanel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // If the click falls inside the NumericUpDown, let it keep focus.
        // Any other click (label, checkbox, empty panel space) moves focus
        // to the panel, blurring the field.
        if (e.Source is Control clicked && IsDescendantOf(clicked, ResultsPerSourceBox))
        {
            return;
        }
        SourcesFlyoutPanel?.Focus();
    }

    private void ResultsPerSourceBox_KeyDown(object? sender, KeyEventArgs e)
    {
        // Enter commits the value (which happens automatically on blur)
        // and moves focus back to the panel so the field no longer looks
        // active.
        if (e.Key == Key.Enter)
        {
            SourcesFlyoutPanel?.Focus();
            e.Handled = true;
        }
    }

    private void ResultsPerSourceBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        // Clearing the field leaves Value = null, which the int-typed
        // binding target can't accept — triggering a red validation
        // border. Coerce back to the minimum so the field always holds
        // a valid value.
        if (sender is NumericUpDown nud && nud.Value is null)
            nud.Value = nud.Minimum;
    }

    private void TmdbKeyBox_KeyDown(object? sender, KeyEventArgs e)
    {
        // Enter just defocuses the field; validation only runs when the
        // user clicks the Check button.
        if (e.Key == Key.Enter)
        {
            SourcesFlyoutPanel?.Focus();
            e.Handled = true;
        }
    }

    private void ValidateTmdbKey_Click(object? sender, RoutedEventArgs e)
    {
        ValidateTmdbKey();
    }

    // HTTP client reused across validation calls. Short timeout — we want
    // fast pass/fail feedback, not a hang.
    private readonly System.Net.Http.HttpClient _tmdbValidationHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    // Incrementing counter so late-arriving validation responses from
    // outdated keystrokes don't overwrite the current status.
    private int _tmdbValidationGeneration;

    private async void ValidateTmdbKey()
    {
        var key = TmdbKeyBox?.Text?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            SetTmdbKeyStatus("", null);
            return;
        }

        var generation = ++_tmdbValidationGeneration;
        SetTmdbKeyStatus("…", Avalonia.Media.Brushes.Gray);

        bool ok;
        try
        {
            var client = new TmdbClient(_tmdbValidationHttp);
            ok = await client.ValidateKeyAsync(key, default);
        }
        catch { ok = false; }

        // Drop this result if another validation was kicked off in the meantime.
        if (generation != _tmdbValidationGeneration) return;

        if (ok)
            SetTmdbKeyStatus("✓", Avalonia.Media.Brushes.LimeGreen);
        else
            SetTmdbKeyStatus("✗", Avalonia.Media.Brushes.Tomato);
    }

    // Auto-revert timer: after a validation result is shown for ~3s, the
    // result label disappears and the Check button comes back.
    private DispatcherTimer? _tmdbResultRevertTimer;

    private void SetTmdbKeyStatus(string glyph, Avalonia.Media.IBrush? brush)
    {
        // Legacy text-glyph indicator (still updated for any consumers
        // that rely on it; the visible UI is the Check button below).
        if (TmdbKeyStatusIcon is not null)
        {
            TmdbKeyStatusIcon.Text = glyph;
            if (brush is not null)
                TmdbKeyStatusIcon.Foreground = brush;
        }

        // Cancel any pending revert from a prior validation.
        _tmdbResultRevertTimer?.Stop();

        if (glyph == "✓" || glyph == "✗")
        {
            // Show result label in place of the Check button.
            if (TmdbCheckIconHost is not null) TmdbCheckIconHost.IsVisible = glyph == "✓";
            if (TmdbInvalidIconHost is not null) TmdbInvalidIconHost.IsVisible = glyph == "✗";
            if (TmdbResultText is not null)
            {
                TmdbResultText.Text = glyph == "✓" ? "Valid" : "Not valid";
                TmdbResultText.Foreground = glyph == "✓"
                    ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x3F, 0xAA, 0x63))
                    : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0xD9, 0x54, 0x4D));
            }
            if (TmdbResultLabel is not null) TmdbResultLabel.IsVisible = true;
            if (TmdbValidateBtn is not null) TmdbValidateBtn.IsVisible = false;

            _tmdbResultRevertTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _tmdbResultRevertTimer.Tick -= OnTmdbResultRevert;
            _tmdbResultRevertTimer.Tick += OnTmdbResultRevert;
            _tmdbResultRevertTimer.Start();
        }
        else
        {
            // Idle / loading / empty state — show the Check button.
            if (TmdbResultLabel is not null) TmdbResultLabel.IsVisible = false;
            if (TmdbValidateBtn is not null) TmdbValidateBtn.IsVisible = true;
        }
    }

    private void OnTmdbResultRevert(object? sender, EventArgs e)
    {
        _tmdbResultRevertTimer?.Stop();
        if (TmdbResultLabel is not null) TmdbResultLabel.IsVisible = false;
        if (TmdbValidateBtn is not null) TmdbValidateBtn.IsVisible = true;
    }

    // Click on empty space inside the settings card removes focus from
    // any text-entry control (NumericUpDown / TMDB TextBox) — without
    // this, the field keeps its caret/selection until you tab away.
    private void SettingsBackground_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual v && IsInsideEditableInput(v)) return;
        SourcesFlyoutPanel?.Focus();
    }

    private static bool IsInsideEditableInput(Visual origin)
    {
        Visual? cur = origin;
        while (cur is not null)
        {
            if (cur is TextBox or NumericUpDown) return true;
            cur = cur.GetVisualParent();
        }
        return false;
    }

    private const int ResizeBorder = 6;

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Edge-triggered window resize: WindowDecorations="None" removes the
        // OS resize frame, so we detect edge clicks ourselves and forward
        // them to BeginResizeDrag. Only when windowed (not maximized /
        // fullscreen).
        if (WindowState == WindowState.Normal
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (GetResizeEdge(e.GetPosition(this)) is { } edge)
            {
                BeginResizeDrag(edge, e);
                e.Handled = true;
                return;
            }
        }

        // If Title or Year TextBox currently has focus and the user clicks
        // somewhere outside of it, move focus to the video area so the
        // TextBox is deselected (no caret, no highlight).
        if (FocusManager?.GetFocusedElement() is not TextBox box) return;
        if (box != TitleBox && box != YearBox) return;
        if (e.Source is Control clicked && IsDescendantOf(clicked, box)) return;

        VideoOverlayRoot?.Focus();
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (WindowState != WindowState.Normal) return;

        Cursor = GetResizeEdge(e.GetPosition(this)) switch
        {
            WindowEdge.North or WindowEdge.South
                => new Cursor(StandardCursorType.SizeNorthSouth),
            WindowEdge.West or WindowEdge.East
                => new Cursor(StandardCursorType.SizeWestEast),
            WindowEdge.NorthWest or WindowEdge.SouthEast
                => new Cursor(StandardCursorType.TopLeftCorner),
            WindowEdge.NorthEast or WindowEdge.SouthWest
                => new Cursor(StandardCursorType.TopRightCorner),
            _ => Cursor.Default,
        };
    }

    private WindowEdge? GetResizeEdge(Point pos)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var b = ResizeBorder;

        var onLeft = pos.X >= 0 && pos.X < b;
        var onRight = pos.X > w - b && pos.X <= w;
        var onTop = pos.Y >= 0 && pos.Y < b;
        var onBottom = pos.Y > h - b && pos.Y <= h;

        if (onTop && onLeft) return WindowEdge.NorthWest;
        if (onTop && onRight) return WindowEdge.NorthEast;
        if (onBottom && onLeft) return WindowEdge.SouthWest;
        if (onBottom && onRight) return WindowEdge.SouthEast;
        if (onTop) return WindowEdge.North;
        if (onBottom) return WindowEdge.South;
        if (onLeft) return WindowEdge.West;
        if (onRight) return WindowEdge.East;
        return null;
    }

    private static bool IsDescendantOf(Control? child, Control? ancestor)
    {
        if (ancestor is null) return false;
        for (var c = child; c is not null; c = c.Parent as Control)
        {
            if (ReferenceEquals(c, ancestor)) return true;
        }
        return false;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _hideUiTimer.Stop();
        // Save the current playback position before stopping the player —
        // Stop() clears Time to 0 so a later save would overwrite with
        // zero.
        SaveCurrentPosition();
        _player.Stop();
        _currentMedia?.Dispose();
        _player.Dispose();
        _libvlc.Dispose();
    }
}
