using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MovieHunter.Models;
using MovieHunter.Services;
using MovieHunter.ViewModels;

namespace MovieHunter.Views;

// In-player Episodes popup. Surfaced via the EpisodesBtn between the
// volume cluster and the PiP button on the transport bar; visible only
// while a series episode is playing (toggled in OnPlayRequested /
// Back_Click). Hover-to-open mirrors the volume popup so the user
// doesn't need a click to peek at sibling episodes.
//
// Two views in the same popup body, swapped via IsVisible:
//   1. Episode list  — the default. Header has a back arrow + the
//      currently-displayed season's name; body is the season's
//      episodes. The currently-playing episode gets the .playing-now
//      highlight (same accent fill as the episode picker modal).
//   2. Season list   — shown when the user clicks the back arrow.
//      Lists every season; the season the user is watching is
//      highlighted. Clicking a season swaps back to the episode-list
//      view for that season.
//
// Series data (BsToService.GetSeriesAsync) is fetched lazily on first
// hover after a new series starts and cached in _episodesPopupSeasons.
// The cache is invalidated on each OnPlayRequested via
// ResetEpisodesPopupCache so a different series re-fetches.
public partial class MainWindow
{
    // Hover bookkeeping — same pattern as the volume popup.
    private bool _pointerOverEpisodesBtn;
    private bool _pointerOverEpisodesPopup;
    private DispatcherTimer? _episodesPopupHideTimer;

    // Cached season list for the currently-playing series. Cleared on
    // OnPlayRequested so a different series triggers a fresh fetch.
    private IReadOnlyList<SeriesSeason>? _episodesPopupSeasons;
    // The series URL the cache was built for — guards against using a
    // stale cache if OnPlayRequested didn't get to call
    // ResetEpisodesPopupCache for some reason.
    private string? _episodesPopupCachedFor;
    // Cancellation for in-flight series fetches; closing or restarting
    // the popup cancels any pending response so a slow request can't
    // repopulate the popup with the wrong series.
    private CancellationTokenSource? _episodesPopupCts;

    // Which season the popup is currently displaying. Defaults to the
    // currently-playing episode's season; the user can navigate away
    // via the back arrow + season list.
    private int? _episodesPopupDisplayedSeason;

    // True between PointerPressed inside the popup and the matching
    // release. Keeps the popup open during scrollbar-thumb drags —
    // ScrollBar captures the pointer on press, so when the user drags
    // outside the popup geometry, PointerExited fires on the popup
    // Border but PointerReleased never bubbles back through it (capture
    // routes events to the captured element only). Without this flag,
    // the close timer would fire mid-drag. Tunnel-routed handlers on
    // EpisodesPopupBorder catch the press / release before the
    // ScrollBar's own handlers run — same pattern the VolumeSlider
    // uses with _volumeDragging.
    private bool _episodesPopupPointerPressed;

    // Wired from the MainWindow constructor. Tunnel routing means we
    // see the press / release before the ScrollBar thumb (or any other
    // child) handles it, so the flag flips reliably even when the
    // ScrollBar captures the pointer on the press. Same recipe as
    // VolumeSlider in the constructor.
    internal void HookEpisodesPopupDragHandlers()
    {
        EpisodesPopupBorder.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(EpisodesPopupBorder).Properties.IsLeftButtonPressed)
            {
                _episodesPopupPointerPressed = true;
                _episodesPopupHideTimer?.Stop();
            }
        }, RoutingStrategies.Tunnel);
        EpisodesPopupBorder.AddHandler(InputElement.PointerReleasedEvent, (_, _) =>
        {
            if (!_episodesPopupPointerPressed) return;
            _episodesPopupPointerPressed = false;
            // Re-evaluate close: the PointerExited that may have fired
            // mid-drag was canceled by the pressed flag, so we owe a
            // close-schedule if the cursor isn't currently over the
            // popup or the icon. If it IS, pointer-entered already
            // kept things open and there's nothing to do.
            if (!_pointerOverEpisodesBtn && !_pointerOverEpisodesPopup)
                ScheduleEpisodesPopupClose();
        }, RoutingStrategies.Tunnel);
    }

    private void EpisodesBtn_PointerEntered(object? sender, PointerEventArgs e)
    {
        _pointerOverEpisodesBtn = true;
        _episodesPopupHideTimer?.Stop();
        _ = ShowEpisodesPopupAsync();
    }

    private void EpisodesBtn_PointerExited(object? sender, PointerEventArgs e)
    {
        _pointerOverEpisodesBtn = false;
        ScheduleEpisodesPopupClose();
    }

    private void EpisodesPopupContent_PointerEntered(object? sender, PointerEventArgs e)
    {
        _pointerOverEpisodesPopup = true;
        _episodesPopupHideTimer?.Stop();
    }

    private void EpisodesPopupContent_PointerExited(object? sender, PointerEventArgs e)
    {
        _pointerOverEpisodesPopup = false;
        ScheduleEpisodesPopupClose();
    }

    // Stops a click on empty popup space from bubbling out to the
    // VideoOverlayRoot — without this, clicks on the popup background
    // could trigger the single-tap timer that toggles play/pause.
    // Tapped + DoubleTapped are separate synthesized routed events
    // from PointerPressed: handling PointerPressed alone doesn't stop
    // the gesture recogniser from raising Tapped, which is what
    // VideoOverlayRoot.Tapped → OnVideoTapped picks up. So handle
    // both the underlying pointer event AND the high-level gestures.
    private void EpisodesPopupContent_PointerPressed(object? sender, PointerPressedEventArgs e)
        => e.Handled = true;

    private void EpisodesPopupContent_Tapped(object? sender, TappedEventArgs e)
        => e.Handled = true;

    // Mirrors the volume popup — short delay so the user can move the
    // pointer from the button to the popup body without the popup
    // snapping shut between the two.
    private void ScheduleEpisodesPopupClose()
    {
        _episodesPopupHideTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _episodesPopupHideTimer.Tick -= OnEpisodesPopupHideTick;
        _episodesPopupHideTimer.Tick += OnEpisodesPopupHideTick;
        _episodesPopupHideTimer.Stop();
        _episodesPopupHideTimer.Start();
    }

    private void OnEpisodesPopupHideTick(object? sender, EventArgs e)
    {
        _episodesPopupHideTimer?.Stop();
        if (_pointerOverEpisodesBtn || _pointerOverEpisodesPopup) return;
        // Drag in progress (scrollbar thumb, ItemsControl item, etc.)
        // — wait for release before closing. The window-level
        // PointerReleased handler reschedules the close once the
        // press finishes.
        if (_episodesPopupPointerPressed) return;
        EpisodesPopup.IsOpen = false;
        // Drop the sticky hover scale on the Episodes icon now that
        // its popup is gone.
        EpisodesBtn?.Classes.Set("popup-open", false);
        // Reset back to the episode-list view for the next open, AND
        // drop the manually-navigated season so the popup re-opens on
        // the season the user is actually watching. Without this, a
        // navigation to e.g. Season 2 would persist across closes —
        // hovering the button later would still show Season 2 even
        // when the user was on Season 1.
        ShowEpisodeListView();
        _episodesPopupDisplayedSeason = null;
        // Stop the loading spinner so its INFINITE rotation animation
        // doesn't keep invalidating against a hidden target between
        // hovers.
        SetEpisodesPopupSpinner(false);
        // Restore the timeline row that ShowEpisodesPopupAsync hid
        // while the popup was up (mirrors the volume popup's pattern).
        // Cross-check against the other hover popups so we don't
        // re-show the scrub bar while volume / next-episode is still up.
        if (TimelineRow is not null && !IsAnyTransportPopupOpen())
            TimelineRow.IsVisible = true;
    }

    /// <summary>
    /// Pop the popup open and populate it from the cached series data.
    /// If the prefetch (kicked off in ResetEpisodesPopupCache when the
    /// new series started playing) is still in flight, populate as
    /// soon as it lands via the popup-open check at the end of
    /// FetchAndCacheSeriesAsync — the popup just shows blank for that
    /// brief window instead of a "Loading…" beat.
    /// </summary>
    private System.Threading.Tasks.Task ShowEpisodesPopupAsync()
    {
        if (_currentVideoResult is not { } current)
            return System.Threading.Tasks.Task.CompletedTask;
        if (string.IsNullOrEmpty(current.SeriesPageUrl))
            return System.Threading.Tasks.Task.CompletedTask;

        // Already open — re-hovering EpisodesBtn (e.g. user moved
        // pointer from popup back over the icon) shouldn't reset the
        // current view to the episode list. Preserve whatever the user
        // navigated to (season list via the back arrow). The view
        // resets to episode-list only on a fresh open after close —
        // OnEpisodesPopupHideTick clears _episodesPopupDisplayedSeason
        // and calls ShowEpisodeListView() at close time, so the next
        // open starts on the episode-list view as expected.
        if (EpisodesPopup.IsOpen)
            return System.Threading.Tasks.Task.CompletedTask;

        // Hard-close sibling popups (volume / next-episode) so a fast
        // pointer sweep doesn't leave them lingering during the 120 ms
        // hide grace.
        CloseSiblingTransportPopups(TransportPopupEpisodes);
        EpisodesPopup.IsOpen = true;
        // Sticky hover scale on the Episodes icon while the popup is
        // up — without this, moving the pointer from the icon onto the
        // popup body would deflate the icon back to idle even though
        // the popup it just triggered is still showing.
        EpisodesBtn?.Classes.Set("popup-open", true);
        ShowEpisodeListView();
        if (TimelineRow is not null) TimelineRow.IsVisible = false;
        if (_episodesPopupDisplayedSeason is null
            && current.SeasonNumber is { } sn)
        {
            _episodesPopupDisplayedSeason = sn;
        }

        var seriesUrl = current.SeriesPageUrl!;
        var cacheHit = _episodesPopupSeasons is not null
            && string.Equals(_episodesPopupCachedFor, seriesUrl, StringComparison.OrdinalIgnoreCase);
        if (cacheHit)
        {
            SetEpisodesPopupSpinner(false);
            PopulateEpisodeList();
            return System.Threading.Tasks.Task.CompletedTask;
        }

        // No cache yet — clear any stale rendering and show a centered
        // spinner so the popup never looks blank-and-empty while the
        // fetch is in flight. The spinner stays up until the fetch
        // completes (PopulateEpisodeList hides it) or fails (the
        // failure path in FetchAndCacheSeriesAsync hides it too).
        // Prefetch may already be in flight from
        // ResetEpisodesPopupCache; if so, just wait for its result.
        EpisodesPopupSeasonLabel.Text = "";
        EpisodesPopupEpisodeList.ItemsSource = Array.Empty<EpisodeRow>();
        EpisodesPopupSeasonList.ItemsSource = Array.Empty<SeasonItem>();
        SetEpisodesPopupSpinner(true);
        if (!string.Equals(_episodesPopupCachedFor, seriesUrl, StringComparison.OrdinalIgnoreCase))
        {
            _ = FetchAndCacheSeriesAsync(seriesUrl);
        }
        return System.Threading.Tasks.Task.CompletedTask;
    }

    // Class-gated spinner control — same pattern as the captcha
    // overlay's spinner. Adding the .spinning class while invisible
    // would leak INFINITE-animation invalidations against a hidden
    // target, so the class only goes on while the spinner is also
    // visible. When spinning, we also collapse the episode + season
    // views so only the spinner is on screen — otherwise the
    // back-arrow header would still paint stacked behind the
    // spinner with an empty list, which looks like a half-rendered
    // popup.
    private void SetEpisodesPopupSpinner(bool spinning)
    {
        EpisodesPopupLoadingSpinner.IsVisible = spinning;
        EpisodesPopupSpinnerPath.Classes.Set("spinning", spinning);
        if (spinning)
        {
            EpisodesPopupEpisodeView.IsVisible = false;
            EpisodesPopupSeasonView.IsVisible = false;
        }
        else
        {
            // Spinner going down — restore the episode view so the
            // populate / cache-hit path lands on a visible container.
            // Season view is opt-in via the back-arrow click and
            // stays hidden by default.
            EpisodesPopupEpisodeView.IsVisible = true;
        }
    }

    /// <summary>
    /// Single fetch path for the series data. Called from two places:
    ///   1. ResetEpisodesPopupCache, on new playback (background
    ///      prefetch — the result populates the cache before the user
    ///      ever hovers).
    ///   2. ShowEpisodesPopupAsync as a fallback if the popup is hovered
    ///      before any prefetch has run.
    /// On success, populates the cache and — if the popup is currently
    /// open — re-renders the list so the user sees episodes the
    /// instant the network response lands.
    /// </summary>
    private async System.Threading.Tasks.Task FetchAndCacheSeriesAsync(string seriesUrl)
    {
        if (string.IsNullOrEmpty(seriesUrl)) return;
        if (DataContext is not MainWindowViewModel vm) return;
        // If we're switching to a different URL, drop the old
        // seasons BEFORE updating _episodesPopupCachedFor —
        // otherwise the brief window between (cachedFor=NEW,
        // seasons=OLD) lets a concurrent hover hit the cache-hit
        // path and render the wrong series.
        if (!string.Equals(_episodesPopupCachedFor, seriesUrl, StringComparison.OrdinalIgnoreCase))
        {
            _episodesPopupSeasons = null;
        }
        // Mark the cache target as the requested URL up-front so a
        // late hover sees we've claimed this fetch and doesn't kick
        // off a duplicate one.
        _episodesPopupCachedFor = seriesUrl;
        _episodesPopupCts?.Cancel();
        _episodesPopupCts = new CancellationTokenSource();
        var ct = _episodesPopupCts.Token;

        SeriesInfo? info;
        try
        {
            info = await vm.BsTo.GetSeriesAsync(seriesUrl, ct);
        }
        catch
        {
            info = null;
        }
        // Any failure path — cancellation by ResetEpisodesPopupCache,
        // a thrown HttpRequestException, an empty / null response —
        // has to clear the "fetch claimed for this URL" marker if
        // it's still ours, otherwise the next hover sees the claim
        // standing and ShowEpisodesPopupAsync silently skips the
        // re-fetch. Without this, an intermittent bs.to / network
        // blip leaves the popup permanently blank until the user
        // navigates to a different series. The claim only stays
        // stuck if a same-URL re-fetch has taken over (rare; the
        // popup hover path doesn't kick same-URL re-fetches), in
        // which case we'd be clobbering its claim — guard against
        // that with the URL match check.
        if (ct.IsCancellationRequested || info is null || info.Seasons.Count == 0)
        {
            if (string.Equals(_episodesPopupCachedFor, seriesUrl, StringComparison.OrdinalIgnoreCase))
                _episodesPopupCachedFor = null;
            // Drop the spinner so the user isn't stuck staring at it
            // forever — the popup will just be empty, and a new hover
            // (now that the claim is cleared) will retry the fetch.
            if (EpisodesPopup.IsOpen) SetEpisodesPopupSpinner(false);
            return;
        }
        // Bail if the active series changed mid-fetch (user switched
        // videos), so we don't populate the popup with the wrong show.
        if (!string.Equals(_episodesPopupCachedFor, seriesUrl, StringComparison.OrdinalIgnoreCase))
            return;

        _episodesPopupSeasons = info.Seasons;
        // Cache just landed — re-evaluate the transport-bar
        // Next-Episode button so it picks up the new data even if
        // OnPlayRequested already fired before the fetch completed.
        UpdateNextEpisodeButtonState();

        // Late-arrival populate: if the popup happens to be open right
        // now (user hovered before prefetch finished), render now and
        // hide the spinner.
        if (EpisodesPopup.IsOpen)
        {
            SetEpisodesPopupSpinner(false);
            PopulateEpisodeList();
        }
    }

    /// <summary>
    /// Renders the displayed-season's episodes into the list, with the
    /// current episode flagged for the .playing-now highlight. Season
    /// label is updated alongside.
    /// </summary>
    private void PopulateEpisodeList()
    {
        if (_episodesPopupSeasons is null || _episodesPopupSeasons.Count == 0) return;

        var displayedSeasonNumber = _episodesPopupDisplayedSeason
            ?? _currentVideoResult?.SeasonNumber
            ?? _episodesPopupSeasons[0].Number;
        var season = _episodesPopupSeasons.FirstOrDefault(s => s.Number == displayedSeasonNumber)
                     ?? _episodesPopupSeasons[0];
        _episodesPopupDisplayedSeason = season.Number;
        EpisodesPopupSeasonLabel.Text = $"Season {season.Number}";

        var activeEpisodeUrl = _currentVideoResult?.PageUrl;
        // Episodes dict is keyed by language-stripped URL
        // (NormalizeEpisodeKey), so German and Subbed share one saved
        // progress slot — single lookup per row, no toggle / max
        // needed. UpdatePositionInMemory writes there live, so the
        // currently-playing row's bar stays in sync without a separate
        // top-level PositionMs fallback.
        var seriesUrl = _currentVideoResult?.SeriesPageUrl;
        var recent = !string.IsNullOrEmpty(seriesUrl) && DataContext is MainWindowViewModel vm
            ? vm.Recent.FindByUrlOrToggled(seriesUrl!)
            : null;
        var rows = season.Episodes
            .Select(ep =>
            {
                var progress = 0.0;
                if (recent is not null
                    && recent.Episodes.TryGetValue(BsToUrl.NormalizeEpisodeKey(ep.Url), out var p)
                    && p.LengthMs > 0)
                {
                    progress = Math.Clamp((double)p.PositionMs / p.LengthMs, 0.0, 1.0);
                }

                var isPlayingNow = !string.IsNullOrEmpty(activeEpisodeUrl)
                    && string.Equals(
                        BsToUrl.NormalizeEpisodeKey(activeEpisodeUrl!),
                        BsToUrl.NormalizeEpisodeKey(ep.Url),
                        StringComparison.OrdinalIgnoreCase);

                return new EpisodeRow
                {
                    Episode = ep,
                    Progress = progress,
                    IsPlayingNow = isPlayingNow,
                };
            })
            .ToList();
        EpisodesPopupEpisodeList.ItemsSource = rows;

        // Scroll the playing-now episode to the second row of the
        // viewport (one row above it visible as context). Drops back to
        // top when the playing episode is the first or absent — mirrors
        // the SeasonItem behavior in PopulateSeasonList. Posted to a
        // Loaded-priority dispatcher so containers exist by the time
        // we read their Bounds.
        var playingIndex = rows.FindIndex(r => r.IsPlayingNow);
        ScrollItemToSecond(EpisodesPopupEpisodeList, EpisodesPopupEpisodeScroll, playingIndex);
    }

    private void PopulateSeasonList()
    {
        if (_episodesPopupSeasons is null) return;
        var currentSeasonNumber = _currentVideoResult?.SeasonNumber;
        var items = _episodesPopupSeasons
            .Select(s => new SeasonItem
            {
                Number = s.Number,
                EpisodeCount = s.Episodes.Count,
                IsCurrentSeason = currentSeasonNumber is { } cn && cn == s.Number,
            })
            .ToList();
        EpisodesPopupSeasonList.ItemsSource = items;

        // Same "current item as second row" framing as the episode
        // list — the season the user is watching reads as the natural
        // anchor with its prior season visible above it as context.
        var currentIndex = items.FindIndex(s => s.IsCurrentSeason);
        ScrollItemToSecond(EpisodesPopupSeasonList, EpisodesPopupSeasonView, currentIndex);
    }

    // Sets the ScrollViewer's vertical offset so the (index - 1)th item
    // sits at the top of the viewport, putting the targeted item second
    // from top. Falls back to scroll-to-top when index <= 0 (target is
    // the first item or wasn't found) or when the previous container
    // hasn't been realized yet. The Bounds.Y read needs the ItemsControl
    // to have run a layout pass — Dispatcher.UIThread.Post with Loaded
    // priority defers the call until after Avalonia finishes the layout
    // triggered by the ItemsSource assignment.
    //
    // <paramref name="hideUntilSettled"/> hides the ScrollViewer
    // synchronously (Opacity=0) until the deferred scroll lands, then
    // restores it. Without this, the first render frame shows the list
    // at offset 0 before the Loaded-priority callback applies the
    // target offset — a visible "scroll snap" flash on opens that's
    // jarring even when it lasts a frame or two. With it, the user
    // never sees the unscrolled state. Default false because the in-
    // video Episodes popup doesn't need it (its open/close animation
    // already covers the gap); the episode-picker MODAL passes true
    // because its open is instant on cache hits and the snap is
    // visible there.
    private static void ScrollItemToSecond(
        ItemsControl list, ScrollViewer scroll, int index,
        bool hideUntilSettled = false)
    {
        if (hideUntilSettled) scroll.Opacity = 0;
        Dispatcher.UIThread.Post(() =>
        {
            if (index <= 0)
            {
                scroll.Offset = new Avalonia.Vector(scroll.Offset.X, 0);
                if (hideUntilSettled) scroll.Opacity = 1;
                return;
            }
            var prev = list.ContainerFromIndex(index - 1) as Avalonia.Controls.Control;
            if (prev is null)
            {
                scroll.Offset = new Avalonia.Vector(scroll.Offset.X, 0);
                if (hideUntilSettled) scroll.Opacity = 1;
                return;
            }
            scroll.Offset = new Avalonia.Vector(scroll.Offset.X, prev.Bounds.Y);
            if (hideUntilSettled) scroll.Opacity = 1;
        }, DispatcherPriority.Loaded);
    }

    private void EpisodesPopupBack_Click(object? sender, RoutedEventArgs e)
    {
        // Flip to the season-list view. The back arrow only exists on
        // the episode-list view, so we don't need a return path here —
        // clicking a season takes the user back to the episode list.
        PopulateSeasonList();
        ShowSeasonListView();
    }

    private void EpisodesPopupSeason_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SeasonItem item }) return;
        _episodesPopupDisplayedSeason = item.Number;
        PopulateEpisodeList();
        ShowEpisodeListView();
    }

    private void EpisodesPopupEpisode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EpisodeRow row }) return;
        if (_currentVideoResult is not { } current) return;
        if (string.IsNullOrEmpty(current.SeriesPageUrl)) return;
        if (DataContext is not MainWindowViewModel vm) return;

        var ep = row.Episode;
        // Same-episode click → just close the popup. Restarting the
        // current episode would force a captcha + reload chain for no
        // user-visible win.
        if (string.Equals(current.PageUrl, ep.Url, StringComparison.OrdinalIgnoreCase))
        {
            EpisodesPopup.IsOpen = false;
            return;
        }

        // Find the season the picked episode belongs to so the new
        // VideoResult carries the right S/E numbers downstream
        // (Recent's last-episode write keys off these).
        var seasonNumber = 0;
        if (_episodesPopupSeasons is not null)
        {
            foreach (var s in _episodesPopupSeasons)
            {
                if (s.Episodes.Contains(ep)) { seasonNumber = s.Number; break; }
            }
        }

        // Build the episode-flavoured VideoResult, mirroring what the
        // episode picker modal produces for its EpisodePickerEpisode_Click
        // handler. bs.to playback always goes through the captcha
        // overlay — same downstream as a picker click.
        var episodeResult = new VideoResult
        {
            Title = ep.Title,
            Source = current.Source,
            PageUrl = ep.Url,
            ThumbnailUrl = current.ThumbnailUrl ?? current.SeriesThumbnailUrl,
            Year = current.Year,
            Duration = current.Duration,
            Kind = VideoKind.Movie,
            SeriesPageUrl = current.SeriesPageUrl,
            SeriesTitle = current.SeriesTitle,
            SeriesThumbnailUrl = current.SeriesThumbnailUrl ?? current.ThumbnailUrl,
            SeasonNumber = seasonNumber,
            EpisodeNumber = ep.Number,
        };

        // Resume position lookup. Episodes dict is keyed by language-
        // stripped URL (NormalizeEpisodeKey) and UpdatePositionInMemory
        // keeps it live, so a single lookup covers both saved-from-prior-
        // session and cross-language carry-over from the currently
        // playing episode.
        var resumeMs = 0L;
        var recent = vm.Recent.FindByUrlOrToggled(current.SeriesPageUrl!);
        if (recent is not null
            && recent.Episodes.TryGetValue(BsToUrl.NormalizeEpisodeKey(ep.Url), out var p))
        {
            resumeMs = p.PositionMs;
        }

        EpisodesPopup.IsOpen = false;
        // Pause the current episode BEFORE the captcha overlay opens
        // so audio drops out the moment the user commits — without
        // this, the previous episode keeps playing under the captcha
        // until the new stream lands. _player.Pause() in LibVLC
        // TOGGLES, so we check the state first; otherwise picking an
        // episode while already paused would resume the old one for
        // the second or two before the captcha resolves the new one.
        // Same recipe as the Next-Episode button.
        if (_player.State == LibVLCSharp.Shared.VLCState.Playing)
        {
            try { _player.Pause(); } catch { /* defensive */ }
        }
        OpenBstoCaptchaOverlay(episodeResult, resumeMs);
    }

    private void ShowEpisodeListView()
    {
        if (EpisodesPopupEpisodeView is not null)
            EpisodesPopupEpisodeView.IsVisible = true;
        if (EpisodesPopupSeasonView is not null)
            EpisodesPopupSeasonView.IsVisible = false;
    }

    private void ShowSeasonListView()
    {
        if (EpisodesPopupEpisodeView is not null)
            EpisodesPopupEpisodeView.IsVisible = false;
        if (EpisodesPopupSeasonView is not null)
            EpisodesPopupSeasonView.IsVisible = true;
    }

    /// <summary>
    /// Called from OnPlayRequested when a new media starts. Drops the
    /// cached series data on a series change AND fires off a background
    /// prefetch of the new series's episode list — by the time the user
    /// hovers the Episodes button (after the captcha + load chain) the
    /// cache is already populated, so the popup opens straight to the
    /// list without a "Loading…" beat. Same-series replays reuse the
    /// cache and any in-flight prewarm fetch.
    /// </summary>
    internal void ResetEpisodesPopupCache(VideoResult source)
    {
        EpisodesPopup.IsOpen = false;
        var newSeriesUrl = source.SeriesPageUrl;
        var seriesChanged = !string.Equals(
            _episodesPopupCachedFor, newSeriesUrl, StringComparison.OrdinalIgnoreCase);
        if (seriesChanged)
        {
            // Different series (or no prior fetch) — cancel any
            // in-flight fetch from a previous click and start fresh.
            _episodesPopupCts?.Cancel();
            _episodesPopupCts = null;
            _episodesPopupSeasons = null;
            _episodesPopupCachedFor = null;
            _episodesPopupDisplayedSeason = null;
            if (!string.IsNullOrEmpty(newSeriesUrl))
                _ = FetchAndCacheSeriesAsync(newSeriesUrl!);
        }
        else
        {
            // Same series as a click-time prewarm — keep the cache
            // AND the in-flight fetch (don't cancel CTS). If the
            // prewarm completed already we hit the cache instantly;
            // if it's still running, the popup spinner bridges the
            // remaining wait. Just refresh which season the popup
            // defaults to.
            _episodesPopupDisplayedSeason = source.SeasonNumber;
        }
    }

    // Hover-state bookkeeping for the Next-Episode popup. Mirrors the
    // EpisodesPopup pattern (button + popup body, short grace timer
    // so the pointer can travel from one to the other without the
    // popup snapping shut between them).
    private bool _pointerOverNextEpisodeBtn;
    private bool _pointerOverNextEpisodePopup;
    private DispatcherTimer? _nextEpisodePopupHideTimer;

    private void NextEpisodeBtn_PointerEntered(object? sender, PointerEventArgs e)
    {
        _pointerOverNextEpisodeBtn = true;
        _nextEpisodePopupHideTimer?.Stop();
        // Don't show the popup when the button is disabled (last
        // episode of last season) — there's nothing meaningful to
        // surface, and showing the "Next episode" header alone reads
        // as a stale state. The user said the disabled icon should
        // just be greyed out.
        if (NextEpisodeBtn?.IsEnabled != true) return;
        ShowNextEpisodePopup();
    }

    private void NextEpisodeBtn_PointerExited(object? sender, PointerEventArgs e)
    {
        _pointerOverNextEpisodeBtn = false;
        ScheduleNextEpisodePopupClose();
    }

    private void NextEpisodePopupContent_PointerEntered(object? sender, PointerEventArgs e)
    {
        _pointerOverNextEpisodePopup = true;
        _nextEpisodePopupHideTimer?.Stop();
    }

    private void NextEpisodePopupContent_PointerExited(object? sender, PointerEventArgs e)
    {
        _pointerOverNextEpisodePopup = false;
        ScheduleNextEpisodePopupClose();
    }

    // Stops a click on the popup body from bubbling to the
    // VideoOverlayRoot single-tap handler — same pattern as
    // EpisodesPopupContent_PointerPressed.
    private void NextEpisodePopupContent_PointerPressed(object? sender, PointerPressedEventArgs e)
        => e.Handled = true;

    private void NextEpisodePopupContent_Tapped(object? sender, TappedEventArgs e)
        => e.Handled = true;

    private void ScheduleNextEpisodePopupClose()
    {
        _nextEpisodePopupHideTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _nextEpisodePopupHideTimer.Tick -= OnNextEpisodePopupHideTick;
        _nextEpisodePopupHideTimer.Tick += OnNextEpisodePopupHideTick;
        _nextEpisodePopupHideTimer.Stop();
        _nextEpisodePopupHideTimer.Start();
    }

    private void OnNextEpisodePopupHideTick(object? sender, EventArgs e)
    {
        _nextEpisodePopupHideTimer?.Stop();
        if (_pointerOverNextEpisodeBtn || _pointerOverNextEpisodePopup) return;
        NextEpisodePopup.IsOpen = false;
        // Drop the sticky hover scale on the next-episode icon.
        NextEpisodeBtn?.Classes.Set("popup-open", false);
        // Only restore the timeline row if no OTHER hover popup is
        // still open — otherwise this re-shows the scrub bar behind
        // a still-open volume / episodes popup. Each popup owns the
        // scrub-bar stash on its own surface but defers the restore
        // to whichever hide tick is the genuine last one out.
        if (TimelineRow is not null && !IsAnyTransportPopupOpen())
            TimelineRow.IsVisible = true;
    }

    private void ShowNextEpisodePopup()
    {
        // Hard-close any sibling popup so a fast pointer sweep
        // between transport-bar icons doesn't leave the previous
        // popup visible during its 120 ms hide grace.
        CloseSiblingTransportPopups(TransportPopupNextEpisode);
        // Idempotent — re-running the body when the popup is already
        // open is what re-asserts TimelineRow.IsVisible=false in case
        // another popup's hide tick (volume / episodes) fired in the
        // grace window and reset it. Without this re-assertion the
        // scrub bar stays visible behind the small next-episode popup.
        NextEpisodePopup.IsOpen = true;
        // Sticky hover scale on the next-episode icon while the popup
        // is up; removed in OnNextEpisodePopupHideTick.
        NextEpisodeBtn?.Classes.Set("popup-open", true);
        if (TimelineRow is not null) TimelineRow.IsVisible = false;
    }

    // True when ANY of the transport-bar hover popups (volume /
    // episodes / next-episode) is currently open. Each popup's hide
    // tick consults this before flipping TimelineRow back to visible
    // — without the cross-check, a fast pointer move from one popup
    // to another would have the first popup's hide tick fire mid-way
    // through the second popup's lifetime and re-show the scrub bar.
    private bool IsAnyTransportPopupOpen() =>
        (VolumePopup?.IsOpen == true)
        || (EpisodesPopup?.IsOpen == true)
        || (NextEpisodePopup?.IsOpen == true);

    // Stable identifiers for which sibling popup is being shown.
    // Used by CloseSiblingTransportPopups so each popup's show path
    // can hard-close its peers without the 120 ms grace lingering —
    // a fast pointer sweep across volume / episodes / next-episode
    // otherwise leaves the previous popup visible on top of the new
    // one until its hide timer ticks. Hard-close also stops the
    // timer so a deferred tick can't reach back and toggle state on
    // a sibling that's no longer the active popup.
    private const int TransportPopupVolume = 0;
    private const int TransportPopupEpisodes = 1;
    private const int TransportPopupNextEpisode = 2;

    private void CloseSiblingTransportPopups(int except)
    {
        if (except != TransportPopupVolume
            && VolumePopup is not null && VolumePopup.IsOpen)
        {
            _volumePopupHideTimer?.Stop();
            VolumePopup.IsOpen = false;
            MuteBtn?.Classes.Set("popup-open", false);
        }
        if (except != TransportPopupEpisodes
            && EpisodesPopup is not null && EpisodesPopup.IsOpen)
        {
            _episodesPopupHideTimer?.Stop();
            EpisodesPopup.IsOpen = false;
            EpisodesBtn?.Classes.Set("popup-open", false);
            // Reset the episodes popup state the same way
            // OnEpisodesPopupHideTick does — back to the episode-list
            // view, drop the manually-navigated season, stop the
            // spinner. Without this a re-open after a forced close
            // would land on whatever sub-view the user had navigated
            // to before.
            ShowEpisodeListView();
            _episodesPopupDisplayedSeason = null;
            SetEpisodesPopupSpinner(false);
        }
        if (except != TransportPopupNextEpisode
            && NextEpisodePopup is not null && NextEpisodePopup.IsOpen)
        {
            _nextEpisodePopupHideTimer?.Stop();
            NextEpisodePopup.IsOpen = false;
            NextEpisodeBtn?.Classes.Set("popup-open", false);
        }
    }

    /// <summary>
    /// Resolves the next sequential episode from the cached season
    /// list. Walks: next episode in the current season →
    /// first episode of the next season → null. Used by both the
    /// transport-bar Next-Episode button and its enabled-state
    /// updater. Returns null when there's no cached season data
    /// (fetch hasn't landed yet) or when the user is on the very
    /// last episode of the very last season — both paths leave the
    /// button disabled.
    /// </summary>
    private (SeriesSeason Season, SeriesEpisode Episode)? ResolveNextEpisode(
        int? currentSeasonNumber, int? currentEpisodeNumber)
    {
        if (_episodesPopupSeasons is null || _episodesPopupSeasons.Count == 0) return null;
        if (currentSeasonNumber is not int seasonNum) return null;
        if (currentEpisodeNumber is not int episodeNum) return null;

        // Order by Number so jump-to-next-season uses the show's
        // canonical sequence (Season 0 specials sit before Season 1
        // when present, and the next-episode jump from S0Eend would
        // land on S1E1 — same convention DefaultStartSeason uses).
        var seasons = _episodesPopupSeasons.OrderBy(s => s.Number).ToList();
        var seasonIdx = seasons.FindIndex(s => s.Number == seasonNum);
        if (seasonIdx < 0) return null;
        var current = seasons[seasonIdx];

        var episodeIdx = -1;
        for (int i = 0; i < current.Episodes.Count; i++)
        {
            if (current.Episodes[i].Number == episodeNum) { episodeIdx = i; break; }
        }
        if (episodeIdx < 0) return null;

        // Same-season next.
        if (episodeIdx + 1 < current.Episodes.Count)
            return (current, current.Episodes[episodeIdx + 1]);

        // First-of-next-season fallback. Walk forward in case a season
        // ends up empty (rare but the list does contain Season 0 entries
        // that could be empty for shows with no specials posted yet).
        for (int s = seasonIdx + 1; s < seasons.Count; s++)
        {
            if (seasons[s].Episodes.Count > 0)
                return (seasons[s], seasons[s].Episodes[0]);
        }
        return null;
    }

    /// <summary>
    /// Re-evaluates the Next-Episode button's IsEnabled AND its
    /// hover-tooltip subtitle based on the current playback target
    /// and the cached season list. Called from OnPlayRequested
    /// (current episode changed) and from FetchAndCacheSeriesAsync /
    /// PopulateEpisodesPopupCache (cache just landed) so the button
    /// picks up data whichever lands first.
    /// </summary>
    internal void UpdateNextEpisodeButtonState()
    {
        if (NextEpisodeBtn is null) return;
        (SeriesSeason Season, SeriesEpisode Episode)? next = null;
        if (_currentVideoResult is { } current
            && !string.IsNullOrEmpty(current.SeriesPageUrl))
        {
            next = ResolveNextEpisode(current.SeasonNumber, current.EpisodeNumber);
        }
        NextEpisodeBtn.IsEnabled = next is not null;

        // Popup subtitle line: "S01E12 · Episode title" when there
        // IS a next episode. When there isn't, also force the popup
        // closed in case it was already open while the cache was
        // updating across the season boundary — would otherwise
        // briefly show a stale subtitle until the user moves the
        // pointer away.
        if (NextEpisodeSubtitle is not null)
        {
            if (next is { } n)
            {
                NextEpisodeSubtitle.Text =
                    $"S{n.Season.Number:D2}E{n.Episode.Number:D2} · {n.Episode.Title}";
            }
            else
            {
                NextEpisodeSubtitle.Text = "";
                if (NextEpisodePopup is not null && NextEpisodePopup.IsOpen)
                    NextEpisodePopup.IsOpen = false;
            }
        }
    }

    private void NextEpisode_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentVideoResult is not { } current) return;
        var next = ResolveNextEpisode(current.SeasonNumber, current.EpisodeNumber);
        if (next is null) return;

        // Pause current playback BEFORE the captcha overlay opens so
        // the audio drops out the moment the user commits — without
        // this, the previous episode keeps playing under the captcha
        // until the new stream lands. _player.Pause() in LibVLC
        // TOGGLES, not pauses, so we check the state first;
        // otherwise clicking Next-Episode while already paused would
        // resume the old episode for the second or two before the
        // new stream is ready.
        if (_player.State == LibVLCSharp.Shared.VLCState.Playing)
        {
            try { _player.Pause(); } catch { /* defensive */ }
        }

        // Build the next-episode VideoResult. Carry series context
        // forward (SeriesPageUrl / SeriesTitle / SeriesThumbnailUrl)
        // so OnPlayRequested keys Recent / MyList by the show, not
        // the individual episode URL — same shape EpisodePicker uses
        // when the user picks a row.
        var nextVr = new VideoResult
        {
            Title = next.Value.Episode.Title,
            Source = current.Source,
            PageUrl = next.Value.Episode.Url,
            ThumbnailUrl = current.SeriesThumbnailUrl ?? current.ThumbnailUrl,
            Year = current.Year,
            Duration = current.Duration,
            Kind = VideoKind.Movie,
            SeriesPageUrl = current.SeriesPageUrl,
            SeriesTitle = current.SeriesTitle,
            SeriesThumbnailUrl = current.SeriesThumbnailUrl,
            SeasonNumber = next.Value.Season.Number,
            EpisodeNumber = next.Value.Episode.Number,
        };
        OpenBstoCaptchaOverlay(nextVr, 0);
    }

    /// <summary>
    /// Click-time prewarm: kicks off the series fetch as soon as the
    /// user presses Start playing / Continue playing / a search-result
    /// resume button. The 5-10 s captcha + Vidmoly Turnstile chain
    /// gives the fetch plenty of headroom, so by the time the video
    /// player mounts and the user hovers the Episodes icon, the
    /// episode list is already cached and renders instantly. No-op
    /// if a fetch is already in flight or completed for this URL.
    /// </summary>
    internal void PrewarmEpisodesPopup(string? seriesPageUrl)
    {
        if (string.IsNullOrEmpty(seriesPageUrl)) return;
        // Same URL already claimed (or cache populated) — nothing to do.
        if (string.Equals(_episodesPopupCachedFor, seriesPageUrl, StringComparison.OrdinalIgnoreCase))
            return;
        _ = FetchAndCacheSeriesAsync(seriesPageUrl!);
    }

    /// <summary>
    /// Drops already-fetched series data straight into the popup
    /// cache without re-hitting bs.to. Used by
    /// StartFirstEpisodeOfSeriesAsync (which calls GetSeriesAsync to
    /// find S1E1) and the episode picker (which has loaded the same
    /// seasons for the picker UI) — both have the seasons in hand at
    /// click time and a separate prewarm would duplicate the
    /// round-trip.
    /// </summary>
    internal void PopulateEpisodesPopupCache(string? seriesPageUrl, IReadOnlyList<SeriesSeason>? seasons)
    {
        if (string.IsNullOrEmpty(seriesPageUrl)) return;
        if (seasons is null || seasons.Count == 0) return;
        _episodesPopupCachedFor = seriesPageUrl;
        _episodesPopupSeasons = seasons;
        // Cache just landed — see comment in FetchAndCacheSeriesAsync.
        UpdateNextEpisodeButtonState();
    }
}
