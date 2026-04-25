using System;
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
        Dispatcher.UIThread.Post(() => VideoImage.InvalidateVisual());
    }

    private void WirePlayerEvents()
    {
        _player.Playing += (_, _) => UI(() =>
        {
            PlayPauseIcon.Text = "⏸";
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
            PlayPauseIcon.Text = "▶";
            SaveCurrentPosition();
        });
        _player.EndReached += (_, _) => UI(() =>
        {
            PlayPauseIcon.Text = "▶";
            // Mark as fully watched.
            if (_currentVideoResult is not null && DataContext is MainWindowViewModel vm)
            {
                var len = _player.Length;
                vm.Recent.UpsertAndSave(_currentVideoResult, len > 0 ? len : 0, len);
            }
        });
        _player.Stopped    += (_, _) => UI(ResetTransport);
        _player.TimeChanged   += OnTimeChanged;
        _player.LengthChanged += OnLengthChanged;
    }

    private static void UI(Action a) => Dispatcher.UIThread.Post(a);

    private void ResetTransport()
    {
        PlayPauseIcon.Text = "▶";
        SetSliderFromPlayer(0);
        DurationLabel.Text = "00:00";
        PositionSlider.IsEnabled = false;
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
                SetSliderFromPlayer((double)e.Time / length * 1000.0);
                DurationLabel.Text = FormatTime(
                    TimeSpan.FromMilliseconds(Math.Max(0, length - e.Time)));
            }
            else
            {
                PositionSlider.IsEnabled = false;
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
                DurationLabel.Text = FormatTime(TimeSpan.FromMilliseconds(e.Length));
                PositionSlider.IsEnabled = _player.IsSeekable;
            }
            else
            {
                DurationLabel.Text = "—";
                PositionSlider.IsEnabled = false;
            }
        });
    }

    private void SetSliderFromPlayer(double value)
    {
        _isSliderUpdatingFromPlayer = true;
        PositionSlider.Value = value;
        _isSliderUpdatingFromPlayer = false;
    }

    private void OnPositionSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isSliderUpdatingFromPlayer) return;
        if (_player.Length <= 0 || !_player.IsSeekable) return;

        var fraction = e.NewValue / 1000.0;
        _player.Position = (float)fraction;

        // TimeChanged only fires during active playback, so when seeking
        // while paused we update the remaining-time label ourselves.
        var newTimeMs = (long)(fraction * _player.Length);
        DurationLabel.Text = FormatTime(
            TimeSpan.FromMilliseconds(Math.Max(0, _player.Length - newTimeMs)));
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
            vm.Recent.Items.CollectionChanged += (_, _) => RefreshRecentEmpty();

            switch (vm.Sources.Theme)
            {
                case "Light": LightThemeRadio.IsChecked = true; break;
                case "Dark": DarkThemeRadio.IsChecked = true; break;
                case "Dracula": DraculaThemeRadio.IsChecked = true; break;
                case "Netflix": NetflixThemeRadio.IsChecked = true; break;
                case "PrimeVideo": PrimeVideoThemeRadio.IsChecked = true; break;
                case "DisneyPlus": DisneyPlusThemeRadio.IsChecked = true; break;
                case "Catppuccin": CatppuccinThemeRadio.IsChecked = true; break;
                case "LightLavender": LightLavenderThemeRadio.IsChecked = true; break;
                case "LightMint": LightMintThemeRadio.IsChecked = true; break;
                case "LightApricot": LightApricotThemeRadio.IsChecked = true; break;
                default: SystemThemeRadio.IsChecked = true; break;
            }
        }
    }

    private void OnPlayRequested(StreamResult s, VideoResult source, long startPosMs)
    {
        UI(() =>
        {
            // Save the movie we're leaving before overwriting _currentVideoResult.
            SaveCurrentPosition();

            _currentVideoResult = source;
            _pendingSeekMs = startPosMs;
            if (RecentOverlay is not null) RecentOverlay.IsVisible = false;
            if (BackButton is not null) BackButton.IsVisible = true;
            if (TopGradient is not null) TopGradient.IsVisible = true;
            if (BottomGradient is not null) BottomGradient.IsVisible = true;
            if (TransportBarContainer is not null) TransportBarContainer.IsVisible = true;
            if (VideoImage is not null) VideoImage.IsVisible = true;
            // Round the video surface while a movie is playing. Left at 0
            // while the overlay is visible to avoid corner artifacts
            // between ScrollViewer content and the clip.
            VideoBorder.CornerRadius = new CornerRadius(4);
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
        if (_player.State == VLCState.Playing) _player.Pause();
        else _player.Play();
    }

    private void Back10_Click(object? sender, RoutedEventArgs e) =>
        SeekRelative(-10_000);

    private void Forward10_Click(object? sender, RoutedEventArgs e) =>
        SeekRelative(+10_000);

    private void OnVideoTapped(object? sender, TappedEventArgs e)
    {
        if (RecentOverlay.IsVisible) return;
        if (IsInsideTransportBar(e.Source)) return;
        _singleTapTimer.Stop();
        _singleTapTimer.Start();
    }

    private void OnVideoDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (RecentOverlay.IsVisible) return;
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
        => MuteIcon.Text = (_isMuted || VolumeSlider.Value == 0) ? "🔇" : "🔊";

    private void Fullscreen_Click(object? sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

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

    private void SystemTheme_Checked(object? sender, RoutedEventArgs e)
        => SelectTheme(sender, "System");

    private void LightTheme_Checked(object? sender, RoutedEventArgs e)
        => SelectTheme(sender, "Light");

    private void DarkTheme_Checked(object? sender, RoutedEventArgs e)
        => SelectTheme(sender, "Dark");

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

    private void SelectTheme(object? sender, string theme)
    {
        if (sender is RadioButton { IsChecked: true })
        {
            App.ApplyTheme(theme);
            if (DataContext is ViewModels.MainWindowViewModel vm)
                vm.Sources.Theme = theme;
        }
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
            _ = vm.PlayRecentAsync(rw);
        }
    }

    private void RecentCard_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Button btn && FindPosterBorder(btn) is Border poster)
            poster.BorderBrush = ResolveAccentBrush(btn);
    }

    private void RecentCard_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Button btn && FindPosterBorder(btn) is Border poster)
            poster.BorderBrush = Avalonia.Media.Brushes.Transparent;
    }

    private static Border? FindPosterBorder(Control root)
    {
        foreach (var d in root.GetVisualDescendants())
            if (d is Border b && b.Classes.Contains("poster")) return b;
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
        // HideFullscreenUi *after* we've set RecentOverlay.IsVisible,
        // which itself no-ops (no video) but leaves the timer running.
        _hideUiTimer.Stop();
        ResetTransport();

        BackButton.IsVisible = false;
        TopGradient.IsVisible = false;
        BottomGradient.IsVisible = false;
        TransportBarContainer.IsVisible = false;
        CurrentMovieTitle.IsVisible = false;
        VideoImage.IsVisible = false;
        VideoImage.Source = null;
        VideoBorder.CornerRadius = new CornerRadius(0);
        RecentOverlay.IsVisible = true;

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
        vm.Recent.UpsertAndSave(_currentVideoResult, pos, Math.Max(0, len));
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
        // while on the recently-watched overlay would otherwise take the
        // app into fullscreen over an empty player surface.
        if (RecentOverlay.IsVisible) return;

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
            SearchBar.IsVisible = true;
            ResultsPanel.IsVisible = true;
            StatusText.IsVisible = true;
            ContentGrid.ColumnDefinitions[0].Width = new GridLength(380);
            RootGrid.Margin = new Thickness(12);
            ContentGrid.Margin = new Thickness(0, 12, 0, 0);
            Cursor = Cursor.Default;

            // Only re-show video-specific UI if a video is actually loaded.
            // Otherwise (e.g. exiting fullscreen as part of Back_Click, or
            // somehow fullscreening the overlay) keep the transport + back
            // arrow hidden so the recently-watched view stays clean.
            var hasVideo = _currentVideoResult is not null;
            TransportBarContainer.IsVisible = hasVideo;
            BackButton.IsVisible = hasVideo;
            TopGradient.IsVisible = hasVideo;
            BottomGradient.IsVisible = hasVideo;
            CurrentMovieTitle.IsVisible = hasVideo;
            if (hasVideo) VideoBorder.CornerRadius = new CornerRadius(4);

            _hideUiTimer.Stop();
            if (hasVideo) _hideUiTimer.Start();
        }
        else
        {
            _preFullScreenState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState;
            WindowState = WindowState.FullScreen;
            TitleBar.IsVisible = false;
            SearchBar.IsVisible = false;
            ResultsPanel.IsVisible = false;
            StatusText.IsVisible = false;
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

        // Show the ✓ / ✗ state right away if a key is already saved.
        TmdbKeyBox_LostFocus(TmdbKeyBox, new RoutedEventArgs());
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
        if (e.Key == Key.Enter)
        {
            // Losing focus triggers validation via LostFocus.
            SourcesFlyoutPanel?.Focus();
            e.Handled = true;
        }
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

    private async void TmdbKeyBox_LostFocus(object? sender, RoutedEventArgs e)
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

    private void SetTmdbKeyStatus(string glyph, Avalonia.Media.IBrush? brush)
    {
        if (TmdbKeyStatusIcon is null) return;
        TmdbKeyStatusIcon.Text = glyph;
        if (brush is not null)
            TmdbKeyStatusIcon.Foreground = brush;
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
