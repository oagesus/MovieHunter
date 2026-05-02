using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MovieHunter.Models;
using MovieHunter.Services;
using MovieHunter.ViewModels;

namespace MovieHunter.Views;

// Recently watched + My list card visuals and click handling. Shared
// between both panels because each card's data template uses the same
// Button.recent-card class — the Tag on the Button is either a
// RecentWatch or a MyListEntry, and the helpers below pivot on type.
public partial class MainWindow
{
    // Set true the moment a chevron / inline-action chip Click runs
    // on a Recent / MyList card. The card itself is also a Button —
    // and ButtonBase in Avalonia raises its OWN Click event on top
    // of the bubbled inner Click whenever a press + release pair
    // happens within its bounds (even when those bounds CONTAIN a
    // child Button that already fired). The bubbled event has
    // Handled=true (set by the chevron's handler) and is filtered
    // by the standard `if (e.Handled) return;` guard, but the
    // freshly raised parent-Click has Handled=false and would
    // otherwise fall through to RecentWatch_Click / MyListEntry_Click
    // and pull focus to playback / captcha. The dispatcher post on
    // the next tick clears the flag once the parent's freshly-raised
    // Click has already drained, so a real card click immediately
    // after still gets through.
    private bool _innerActionJustClicked;

    // Sets the suppression flag and schedules a clear on the next
    // dispatcher tick. Default priority is enough — by the time the
    // post runs, the synchronous raise of the parent-card's Click
    // has already drained on the stack above us.
    private void SuppressNextCardClick()
    {
        _innerActionJustClicked = true;
        Dispatcher.UIThread.Post(
            () => _innerActionJustClicked = false,
            DispatcherPriority.Default);
    }

    // ── Card click handlers ──────────────────────────────────────────
    // Search-result row's Play button: starts (or resumes) playback for
    // the result whose Tag points at the clicked button. Series results
    // open the episode picker overlay instead of starting playback —
    // the user picks a season + episode there, which re-enters this
    // class via EpisodePickerEpisode_Click.
    private void PlayResult_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: VideoResult vr }
            && DataContext is MainWindowViewModel vm)
        {
            if (vr.Kind == VideoKind.Series)
            {
                // PiP-restore shortcut: clicking Play on a series row
                // whose episode is currently in the mini player just
                // brings the full-size player back, same as movies.
                if (TryRestorePipForPageUrl(vr.PageUrl, sender, e)) return;
                vm.SelectedResult = vr;
                // FindByUrlOrToggled handles the cross-language case:
                // a search row's URL is German but the user's Recent
                // entry is keyed by the consolidated variant (could
                // be Subbed). Plain Find would miss → fall into the
                // "no watch history" branch → start S1E1 instead of
                // resuming. The toggle fallback resumes the actual
                // last-watched episode.
                var existing = vm.Recent.FindByUrlOrToggled(vr.PageUrl);
                if (existing is not null && !string.IsNullOrEmpty(existing.LastEpisodeUrl))
                {
                    // Have a watch history → resume the last-watched
                    // episode at its saved timestamp. Series position
                    // change deferred to the Playing event (see
                    // OnSeriesActuallyPlaying in MainWindow.Video.cs).
                    var resumeEpisode = existing.ToVideoResult();
                    // Prewarm the in-player Episodes popup cache while
                    // the user solves the captcha — by the time playback
                    // begins, the seasons should already be cached.
                    PrewarmEpisodesPopup(resumeEpisode.SeriesPageUrl ?? resumeEpisode.PageUrl);
                    OpenBstoCaptchaOverlay(resumeEpisode, existing.PositionMs);
                }
                else
                {
                    // No watch history yet — fetch the series and start
                    // the first episode of the first season. Falls back
                    // to opening the picker if the fetch fails so the
                    // user has a path forward instead of a dead button.
                    _ = StartFirstEpisodeOfSeriesAsync(vr);
                }
                return;
            }
            // Same movie as the one currently in PiP? Restore the
            // full-size player and resume instead of reloading the
            // stream — same shortcut Recently watched / My list use.
            if (TryRestorePipForPageUrl(vr.PageUrl, sender, e)) return;
            // Float existing entries to the top of both lists right
            // now so the user sees the reorder before the ~500 ms
            // stream-URL extraction completes. No-op for entries that
            // aren't in either list yet — they'll be inserted by
            // OnPlayRequested as part of normal play-start handling.
            vm.Recent.MoveToTopAndSave(vr);
            vm.MyList.MoveToTopAndSave(vr);
            // Resume from saved position if we've watched this before,
            // otherwise start from zero.
            var startMs = vm.Recent.FindByUrlOrToggled(vr.PageUrl)?.PositionMs ?? 0;
            vm.SelectedResult = vr;
            _ = vm.PlayResultAsync(vr, startMs);
        }
    }

    private void RecentWatch_Click(object? sender, RoutedEventArgs e)
    {
        // Defensive guard: if a nested action button (chevron / play /
        // plus) already handled the click, don't run the card-level
        // logic. e.Handled=true on those handlers should already
        // suppress the bubble, but Avalonia's Click is a routed
        // event with Direct strategy in some configurations and the
        // suppression isn't always honored — without this check, the
        // chevron click could fall through to OpenBstoCaptchaOverlay
        // (below), which is exactly the "I clicked the chevron but
        // got the captcha instead of the picker" bug.
        if (e.Handled) return;
        // Second-line defense for the freshly-raised parent-Click
        // case — see _innerActionJustClicked field doc.
        if (_innerActionJustClicked) return;
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
            var vr = rw.ToVideoResult();
            ApplyRecentPlayingHighlightNow();
            // bs.to episodes need a captcha-solving step we can't run
            // headless — route them through the embedded WebView
            // overlay instead of yt-dlp's /extract. The card position
            // is INTENTIONALLY not changed here for series; it stays
            // wherever the user was looking at, with the "Continue
            // playing" overlay applied via _pendingPlayingPageUrl, and
            // moves to the top of Recent / MyList only when VLC fires
            // its Playing event (see OnSeriesActuallyPlaying).
            if (IsBstoUrl(vr.PageUrl))
            {
                // Prewarm the in-player Episodes popup cache while
                // the user solves the captcha. SeriesPageUrl is the
                // series-level URL, falling back to the entry's own
                // URL when the series URL hasn't been resolved yet.
                PrewarmEpisodesPopup(vr.SeriesPageUrl ?? vr.PageUrl);
                OpenBstoCaptchaOverlay(vr, rw.PositionMs);
            }
            else
            {
                // Movie — keep the optimistic float-to-top; the click →
                // ~500 ms extract → OnPlayRequested round-trip is short
                // enough that immediate reorder feels right.
                vm.Recent.MoveToTopAndSave(vr);
                vm.MyList.MoveToTopAndSave(vr);
                _ = vm.PlayRecentAsync(rw);
            }
        }
        // Stop the routed Click from bubbling to the parent recent-card
        // when the inline play chip is the source — without this the
        // card's own RecentWatch_Click would fire a second time.
        e.Handled = true;
    }

    private static bool IsBstoUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.Contains("bs.to", StringComparison.OrdinalIgnoreCase);

    // First "real" season — Number >= 1. bs.to series sometimes list a
    // Season 0 (specials / OVAs) as Seasons[0], and the natural default
    // for both the auto-play-first-episode flow and the episode picker
    // is the first numbered season (S1), not the specials track. Falls
    // back to the literal first season only if the show has nothing
    // numbered (an edge case — most shows have at least a Season 1).
    private static SeriesSeason? DefaultStartSeason(IReadOnlyList<SeriesSeason> seasons)
    {
        for (var i = 0; i < seasons.Count; i++)
            if (seasons[i].Number >= 1) return seasons[i];
        return seasons.Count > 0 ? seasons[0] : null;
    }

    // Fetches the series and starts playback of S1E1 via the captcha
    // overlay. Used when the user clicks Play on a series search result
    // they've never watched before — instead of forcing them to the
    // picker, we go straight to the first episode. Falls back to the
    // picker if the fetch fails or the series has no episodes, so the
    // button always leads somewhere.
    private async System.Threading.Tasks.Task StartFirstEpisodeOfSeriesAsync(VideoResult series)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        // Surface a status while the /series fetch is in flight — the
        // round-trip can take a second or two and otherwise the click
        // looks like it did nothing.
        vm.Status = "Loading episodes…";
        SeriesInfo? info;
        try
        {
            info = await vm.BsTo.GetSeriesAsync(series.PageUrl, default);
        }
        catch
        {
            info = null;
        }
        if (info is null || info.Seasons.Count == 0)
        {
            // Picker has its own loading + error UI, so let it take over.
            OpenEpisodePicker(series, null);
            return;
        }
        // Reuse this fetch for the in-player Episodes popup so it's
        // already cached by the time the user starts hovering — same
        // payload, no duplicate /series round-trip.
        PopulateEpisodesPopupCache(series.PageUrl, info.Seasons);
        // Some bs.to series include a "Season 0" (specials / OVAs) as the
        // first entry — picking Seasons[0] blindly would land the user on
        // an OVA instead of the actual S1E1. Walk the list for the
        // first regular season (Number >= 1); fall back to Seasons[0]
        // only if the show has no numbered seasons at all.
        var s1 = DefaultStartSeason(info.Seasons);
        if (s1 is null || s1.Episodes.Count == 0)
        {
            OpenEpisodePicker(series, null);
            return;
        }
        var ep = s1.Episodes[0];
        var episodeResult = new VideoResult
        {
            Title = ep.Title,
            Source = series.Source,
            PageUrl = ep.Url,
            ThumbnailUrl = series.ThumbnailUrl,
            Year = series.Year,
            Duration = series.Duration,
            Kind = VideoKind.Movie,
            SeriesPageUrl = series.PageUrl,
            SeriesTitle = info.Title,
            SeriesThumbnailUrl = info.ThumbnailUrl ?? series.ThumbnailUrl,
            SeasonNumber = s1.Number,
            EpisodeNumber = ep.Number,
        };
        // Series position change deferred to the Playing event (see
        // OnSeriesActuallyPlaying in MainWindow.Video.cs).
        OpenBstoCaptchaOverlay(episodeResult, 0);
    }

    // Click on a card in the My-List panel — same flow as the
    // recently-watched click, except we have a MyListEntry to convert.
    // Picks the higher of the entry's own saved position and any
    // matching Recently-watched entry's position so playback always
    // resumes from the latest known offset, even if the user added the
    // movie to My-list AFTER they started watching it.
    private void MyListEntry_Click(object? sender, RoutedEventArgs e)
    {
        // Same defensive guard as RecentWatch_Click — see the
        // comment there. Stops a chevron / play / plus click from
        // also routing the card-level action when bubble suppression
        // doesn't hold.
        if (e.Handled) return;
        if (_innerActionJustClicked) return;
        if (sender is not Button { Tag: MyListEntry entry }
            || DataContext is not MainWindowViewModel vm) return;
        if (TryRestorePipForPageUrl(entry.PageUrl, sender, e)) return;
        _pendingPlayingPageUrl = string.IsNullOrEmpty(entry.PageUrl) ? null : entry.PageUrl;

        // bs.to entries can't go through yt-dlp's /extract — that's
        // what RecentWatch_Click already routes around. Same handling
        // here: if Recently-watched has a saved last-episode for this
        // series we resume it via the captcha overlay; otherwise the
        // user picks an episode in the picker first. Without this the
        // straight PlayResultAsync below failed extraction for every
        // bs.to entry in My-list.
        if (IsBstoUrl(entry.PageUrl))
        {
            // FindByUrlOrToggled (not plain Find) so we still hit the
            // consolidated Recent entry when the user added the show
            // to MyList in one language but watched it in the other —
            // entry.PageUrl could now key off the OTHER variant's URL
            // and a strict Find would silently miss, falling into the
            // "open picker" branch and looking like the resume button
            // is broken.
            var rw = vm.Recent.FindByUrlOrToggled(entry.PageUrl);
            if (rw is not null && !string.IsNullOrEmpty(rw.LastEpisodeUrl))
            {
                // Series — position change deferred to the Playing event
                // (see OnSeriesActuallyPlaying in MainWindow.Video.cs).
                var episodeVr = rw.ToVideoResult();
                ApplyRecentPlayingHighlightNow();
                // Prewarm the in-player Episodes popup cache while the
                // captcha runs.
                PrewarmEpisodesPopup(episodeVr.SeriesPageUrl ?? entry.PageUrl);
                OpenBstoCaptchaOverlay(episodeVr, rw.PositionMs);
            }
            else
            {
                // No prior watch state — match the search-result Play
                // button: fetch the series and start S1E1 directly via
                // the captcha overlay. Previously this opened the picker,
                // which made the My-list play / Start-playing button feel
                // inconsistent with the same action in the search tab.
                var seriesVr = new VideoResult
                {
                    Title = entry.Title,
                    Source = entry.Source,
                    PageUrl = entry.PageUrl,
                    ThumbnailUrl = entry.ThumbnailUrl,
                    Year = entry.Year,
                    Duration = entry.Duration,
                    Kind = VideoKind.Series,
                    IsInMyList = true,
                };
                _ = StartFirstEpisodeOfSeriesAsync(seriesVr);
            }
            e.Handled = true;
            return;
        }

        // Float to top in both lists immediately — same reasoning as
        // RecentWatch_Click: don't wait for the ~500 ms stream-URL
        // round-trip to reorder the grid.
        var vr = entry.ToVideoResult();
        vm.MyList.MoveToTopAndSave(vr);
        vm.Recent.MoveToTopAndSave(vr);
        ApplyRecentPlayingHighlightNow();
        var resumeMs = Math.Max(entry.PositionMs,
            vm.Recent.FindByUrlOrToggled(entry.PageUrl)?.PositionMs ?? 0);
        _ = vm.PlayResultAsync(vr, resumeMs);
        e.Handled = true;
    }

    // The chip on a recently-watched / search-result card — toggles
    // membership in MyList and syncs IsInMyList across every visible
    // surface so all chips referring to the same PageUrl flip together.
    private void ToggleMyList_Click(object? sender, RoutedEventArgs e)
    {
        // Suppress the parent card's freshly-raised Click (see
        // _innerActionJustClicked field doc) — same nesting issue
        // as the chevron, just on the my-list-toggle chip.
        SuppressNextCardClick();
        if (sender is not Button btn || DataContext is not MainWindowViewModel vm) return;
        VideoResult? source = btn.Tag switch
        {
            VideoResult v => v,
            // RecentWatch.ToVideoResult() returns the EPISODE for series
            // (PageUrl = LastEpisodeUrl), but MyList keys series entries
            // by the SERIES URL. Build a series-shape VideoResult so
            // Add lands the entry under the right key and the Title/
            // Thumbnail are the show's, not the last-watched episode's.
            RecentWatch rw => rw.IsSeries
                ? new VideoResult
                {
                    Title = rw.Title,
                    Source = rw.Source,
                    PageUrl = rw.PageUrl,
                    ThumbnailUrl = rw.ThumbnailUrl,
                    Year = rw.Year,
                    Duration = rw.Duration,
                    Kind = VideoKind.Series,
                    IsInMyList = rw.IsInMyList,
                }
                : rw.ToVideoResult(),
            MyListEntry m => m.ToVideoResult(),
            _ => null,
        };
        if (source is null || string.IsNullOrEmpty(source.PageUrl)) return;
        // If this movie is already in Recently watched, seed the new
        // My-list entry with the saved position so its progress bar (and
        // resume-from-saved-time) work immediately.
        var existingProgress = vm.Recent.Find(source.PageUrl);
        // Pin the currently-playing movie at index 0 so a fresh
        // addition slots in after it. Without this, adding a movie
        // while another one is playing demotes the playing one to
        // index 1 — the "currently watching" entry should stay on top.
        // Use RecentKey (not PageUrl) so a series episode currently
        // playing in PiP correctly resolves to its SERIES URL — that's
        // the key MyList stores, so PageUrl alone (the EPISODE URL) would
        // never match Items[0].PageUrl and the new entry would push the
        // currently-watched series down to index 1. Mirrors what
        // MyList.MoveToTopAndSave already does.
        var nowSaved = vm.MyList.Toggle(
            source,
            existingProgress?.PositionMs ?? 0,
            existingProgress?.LengthMs ?? 0,
            _currentVideoResult?.RecentKey);
        if (nowSaved)
        {
            // Newly-added MyList entry doesn't carry over the
            // last-watched-episode info (Toggle only takes
            // position/length). Walk the lists once now so a series
            // added after watching shows the right "S2E05 · Title"
            // subtitle on its My-list card right away.
            vm.MyList.SeedLastEpisodeFromRecent(vm.Recent);
        }
        SyncIsInMyList(source.PageUrl, nowSaved);
        e.Handled = true;
    }

    // The chevron-down chip on Recently-watched and My-list cards —
    // opens the episode picker for the bound entry. Clicking the
    // poster body or the play chip still resumes / starts playback;
    // this chip is the explicit "browse all episodes" entry point.
    // Visible only on series entries (IsSeries on both RecentWatch
    // and MyListEntry returns true for bs.to URLs).
    private void OpenEpisodes_Click(object? sender, RoutedEventArgs e)
    {
        // Mark the click as Handled UP FRONT, before any of the early
        // returns below. Without this, a degenerate Tag state (binding
        // not yet realised, virtualisation hot-swap, etc.) would fall
        // through to `return;` with Handled still false — the routed
        // Click then bubbles to the parent recent-card Button, which
        // fires RecentWatch_Click / MyListEntry_Click and pulls focus
        // away to playback / captcha. That's the intermittent "click
        // chevron, modal doesn't open, card overlay vanishes" bug.
        // Setting Handled here makes the chevron click consume itself
        // unconditionally; if no series ends up resolved we still
        // simply do nothing, which is correct.
        e.Handled = true;
        // Belt-and-braces guard for the parent-card's freshly-raised
        // Click — see _innerActionJustClicked field doc. e.Handled
        // alone isn't enough here because the parent ButtonBase
        // raises its OWN Click event with a fresh RoutedEventArgs,
        // so the bubbled-Handled state doesn't propagate.
        SuppressNextCardClick();
        if (sender is not Button btn) return;
        if (DataContext is not MainWindowViewModel vm) return;
        // Picker already open: ignore. Without this guard, a
        // double-fire of the chevron click (e.g. trackpad bounce, or
        // a re-entrant click while the just-opened overlay's pointer
        // routing hasn't settled) re-runs the open path on top of an
        // already-visible modal and produces the "card hover flickers,
        // modal never appears" symptom.
        if (EpisodePickerOverlay.IsVisible) return;

        // Pull display fields + base URL from whichever Tag type bound
        // to this row. Tag is `{Binding}` so it normally holds the
        // row's data object — but during virtualization / container
        // recycling the Tag can lag behind the visible row's actual
        // DataContext. Fall back to DataContext when Tag fails so a
        // stale Tag doesn't silently drop the click into the
        // baseUrl-empty early-return below (which would consume the
        // click via e.Handled=true but never open the modal — exactly
        // the "click does nothing" bug).
        var tagOrCtx = btn.Tag ?? btn.DataContext;
        string? baseUrl = null;
        var title = "";
        var source = "";
        string? thumbnailUrl = null;
        string? year = null;
        string? duration = null;
        var isInMyList = false;
        VideoResult? searchRowVrForChevron = null;

        if (tagOrCtx is VideoResult vr)
        {
            baseUrl = vr.PageUrl;
            title = vr.Title;
            source = vr.Source;
            thumbnailUrl = vr.ThumbnailUrl;
            year = vr.Year;
            duration = vr.Duration;
            isInMyList = vr.IsInMyList;
            searchRowVrForChevron = vr;
        }
        else if (tagOrCtx is RecentWatch rw)
        {
            baseUrl = rw.PageUrl;
            title = rw.Title;
            source = rw.Source;
            thumbnailUrl = rw.ThumbnailUrl;
            year = rw.Year;
            duration = rw.Duration;
            isInMyList = rw.IsInMyList;
        }
        else if (tagOrCtx is MyListEntry m)
        {
            baseUrl = m.PageUrl;
            title = m.Title;
            source = m.Source;
            thumbnailUrl = m.ThumbnailUrl;
            year = m.Year;
            duration = m.Duration;
            isInMyList = true;
        }

        if (string.IsNullOrEmpty(baseUrl)) return;

        // Resolve the consolidated Recent entry across both language
        // variants (since we now keep a single entry per show), then
        // derive the picker's effective URL from rw.LastEpisodeUrl —
        // whichever language the user actually played most recently
        // wins, regardless of which tab fired the click.
        var rwForLang = vm.Recent.FindByUrlOrToggled(baseUrl!);
        var resumeEpisodeUrl = rwForLang?.LastEpisodeUrl;
        var effectiveUrl = baseUrl!;
        if (rwForLang is not null
            && !string.IsNullOrEmpty(rwForLang.LastEpisodeUrl)
            && baseUrl!.Contains("bs.to", StringComparison.OrdinalIgnoreCase))
        {
            // Carry the LANGUAGE of the last-watched episode onto the
            // series URL we hand to the picker. Works for all three
            // variants (no-suffix / /de / /des / /en) because
            // GetLanguage returns the suffix code (or null for the
            // default-no-suffix form), and WithLanguage round-trips
            // it back as the matching series URL.
            effectiveUrl = BsToUrl.WithLanguage(baseUrl!, BsToUrl.GetLanguage(rwForLang.LastEpisodeUrl));
        }

        var series = new VideoResult
        {
            Title = title,
            Source = source,
            PageUrl = effectiveUrl,
            ThumbnailUrl = thumbnailUrl,
            Year = year,
            Duration = duration,
            Kind = VideoKind.Series,
            IsInMyList = isInMyList,
        };

        // Search-row chevron: rotate while the picker is open. The
        // synthetic VideoResult above isn't bound to the row's
        // chevron, so we flip the original vr's flag here and clear
        // it back to false in CloseEpisodePicker via the
        // _pickerOriginatingSearchRow tracking field.
        if (searchRowVrForChevron is not null)
        {
            searchRowVrForChevron.IsEpisodesPopupOpen = true;
            _pickerOriginatingSearchRow = searchRowVrForChevron;
        }
        else
        {
            _pickerOriginatingSearchRow = null;
        }

        // Mark the series card as the pending focus so the matching
        // Recent / MyList card keeps its accent border + scrim +
        // "Continue playing" overlay visible while the episode picker
        // is open. Cleared in the picker's cancel handlers; left set
        // when the user actually picks an episode (OnPlayRequested
        // clears it once playback starts). For search-row chevrons
        // there's no card to highlight on the search tab itself, but
        // the matching Recent / MyList card (if any) on other tabs
        // will pick this up via ApplyRecentPlayingHighlightNow.
        _pendingPlayingPageUrl = string.IsNullOrEmpty(series.PageUrl) ? null : series.PageUrl;
        ApplyRecentPlayingHighlightNow();
        // Defer the actual overlay-show to the next dispatcher tick
        // so the click's PointerPressed / PointerReleased / Click
        // routing fully settles before the modal's backdrop becomes
        // hit-testable. Showing the overlay synchronously inside the
        // click handler races the input pipeline: the still-in-flight
        // pointer events can land on the just-revealed backdrop's
        // PointerPressed handler, which fires CloseEpisodePicker and
        // wipes _pendingPlayingPageUrl — visible to the user as a
        // brief card-hover flicker with no modal ever appearing.
        Dispatcher.UIThread.Post(
            () => OpenEpisodePicker(series, resumeEpisodeUrl),
            DispatcherPriority.Background);
    }

    // The chip in the My-list panel — same as ToggleMyList_Click but
    // always removes (the panel only shows saved items).
    private void MyListRemove_Click(object? sender, RoutedEventArgs e)
    {
        // Suppress the parent card's freshly-raised Click — same
        // reasoning as ToggleMyList_Click / OpenEpisodes_Click.
        SuppressNextCardClick();
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

    // Pushes the matching Recent entry's "S02E05 · Title" subtitle AND
    // last-episode progress fraction onto each search-result VideoResult
    // so the bottom-aligned label + progress bar on the search row
    // reflect where the user left off. Movies and unwatched series get
    // null/0, which hides the bound elements via HasLastWatchedEpisode.
    // Wired to Results.CollectionChanged (after every search) and
    // Recent.Items.CollectionChanged (after a play upserts a new
    // entry) so the label appears without manual refresh.
    private void RefreshAllLastWatchedFromRecent()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        foreach (var vr in vm.Results)
        {
            // RecentKey = own URL on series-shell rows (Kind=Series).
            // FindMostRecentAcrossLanguages picks whichever bs.to
            // language variant (German vs German Subbed) was watched
            // most recently, so search rows reflect the user's latest
            // activity even when they're switching between language
            // variants of the same show.
            var rw = vm.Recent.FindMostRecentAcrossLanguages(vr.RecentKey);
            vr.LastWatchedEpisodeLabel = rw?.EpisodeSubtitle;
            vr.LastWatchedEpisodeProgress = rw?.Progress ?? 0.0;
        }
    }

    // Per-item PropertyChanged listener attached to every RecentWatch in
    // vm.Recent.Items. UpdatePositionAndSave mutates an existing entry's
    // LastEpisodeUrl/Title/SeasonNumber/EpisodeNumber in place (no list
    // shuffle), so neither Recent.Items.CollectionChanged nor a search
    // does — but the search-row label needs to reflect the new resume
    // target. Filtering on EpisodeSubtitle catches metadata changes
    // (the four setters all NotifyPropertyChangedFor it). Filtering on
    // Progress catches playback-position ticks so the search-row
    // progress bar live-updates while the user is watching in PiP and
    // browsing search at the same time. Targets only the matching
    // Results so the cost stays small even with frequent firings.
    private void OnRecentWatchPropertyChanged(
        object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RecentWatch.EpisodeSubtitle)
            && e.PropertyName != nameof(RecentWatch.Progress)) return;
        if (sender is not RecentWatch rw) return;
        if (DataContext is not MainWindowViewModel vm) return;
        var isLabelChange = e.PropertyName == nameof(RecentWatch.EpisodeSubtitle);
        // Match search rows whose RecentKey shares the same language-
        // stripped URL as rw.PageUrl — Recent consolidates to one
        // entry per show, but a search-result row may carry ANY of
        // the language variants' URLs (German default / /de / /des /
        // /en, whichever the search engine returned), and the
        // consolidated entry's PageUrl is just whichever variant won
        // the merge. SameLanguageStripped covers all three variants.
        var rwIsBsto = rw.PageUrl.Contains("bs.to", StringComparison.OrdinalIgnoreCase);
        foreach (var vr in vm.Results)
        {
            var matchesDirect = string.Equals(
                vr.RecentKey, rw.PageUrl, StringComparison.OrdinalIgnoreCase);
            var matchesStripped = !matchesDirect
                && rwIsBsto
                && BsToUrl.SameLanguageStripped(vr.RecentKey, rw.PageUrl);
            if (!matchesDirect && !matchesStripped) continue;
            // For language-stripped matches we still want to push the
            // freshest value: it could be the just-mutated rw OR a
            // separate entry on the row's own URL (whichever has the
            // more recent LastWatchedUtc). For direct matches the
            // just-mutated rw is always authoritative.
            RecentWatch source = rw;
            if (matchesStripped)
            {
                var ownUrlEntry = vm.Recent.Find(vr.RecentKey);
                if (ownUrlEntry is not null
                    && ownUrlEntry.LastWatchedUtc > rw.LastWatchedUtc)
                {
                    source = ownUrlEntry;
                }
            }
            if (isLabelChange) vr.LastWatchedEpisodeLabel = source.EpisodeSubtitle;
            else vr.LastWatchedEpisodeProgress = source.Progress;
        }
    }

    // ── Card hover / playing visual state ────────────────────────────
    // Title-strip text inside a card consumes its own pointer presses so
    // a click on the title/source doesn't bubble up to the parent card
    // and start playback. Clicks on the inline action chips are
    // unaffected (their own buttons handle them first).
    private void TitleStrip_PointerPressed(object? sender, PointerPressedEventArgs e)
        => e.Handled = true;

    // Hover visuals (accent border + scrim + Continue-playing badge)
    // are now driven entirely by Avalonia's :pointerover pseudo-class
    // via XAML styles in MainWindow.axaml — see the Style block near
    // Border.poster. The framework's own pointer-over state respects
    // pointer-capture transfers (clicking an inner chip keeps
    // :pointerover true on the parent), so the overlay stays solid
    // through chip clicks. These handlers are kept as no-ops because
    // the XAML attributes still wire to them; can be removed entirely
    // with a corresponding template edit if we want to simplify later.
    private void RecentCard_PointerEntered(object? sender, PointerEventArgs e) { }
    private void RecentCard_PointerExited(object? sender, PointerEventArgs e) { }

    // Toggles the .is-active and .actually-playing classes on the
    // card. Hover state is no longer touched here — that's :pointerover.
    //   .is-active        — card matches the active video (pending OR
    //                       actually playing). Drives the always-on
    //                       border / scrim / badge visibility regardless
    //                       of hover.
    //   .actually-playing — VLC has emitted Playing (frames are decoding).
    //                       Flips the badge text from "Continue playing"
    //                       to "Currently playing". For bs.to series this
    //                       is gated until the full captcha + hoster +
    //                       buffer chain completes; for movies the gap
    //                       between click and Playing event is small.
    private void ApplyRecentCardVisuals(Button card, bool hovered)
    {
        var pageUrl = card.Tag switch
        {
            RecentWatch rw => rw.PageUrl,
            MyListEntry me => me.PageUrl,
            _ => null,
        };
        var hasPageUrl = !string.IsNullOrEmpty(pageUrl);
        // Two independent state matches: did the user click / open
        // picker for THIS card (pendingMatch), or is THIS card the
        // video VLC is currently bound to (activeMatch). They can both
        // be true (e.g. user opens picker for the same show that's
        // already playing), or independently true on different cards
        // (show A in PiP while user opens picker for show B).
        // Cross-language fallback: card URL might be the German variant
        // while playback is the Subbed variant (or vice versa); same
        // logical show should still light up.
        var pendingMatch = hasPageUrl
            && CardUrlMatchesPlayback(pageUrl!, _pendingPlayingPageUrl);
        var activeMatch = hasPageUrl
            && CardUrlMatchesPlayback(pageUrl!, _currentVideoResult?.RecentKey);
        var isPending = pendingMatch || activeMatch;
        var isSeriesCard = IsCardForSeries(card);
        // "actually playing" must mean THIS card is what's decoding —
        // not just "some video is decoding". Series additionally
        // require VLC's Playing event to have fired (frames really
        // arriving), so the captcha + load chain reads as "Continue
        // playing" rather than "Currently playing". Movies don't gate
        // on the frame event since their click → frames gap is small.
        var actuallyPlaying = isSeriesCard
            ? (activeMatch && _isVideoActuallyPlaying)
            : (pendingMatch || activeMatch);
        card.Classes.Set("is-active", isPending);
        card.Classes.Set("actually-playing", actuallyPlaying);
    }

    // Search-results "currently playing" highlight. Walks vm.Results
    // and sets each VideoResult's IsCurrentlyPlaying to match whether
    // its PageUrl equals the active video's RecentKey. RecentKey
    // returns the series URL for series episodes and the page URL for
    // movies, so the comparison works for both flavours: a hdfilme
    // movie row matches its own URL, a bs.to series row matches the
    // series URL even when an episode is the actual active video.
    // Called from OnPlayRequested / Back / EndReached and on every
    // search-results refresh so the highlight stays accurate.
    private void SyncSearchResultsCurrentlyPlaying()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var key = _currentVideoResult?.RecentKey;
        // Cross-language fallback: a search row whose URL is one
        // language variant of the show should still light up when the
        // user is watching ANY other variant of the same show in PiP.
        // SameLanguageStripped covers all three (German default / /de
        // / /des / /en) — exact PageUrlEquals would only catch the
        // direct-URL case.
        var keyIsBsto = !string.IsNullOrEmpty(key)
            && key!.Contains("bs.to", StringComparison.OrdinalIgnoreCase);
        foreach (var vr in vm.Results)
        {
            vr.IsCurrentlyPlaying = !string.IsNullOrEmpty(key)
                && (PageUrlEquals(vr.PageUrl, key)
                    || (keyIsBsto && BsToUrl.SameLanguageStripped(vr.PageUrl, key)));
        }
    }

    // bs.to URL → series, otherwise movie. Used by the badge logic
    // to decide whether to defer the "Currently playing" flip until
    // VLC's Playing event lands.
    private static bool IsCardForSeries(Button card)
    {
        var pageUrl = card.Tag switch
        {
            RecentWatch rw => rw.PageUrl,
            MyListEntry me => me.PageUrl,
            _ => null,
        };
        return !string.IsNullOrEmpty(pageUrl)
            && pageUrl.Contains("bs.to", StringComparison.OrdinalIgnoreCase);
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
        // Compare against BOTH the pending click target AND the
        // currently-playing video. Previously the pending shadowed the
        // active (?? semantics), but that meant opening the episode
        // picker for show A while show B was in PiP would strip B's
        // "Currently playing" overlay. Using OR lets multiple cards
        // be highlighted independently — the pending one (picker-open
        // or just-clicked-play) shows "Continue playing", the active
        // one shows "Currently playing" (once frames are decoding).
        return CardUrlMatchesPlayback(pageUrl, _pendingPlayingPageUrl)
            || CardUrlMatchesPlayback(pageUrl, _currentVideoResult?.RecentKey);
    }

    // Card-URL ⇄ playback-URL match with cross-language fallback for
    // bs.to. The Recent / MyList card's stable PageUrl might be the
    // German variant while the active playback URL is the German
    // Subbed variant (or vice versa) — they're the same logical show
    // and the "currently playing" overlay should still light up.
    // Plain PageUrlEquals (exact ordinal compare) treats them as
    // unrelated, which is the bug.
    private static bool CardUrlMatchesPlayback(string cardUrl, string? playbackUrl)
    {
        if (string.IsNullOrEmpty(playbackUrl)) return false;
        if (PageUrlEquals(cardUrl, playbackUrl)) return true;
        if (!cardUrl.Contains("bs.to", StringComparison.OrdinalIgnoreCase)) return false;
        // Cross-language match: card's URL is one language variant
        // (no-suffix / /de / /des / /en), playback is another variant
        // of the same show — they must light up together.
        return BsToUrl.SameLanguageStripped(cardUrl, playbackUrl);
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

    // ── Visual-tree helpers used by the card visuals ─────────────────
    private static Border? FindBorderByClass(Control root, string className)
    {
        foreach (var d in root.GetVisualDescendants())
            if (d is Border b && b.Classes.Contains(className)) return b;
        return null;
    }

    // Generic helper — finds the first descendant Control that has the
    // given class. Used by the recent/my-list card visuals to locate
    // the playing-badge regardless of its concrete control type.
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
    private static IBrush ResolveAccentBrush(Control origin)
    {
        var variant = origin.ActualThemeVariant;
        string[] brushKeys = { "SystemAccentColorBrush", "AccentFillColorDefaultBrush" };
        foreach (var key in brushKeys)
        {
            if (origin.TryFindResource(key, variant, out var v) && v is IBrush b)
                return b;
        }
        string[] colorKeys = { "SystemAccentColor", "AccentFillColorDefault" };
        foreach (var key in colorKeys)
        {
            if (origin.TryFindResource(key, variant, out var v) && v is Color c)
                return new SolidColorBrush(c);
        }
        return new SolidColorBrush(Color.FromRgb(0xE5, 0x09, 0x14));
    }

    // Search-result row's "open in browser" button — launches the
    // result's page URL in the default browser. Quietly swallows
    // failures (browser unavailable, malformed URL etc.).
    private void OpenSource_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: VideoResult vr } && !string.IsNullOrWhiteSpace(vr.PageUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = vr.PageUrl,
                    UseShellExecute = true,
                });
            }
            catch
            {
                // swallow; browser launch failures are not fatal
            }
        }
    }
}
