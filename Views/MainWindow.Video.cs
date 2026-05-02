using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using MovieHunter.Models;
using MovieHunter.Services;
using MovieHunter.ViewModels;

namespace MovieHunter.Views;

// Video player subsystem: LibVLC initialization, software-rendering
// callbacks (frame decoding into a WriteableBitmap), all transport
// controls (play/pause/seek/volume/mute), the position slider sync
// between the main and PiP transports, the Netflix-style center HUD,
// fullscreen toggle + auto-hiding chrome, and the keyboard shortcut
// router. The constructor in MainWindow.axaml.cs assigns the readonly
// fields below — partial classes share state, so the assignments
// satisfy the readonly invariant.
public partial class MainWindow
{
    // ── Player state ─────────────────────────────────────────────────
    private readonly LibVLC _libvlc;
    private readonly MediaPlayer _player;
    private Media? _currentMedia;

    private long _pendingSeekMs;
    // True while VLC has actually transitioned to Playing for the
    // current media — not just while we've called Play() and a click
    // has been registered. Drives the Recent / MyList card badge so
    // bs.to series show "Continue playing" through the captcha + load
    // chain and only flip to "Currently playing" once the video is
    // really decoding frames. Movies don't strictly need the gate
    // (their click → playback loop is a few hundred ms), but the
    // unified flag keeps the badge honest across both kinds.
    private bool _isVideoActuallyPlaying;
    // Resume position preserved across a Stopped/Ended → Play restart.
    // _pendingSeekMs is a one-shot (cleared once the Playing event has
    // applied it), so when an HLS stream stalls right after the initial
    // seek and the user clicks Play to retry, _pendingSeekMs is already
    // 0 and the restart would otherwise drop the user back at the start.
    // _resumeFromMs holds the original startPosMs from OnPlayRequested
    // for the lifetime of the current media so the restart re-seeks to
    // the same spot.
    private long _resumeFromMs;
    private bool _isSliderUpdatingFromPlayer;
    // Drag-detection for the PositionSlider thumb. When the user is
    // mid-drag we DON'T seek on every ValueChanged — that fired
    // hundreds of seeks during a single drag, which on HLS streams
    // (bs.to → VOE) made VLC honor only an early position and then
    // ignore the final drag-released one. We capture the fraction in
    // _pendingDragFraction during the drag and apply a single seek
    // when DragCompleted fires.
    private bool _isSliderDragging;
    private double _pendingDragFraction;
    private WindowState _preFullScreenState = WindowState.Normal;
    private readonly DispatcherTimer _hideUiTimer;
    private readonly DispatcherTimer _playbackWatchdog;
    private readonly DispatcherTimer _singleTapTimer;
    private bool _hasReceivedFrame;

    // ── Software-rendering bitmap state ──────────────────────────────
    private WriteableBitmap? _bitmap;
    private ILockedFramebuffer? _currentFrame;
    private readonly object _bitmapLock = new();

    // ── Volume / mute state ──────────────────────────────────────────
    private int _volumeBeforeMute = 80;
    private bool _isMuted;
    private bool _volumeDragging;
    private bool _pointerOverMute;
    private bool _pointerOverVolumePopup;
    private readonly DispatcherTimer _volumePopupHideTimer;

    // ── VLC video callback delegates (kept alive for native interop) ─
    private MediaPlayer.LibVLCVideoFormatCb? _formatCb;
    private MediaPlayer.LibVLCVideoCleanupCb? _cleanupCb;
    private MediaPlayer.LibVLCVideoLockCb? _lockCb;
    private MediaPlayer.LibVLCVideoUnlockCb? _unlockCb;
    private MediaPlayer.LibVLCVideoDisplayCb? _displayCb;

    // ── Software rendering: VLC writes raw frames into our bitmap ────
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

    // ── Player events → UI ───────────────────────────────────────────
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
            // Only NOW does the playing card badge flip from
            // "Continue playing" → "Currently playing". For movies the
            // gap from click to here is small; for bs.to series it
            // covers the full captcha + hoster-extract + buffer chain.
            _isVideoActuallyPlaying = true;
            // Single source of truth for the deferred series reorder —
            // see OnSeriesActuallyPlaying. Movies have already been
            // reordered optimistically at click time / OnPlayRequested,
            // so this is a no-op for them.
            OnSeriesActuallyPlaying();
            ApplyRecentPlayingHighlightNow();
        });
        _player.Paused += (_, _) => UI(() =>
        {
            SetPlayPauseIcons(playing: false);
            SaveCurrentPosition();
        });
        _player.EndReached += (_, _) => UI(() =>
        {
            SetPlayPauseIcons(playing: false);
            _isVideoActuallyPlaying = false;
            ApplyRecentPlayingHighlightNow();
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
        // Track the latest known playback position so a mid-stream
        // Stopped→Play restart (HLS stall) re-seeks to where the user
        // actually was, not to the original resume-from position.
        if (e.Time > 0) _resumeFromMs = e.Time;
        UI(() =>
        {
            _playbackWatchdog.Stop();
            var length = _player.Length;
            if (length > 0)
            {
                PositionSlider.IsEnabled = _player.IsSeekable;
                if (PipPositionSlider is not null)
                    PipPositionSlider.IsEnabled = _player.IsSeekable;
                // While the user is mid-drag, do NOT push the player's
                // current time back into the slider — the player is
                // still playing at its pre-drag position, so this
                // would yank the thumb back to where the drag started
                // every time TimeChanged fires (which it does many
                // times per second). The Recent-progress update below
                // still happens, so the progress bar in Recently
                // watched stays accurate.
                if (!_isSliderDragging)
                {
                    SetSliderFromPlayer((double)e.Time / length * 1000.0);
                    var remaining = FormatTime(
                        TimeSpan.FromMilliseconds(Math.Max(0, length - e.Time)));
                    DurationLabel.Text = remaining;
                    if (PipDurationLabel is not null) PipDurationLabel.Text = remaining;
                }
                // Push the live position into the matching Recent entry so
                // its progress bar tracks playback. In-memory only — disk
                // save still happens on pause / close / end. Skipped while
                // the user is mid-drag on the seek slider — during drag
                // the slider handler is the authoritative source for
                // PositionMs (it pushes the drag-preview position so the
                // Recent / MyList / search progress bars reflect what
                // the user is dragging to). Without this guard the
                // playback tick races the drag handler and the bars
                // oscillate between the live playback time (which
                // hasn't moved yet — seek is deferred to drag-completed)
                // and the drag-preview position, producing visible
                // flicker until release.
                if (!_isSliderDragging
                    && _currentVideoResult is not null
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

    // PointerPressed on the slider — fires for both thumb-grab and
    // track-click. Thumb.DragStarted alone wasn't enough because it
    // only fires when the user grabs the thumb itself; if they
    // clicked the track and then dragged before releasing, the flag
    // never became true and TimeChanged kept yanking the slider back
    // to the player's pre-seek position. PointerPressed catches both
    // entry points uniformly.
    internal void OnPositionSliderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) return;
        _isSliderDragging = true;
        _pendingDragFraction = slider.Value / 1000.0;
    }

    // PointerReleased / PointerCaptureLost — end of the user's
    // interaction. Apply the single deferred seek. Captured pointer
    // can be lost if the user drags off-window then releases; treating
    // it as a release ensures we never get stuck with the dragging
    // flag latched on.
    internal void OnPositionSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
        => EndPositionSliderDrag();

    internal void OnPositionSliderPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => EndPositionSliderDrag();

    private void EndPositionSliderDrag()
    {
        if (!_isSliderDragging) return;
        _isSliderDragging = false;
        if (_player.Length > 0 && _player.IsSeekable
            && _player.State != VLCState.Ended
            && _player.State != VLCState.Stopped)
        {
            // Set both Time and Position. For direct streams (Doodstream
            // MP4) Position is enough; for HLS m3u8 (VOE on bs.to) the
            // segment-aware seek path inside LibVLC is more reliably
            // triggered by Time. Setting both is harmless — whichever
            // wins, the player lands at the user's drag-released spot.
            // Sync the slider visual to the new target right away so
            // the next TimeChanged (which arrives a frame or two
            // later from VLC's seek-complete) doesn't briefly snap
            // the thumb backwards before catching up.
            var targetMs = (long)(_pendingDragFraction * _player.Length);
            _player.Time = targetMs;
            _player.Position = (float)_pendingDragFraction;
            SetSliderFromPlayer(_pendingDragFraction * 1000.0);
            var remaining = FormatTime(
                TimeSpan.FromMilliseconds(Math.Max(0, _player.Length - targetMs)));
            DurationLabel.Text = remaining;
            if (PipDurationLabel is not null) PipDurationLabel.Text = remaining;
        }
        else if (_player.State == VLCState.Ended || _player.State == VLCState.Stopped)
        {
            if (_currentMedia is null) return;
            if (_player.Length > 0)
                _pendingSeekMs = (long)(_pendingDragFraction * _player.Length);
            _player.Play(_currentMedia);
        }
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

        if (_isSliderDragging)
        {
            // Mid-drag: just remember the latest fraction and update
            // the visual remaining-time label below. The actual seek
            // is deferred until DragCompleted, so HLS streams only
            // see one Position write at the drag's final value
            // instead of hundreds during the drag (which racing the
            // final write with earlier in-flight ones is what made
            // bs.to/VOE seeks land on the wrong spot).
            _pendingDragFraction = fraction;
        }
        else
        {
            // Click on the track or programmatic change — single
            // ValueChanged event, so seek immediately.
            _player.Position = (float)fraction;
        }

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

    // Single source of truth for the bs.to-series-only deferred list
    // reorder. Called from the Playing event when frames first arrive,
    // so the user's mental model matches what they see: a series card
    // flips from "Continue playing" → "Currently playing" AND moves to
    // the top of Recent / MyList at the same instant.
    //
    // Movies don't go through this path. They use the optimistic
    // reorder in the click handlers / OnPlayRequested because their
    // click → playback gap is short and an immediate reorder feels
    // right. For series the gap covers a full captcha + hoster-extract
    // + buffer chain (5–30 s), so deferring matches the user's
    // perception of "the show is now actually playing".
    //
    // No-op when:
    //   - There's no active video (e.g. user closed playback before
    //     the Playing event landed).
    //   - The active video isn't a series episode (no SeriesPageUrl).
    //     Movies skip the body without touching the lists.
    private void OnSeriesActuallyPlaying()
    {
        if (_currentVideoResult is not { } source) return;
        if (string.IsNullOrEmpty(source.SeriesPageUrl)) return;
        if (DataContext is not MainWindowViewModel vm) return;
        vm.Recent.MoveToTopAndSave(source);
        vm.MyList.MoveToTopAndSave(source);
        vm.MyList.UpdateLastEpisodeAndSave(source);
    }

    // ── VM-driven playback entry point ───────────────────────────────
    private void OnPlayRequested(StreamResult s, VideoResult source, long startPosMs)
    {
        UI(() =>
        {
            // Snapshot whether we're already inside a playback session
            // BEFORE overwriting _currentVideoResult — used below to
            // decide whether to (re-)capture the origin tab. An episode
            // switch via the in-player Episodes popup re-enters this
            // method while still in the playback view, so
            // CurrentViewFromPanels() would return Search (none of the
            // tab panels are visible during playback), wiping out the
            // origin tab. Skip the capture for the episode-switch case
            // so PiP / Back returns to the right tab.
            var wasAlreadyPlaying = _currentVideoResult is not null;

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
            _resumeFromMs = startPosMs;
            // New media is loading — clear the actually-playing flag so
            // the playing card's badge stays at "Continue playing" until
            // VLC's Playing event re-asserts it.
            _isVideoActuallyPlaying = false;
            // Real playback has begun — drop the optimistic-highlight URL
            // so subsequent updates flow through _currentVideoResult.
            _pendingPlayingPageUrl = null;
            // Insert into Recently watched the moment playback starts so
            // the card shows up right away (instead of only after pause /
            // close, which is when SaveCurrentPosition runs). The entry
            // floats to the top and gets a real position/length on the
            // next save.
            if (DataContext is MainWindowViewModel vmAdd)
            {
                var isSeriesEpisode = !string.IsNullOrEmpty(source.SeriesPageUrl);
                if (isSeriesEpisode)
                {
                    // bs.to series — defer the reorder until VLC fires
                    // the Playing event (see OnSeriesActuallyPlaying).
                    // UpdatePositionAndSave updates an existing entry's
                    // fields in place WITHOUT moving it to the top, so
                    // the card stays at its current position; for a
                    // brand-new series with no prior Recent entry it
                    // falls back to UpsertAndSave which inserts at top
                    // (the only sensible place for a fresh entry).
                    // MyList move + last-episode write are also
                    // deferred — both run at OnSeriesActuallyPlaying.
                    vmAdd.Recent.UpdatePositionAndSave(source, Math.Max(0, startPosMs), 0);
                }
                else
                {
                    // Movie — optimistic reorder. The click → frame gap
                    // is short enough that floating the card to top
                    // right away matches user expectation.
                    vmAdd.Recent.UpsertAndSave(source, Math.Max(0, startPosMs), 0);
                    vmAdd.MyList.MoveToTopAndSave(source);
                    vmAdd.MyList.UpdateLastEpisodeAndSave(source);
                }
            }

            // Highlight the now-playing card in the Recently watched panel
            // (deferred to next tick so the freshly-upserted item has had
            // a layout pass to materialise its visual).
            RefreshRecentPlayingHighlight();
            // Light up the matching search-result row, if any. Mirrors
            // the recent / my-list card highlight; covers the case
            // where the user kept their search visible after starting
            // playback and wants to spot the active result quickly.
            SyncSearchResultsCurrentlyPlaying();
            // Capture which view the user was on so Back_Click can return
            // there — playback from the Recently watched tab should *stay*
            // in that tab, not jump to Search. Only do this on FRESH
            // playback (no prior video). Episode switches via the in-
            // player popup keep their original origin so PiP / Back
            // doesn't dump the user to Search just because they hopped
            // between episodes mid-session.
            if (!wasAlreadyPlaying)
            {
                _originView = CurrentViewFromPanels();
                _activeTab = _originView;
            }
            // Starting fresh playback — make sure any previous PiP overlay
            // is closed so the video shows full size.
            ExitPipMode();
            // Mount the full-width video stage. Search bar + results panel
            // are hidden during playback regardless of origin tab.
            ShowPlaybackView();
            if (BackButton is not null) BackButton.IsVisible = true;
            if (PipButton is not null) PipButton.IsVisible = true;
            // Episodes button — only relevant for series episodes.
            // Reset the popup's season cache so a different series
            // triggers a fresh GetSeriesAsync on the next hover.
            if (EpisodesBtn is not null)
                EpisodesBtn.IsVisible = !string.IsNullOrEmpty(source.SeriesPageUrl);
            // Next-episode button: same series-only visibility as
            // EpisodesBtn. Enable state is driven by the cached season
            // list — call after ResetEpisodesPopupCache so we read
            // post-reset cache state (different-series resets clear
            // _episodesPopupSeasons; same-series keeps it).
            if (NextEpisodeBtn is not null)
                NextEpisodeBtn.IsVisible = !string.IsNullOrEmpty(source.SeriesPageUrl);
            ResetEpisodesPopupCache(source);
            UpdateNextEpisodeButtonState();
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
            VideoBorder.CornerRadius = new CornerRadius(12);
            VideoBorder.Background = Avalonia.Media.Brushes.Black;
            if (CurrentMovieTitle is not null)
            {
                SetVideoTitleInlines(source);
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
            // Resume position via :start-time (seconds) instead of a
            // post-Play seek. On HLS streams (bs.to → VOE) the post-Play
            // path was unreliable: VLC's Playing event fires before the
            // m3u8 index is fully loaded, and seeking at that point can
            // land on a missing segment and stall the player on a black
            // frame. :start-time is consumed by the demuxer at media
            // open time, so the playlist is already positioned at the
            // target segment before the first frame is decoded.
            // _pendingSeekMs is left set as a belt-and-braces fallback —
            // if for any reason :start-time is ignored (older VLC, edge-
            // case stream type), the Playing-event seek still corrects
            // the position; if :start-time worked, the redundant seek
            // is a no-op against the same offset.
            if (startPosMs > 0)
                next.AddOption(
                    $":start-time={(startPosMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            System.Diagnostics.Debug.WriteLine(
                $"[Play] url={s.Url} UA={s.HttpUserAgent} Ref={s.HttpReferer}");

            if (!string.IsNullOrWhiteSpace(s.AudioUrl))
                next.AddSlave(MediaSlaveType.Audio, 4, s.AudioUrl!);

            _player.Play(next);
            _currentMedia?.Dispose();
            _currentMedia = next;
        });
    }

    // ── Transport bar handlers ───────────────────────────────────────
    private void PlayPause_Click(object? sender, RoutedEventArgs e)
    {
        // Belt-and-suspenders: if no media is loaded (e.g. after Back)
        // there's nothing to play/pause, and _player.Play() with no arg
        // could resume VLC's internally-cached last media on some
        // backends.
        if (_currentMedia is null) return;
        // Surface the PiP chrome on every play/pause toggle so keyboard
        // (Space) feedback is visible even when the cursor is away from
        // the bar; pulse resets the 3-second auto-hide timer.
        if (_isPipMode) PulsePipUi();
        // HUD shows the icon for the resulting state — pausing → "pause",
        // any flavour of resume/start → "play".
        var willPause = _player.State == VLCState.Playing;
        PulsePlayerHud(willPause ? "pause" : "play");
        if (willPause) { _player.Pause(); return; }
        // Restart from a Stopped/Ended state. Two cases:
        //   - Genuine end-of-stream: replay from zero (the user finished
        //     the video and clicked play again).
        //   - Mid-playback stall: HLS streams (bs.to → VOE) sometimes
        //     stall right after the initial resume seek lands on a
        //     segment that hasn't finished downloading yet — the player
        //     transitions to Stopped without ever showing a frame, and
        //     the user clicks play to retry. In that case we need to
        //     re-apply the resume position, not start over from zero.
        // EndReached fires for genuine end-of-stream, so we use that
        // (via _player.State == Ended) to tell the two cases apart:
        // Stopped without Ended means a stall, restart with the resume
        // position; Ended means the user has finished, replay from 0.
        if (_player.State == VLCState.Ended || _player.State == VLCState.Stopped)
        {
            _pendingSeekMs = _player.State == VLCState.Stopped ? _resumeFromMs : 0;
            _player.Play(_currentMedia);
            return;
        }
        _player.Play();
    }

    private void Back10_Click(object? sender, RoutedEventArgs e) =>
        SeekRelative(-10_000);

    private void Forward10_Click(object? sender, RoutedEventArgs e) =>
        SeekRelative(+10_000);

    // ── Click / double-click on the video surface ────────────────────
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
        // Click-to-toggle pauses (or resumes) playback. Pulse the HUD
        // with the resulting-state icon so a click on the video gets
        // the same visual confirmation as clicking the play/pause
        // button or pressing Space.
        var willPause = _player.State == VLCState.Playing;
        PulsePlayerHud(willPause ? "pause" : "play");
        if (willPause) _player.Pause();
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
            cur = Avalonia.VisualTree.VisualExtensions.GetVisualParent(cur);
        }
        return false;
    }

    // ── Volume / mute ────────────────────────────────────────────────
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

        // Persist across restarts — but skip during an active slider
        // drag, since every Sources.Volume = ... write triggers a
        // synchronous JSON save (PropertyChanged → Save pipeline) and
        // a drag fires ValueChanged dozens of times. The PointerReleased
        // hook on VolumeSlider (in MainWindow.axaml.cs ctor) saves the
        // final value once the user lets go. Hotkey-driven AdjustVolume
        // calls don't drag, so they save on the spot.
        if (!_volumeDragging && DataContext is MainWindowViewModel vm)
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

    // Click-suppression on the volume popup — same reasoning as the
    // Episodes popup. Without these, a click on empty popup space
    // bubbles out to VideoOverlayRoot.Tapped and toggles play/pause.
    // PointerPressed alone isn't enough since Tapped/DoubleTapped are
    // gesture-recogniser events synthesised independently.
    private void VolumePopupContent_PointerPressed(object? sender, PointerPressedEventArgs e)
        => e.Handled = true;

    private void VolumePopupContent_Tapped(object? sender, TappedEventArgs e)
        => e.Handled = true;

    private void ShowVolumePopup()
    {
        // Hard-close sibling popups (episodes / next-episode) so a
        // fast pointer sweep across the transport bar doesn't leave
        // them lingering during the 120 ms hide grace.
        CloseSiblingTransportPopups(TransportPopupVolume);
        VolumePopupText.Text = $"{(int)VolumeSlider.Value}%";
        VolumePopup.IsOpen = true;
        // Sticky hover scale on the mute icon while the popup is up
        // (same recipe used by Episodes + NextEpisode). Removed in
        // the volume popup's hide tick.
        MuteBtn?.Classes.Set("popup-open", true);
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
        // HUD reflects the resulting state — muted now shows the
        // crossed-out volume icon, unmuted shows the regular speaker.
        PulsePlayerHud(_isMuted || VolumeSlider.Value == 0 ? "mute" : "volume");
        // Surface the PiP chrome on M-key mute toggles too — without
        // this the PiP transport stays hidden while the user blindly
        // mutes/unmutes from a hotkey.
        if (_isPipMode) PulsePipUi();
    }

    private void UpdateMuteIcon()
    {
        var muted = _isMuted || VolumeSlider.Value == 0;
        VolumeOnIcon.IsVisible = !muted;
        VolumeOffIcon.IsVisible = muted;
    }

    // ── Fullscreen + auto-hiding chrome ──────────────────────────────
    private void Fullscreen_Click(object? sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
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
            if (hasVideo) ShowPlaybackView();
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
            if (hasVideo) VideoBorder.CornerRadius = new CornerRadius(12);

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
                    ShowPlaybackView();
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

    // ── Keyboard shortcuts ───────────────────────────────────────────
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
        // Hotkey volume nudges (↑ / ↓) get their own HUD pulse — the
        // slider isn't visible without hover, so without this the user
        // has no on-screen confirmation the keypress did anything.
        PulsePlayerHud(newValue == 0 ? "mute" : "volume");
        // Surface the PiP chrome on volume hotkeys (↑ / ↓) so the user
        // gets the same 3-second reveal as Space / ← / →.
        if (_isPipMode) PulsePipUi();
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
        // Surface the PiP chrome on every keyboard seek (← / →) so the
        // user sees the slider scrub past the new position instead of
        // having to nudge the mouse to bring the controls back.
        if (_isPipMode) PulsePipUi();
        PulsePlayerHud(deltaMs < 0 ? "back" : "forward");
    }

    // ── Netflix-style center HUD ─────────────────────────────────────
    // Flashes a big icon on a translucent black pill in the middle of
    // the video for ~half a second, fading in (Opacity transition) and
    // then fading out via the timer below. Driven from PlayPause /
    // SeekRelative / Volume_Changed / Mute_Click so every play/pause/
    // skip/volume action — button or hotkey — gets a quick on-screen
    // reassurance pulse.
    private DispatcherTimer? _hudHideTimer;

    // Whichever HUD (main or PiP) was last lit by PulsePlayerHud —
    // captured so OnHudHideTick can fade out the correct one even if
    // the user has flipped PiP mode in between.
    private Border? _activeHud;

    private void PulsePlayerHud(string action)
    {
        if (_currentVideoResult is null) return;

        // Pick the right HUD: in PiP we drive the mini overlay inside
        // PipFrame, otherwise the full-size center HUD over the main
        // video surface. Each has its own set of six icon Paths and
        // its own Border with a DoubleTransition.
        var hud = _isPipMode ? PipPlayerHud : PlayerHud;
        if (hud is null) return;

        if (_isPipMode)
        {
            PipHudPlayIcon.IsVisible = action == "play";
            PipHudPauseIcon.IsVisible = action == "pause";
            PipHudBackIcon.IsVisible = action == "back";
            PipHudForwardIcon.IsVisible = action == "forward";
            PipHudVolumeIcon.IsVisible = action == "volume";
            PipHudMuteIcon.IsVisible = action == "mute";

            // Scale the PiP HUD to the current PiP frame — pill ~22% of
            // the frame's shorter side so it stays proportionate when
            // the user resizes; clamp so it never gets unreadable
            // small or balloons past the frame.
            var frameMin = Math.Min(
                Math.Max(PipFrame.Bounds.Width, 1),
                Math.Max(PipFrame.Bounds.Height, 1));
            var hudSize = Math.Clamp(frameMin * 0.32, 44, 96);
            hud.Width = hudSize;
            hud.Height = hudSize;
            if (PipHudViewbox is not null)
            {
                var iconSize = hudSize * 0.62;
                PipHudViewbox.Width = iconSize;
                PipHudViewbox.Height = iconSize;
            }
        }
        else
        {
            HudPlayIcon.IsVisible = action == "play";
            HudPauseIcon.IsVisible = action == "pause";
            HudBackIcon.IsVisible = action == "back";
            HudForwardIcon.IsVisible = action == "forward";
            HudVolumeIcon.IsVisible = action == "volume";
            HudMuteIcon.IsVisible = action == "mute";
        }

        // Skip-forward / skip-back shift the pill toward the matching
        // edge of the video (mirrors how YouTube / mobile players show
        // a left/right HUD when double-tapping). All other actions stay
        // centered. The shift distance scales with the frame's width
        // in PiP mode so it doesn't drift off the edge of a small
        // PiP — fixed 80 px on the full-size player.
        hud.HorizontalAlignment = action switch
        {
            "back" => Avalonia.Layout.HorizontalAlignment.Left,
            "forward" => Avalonia.Layout.HorizontalAlignment.Right,
            _ => Avalonia.Layout.HorizontalAlignment.Center,
        };
        var shift = _isPipMode
            ? Math.Clamp(PipFrame.Bounds.Width * 0.1, 16, 60)
            : 80;
        hud.Margin = action switch
        {
            "back" => new Thickness(shift, 0, 0, 0),
            "forward" => new Thickness(0, 0, shift, 0),
            _ => new Thickness(0),
        };

        // Asymmetric fade — quick in, slow out, mirroring Netflix's web
        // player. The DoubleTransition takes a single Duration, so we
        // switch it to 100 ms here for the fade-in and back to 400 ms
        // in OnHudHideTick for the fade-out.
        if (hud.Transitions is { Count: > 0 } t
            && t[0] is Avalonia.Animation.DoubleTransition dt)
        {
            dt.Duration = TimeSpan.FromMilliseconds(100);
        }

        // Cap peak opacity below 1.0 — Netflix's web HUD never goes
        // fully opaque (research suggests ~70-85%); 0.85 keeps the icon
        // legible while letting the video texture show through, so the
        // pulse feels lighter than a hard "punch in".
        hud.Opacity = 0.85;

        _activeHud = hud;

        // Timer matches the 100 ms fade-in so fade-out starts the
        // moment the icon hits peak opacity — no hold. Total visible
        // time ≈ 500 ms (100 in + 400 out).
        _hudHideTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _hudHideTimer.Tick -= OnHudHideTick;
        _hudHideTimer.Tick += OnHudHideTick;
        _hudHideTimer.Stop();
        _hudHideTimer.Start();
    }

    private void OnHudHideTick(object? sender, EventArgs e)
    {
        _hudHideTimer?.Stop();
        var hud = _activeHud;
        if (hud is null) return;
        // Slower fade-out (400 ms) than fade-in — the icon lingers as
        // it dissolves so the user has time to register what happened.
        if (hud.Transitions is { Count: > 0 } t
            && t[0] is Avalonia.Animation.DoubleTransition dt)
        {
            dt.Duration = TimeSpan.FromMilliseconds(400);
        }
        hud.Opacity = 0;
    }

    // ── Closing playback ─────────────────────────────────────────────
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
        _isVideoActuallyPlaying = false;
        _playbackWatchdog.Stop();
        // Stop the auto-hide timer — otherwise its next tick would fire
        // HideFullscreenUi *after* we've torn down playback, leaving
        // the timer running with no video to hide UI over.
        _hideUiTimer.Stop();
        ResetTransport();

        BackButton.IsVisible = false;
        PipButton.IsVisible = false;
        if (EpisodesBtn is not null) EpisodesBtn.IsVisible = false;
        if (NextEpisodeBtn is not null) NextEpisodeBtn.IsVisible = false;
        if (NextEpisodePopup is not null) NextEpisodePopup.IsOpen = false;
        if (EpisodesPopup is not null) EpisodesPopup.IsOpen = false;
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
        SyncSearchResultsCurrentlyPlaying();

        // Return to whichever view the user came from, and keep the
        // sidebar's active-tab highlight in sync.
        ShowViewFor(_originView);
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

    // Builds the player title as Inlines so the show name reads in
    // a heavier weight than the rest of the line. For a series
    // episode: "ShowName (Bold) S1E20 (SemiBold) Episode title (SemiBold)".
    // For a movie: just the title in the default SemiBold weight.
    // The bs.to "| Episode NNN |" prefix is stripped from the episode
    // title since the SnEnn segment already conveys the same info.
    private void SetVideoTitleInlines(VideoResult source)
    {
        if (CurrentMovieTitle is null) return;
        var inlines = new InlineCollection();
        if (!string.IsNullOrEmpty(source.SeriesTitle))
        {
            inlines.Add(new Run(source.SeriesTitle) { FontWeight = FontWeight.Bold });
            if (source.SeasonNumber is int s && source.EpisodeNumber is int e)
                inlines.Add(new Run($" S{s:00}E{e:00}"));
            var epTitle = StripEpisodePrefix(source.Title);
            if (!string.IsNullOrEmpty(epTitle))
                inlines.Add(new Run($" · {epTitle}"));
        }
        else
        {
            // Movies — render the title in the same Bold weight the
            // show name gets for series, so the title's prominence is
            // consistent across the two playback flows.
            inlines.Add(new Run(source.Title ?? string.Empty) { FontWeight = FontWeight.Bold });
        }
        CurrentMovieTitle.Inlines = inlines;
    }

    private static string StripEpisodePrefix(string? title)
    {
        if (string.IsNullOrEmpty(title)) return string.Empty;
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            title.Trim(),
            @"^(?:\|\s*Episode\s+\d+\s*\|\s*)+",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return stripped.TrimStart('|').Trim();
    }
}
