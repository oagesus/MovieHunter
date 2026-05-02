using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MovieHunter.Models;
using MovieHunter.Services;
using MovieHunter.ViewModels;

namespace MovieHunter.Views;

// Episode-picker overlay. Driven from PlayResult_Click in Cards.cs when
// the clicked result has Kind = Series. Fetches the season + episode
// list async via BsToService, populates the season ComboBox, and lets
// the user click an episode to start playback (the click translates to
// a regular VideoResult with SeriesPageUrl set, so Recent / MyList key
// the entry by the series page URL).
public partial class MainWindow
{
    // The series result that opened the picker. Kept around so episode
    // clicks can pass series-context fields into the constructed
    // VideoResult (series title / thumbnail get carried into the
    // Recent entry via UpsertAndSave).
    private VideoResult? _pickerSeries;

    // The currently-displayed seasons + episodes. Populated by
    // OpenEpisodePickerAsync, consumed by the ComboBox SelectionChanged
    // handler (which swaps the ItemsControl source) and by the episode
    // click handler (to look up the parent season number).
    private IReadOnlyList<SeriesSeason> _pickerSeasons = Array.Empty<SeriesSeason>();

    // Last-watched episode info from Recent (if any). Used to pre-select
    // the matching season + scroll to / highlight the saved episode.
    private string? _pickerResumeEpisodeUrl;

    // Cancellation for in-flight episode fetches — closing or reopening
    // the picker cancels any pending /series request so a slow response
    // doesn't repopulate the UI after the user moved on.
    private CancellationTokenSource? _pickerCts;

    // Per-series cache of resolved TMDb poster URLs. Stores the empty
    // string for "we already searched and TMDb didn't have a match" —
    // negative caching, so reopening a series with no TMDb hit doesn't
    // re-spend a /search/tv call (six requests with the variant fallback)
    // every time. Keyed by series PageUrl since that's stable across
    // search-result re-builds. Lifetime is the static field's lifetime
    // (process), which matches the BsToService cache scope.
    private static readonly Dictionary<string, string> _tmdbPosterCache =
        new(StringComparer.OrdinalIgnoreCase);


    // The URL currently displayed in EpisodePickerThumb. Tracked here
    // so SetPickerThumbnail can skip its clear-then-set on no-op
    // assignments (repeat opens of the same series with the same
    // poster URL) — without this, the manual Source=null + SetSource
    // dance produced a one-frame dark flash even when AsyncImageLoader
    // had the bitmap cached. URL transitions (different show, or
    // initial-empty followed by resolved URL) still clear+set so the
    // previous poster doesn't leak under the new fetch.
    private string _currentPickerThumbUrl = string.Empty;

    // The series URL currently driving the picker's content. Initially
    // mirrors _pickerSeries.PageUrl, but mutates when the user switches
    // language (German ⇄ German Subbed via the EpisodePickerLanguageBox)
    // since each language is a separate URL on bs.to (`/des` suffix).
    // _pickerSeries.PageUrl is init-only so we can't update it in place;
    // tracking the live URL separately lets Recent lookups, episode-
    // click resume logic, and BsTo cache hits all key off the active
    // variant.
    private string? _pickerCurrentSeriesUrl;

    // Timestamp of the most recent overlay-show. Used by the backdrop
    // click handler to ignore PointerPressed events that arrive within
    // a short window after open — those are leftovers from the same
    // gesture (chevron click) that just opened the modal, and would
    // otherwise immediately close it. The "click did nothing" bug.
    private DateTime _overlayShownAt = DateTime.MinValue;

    // Suppresses the language ComboBox's SelectionChanged handler from
    // firing a refetch when OpenEpisodePicker programmatically sets
    // SelectedIndex during the initial open (matching the language
    // implied by the incoming URL). Without this guard, every modal
    // open would trigger a needless refetch + repopulate.
    private bool _suppressLanguageChange;

    // The single consolidated RecentWatch instance we subscribe to
    // for live-Progress updates of the picker rows. After the
    // language-duplicate merge there's only ONE Recent entry per
    // show, regardless of which variant the user is watching, so
    // resolving via FindByUrlOrToggled gives us the right reference
    // whether _pickerCurrentSeriesUrl is the German URL or the
    // Subbed URL. Cleared in CloseEpisodePicker and re-attached on
    // language switches OR whenever Recent.Items changes (UpsertAndSave
    // on first play of a new show replaces the RecentWatch reference,
    // so a subscription that was attached BEFORE that play would point
    // to a removed instance afterward — re-resolving on collection
    // changes catches this case).
    private RecentWatch? _pickerRecentSubscription;
    private MainWindowViewModel? _pickerRecentItemsHookedVm;

    // The original search-row VideoResult that opened the picker, if
    // any. We store it separately because the picker now receives a
    // synthetic VideoResult built with the language-effective URL
    // (so all three card surfaces open the picker in the same variant)
    // — _pickerSeries points at that synthetic instance, not the
    // search row's bound vr. The chevron-rotation flag
    // (IsEpisodesPopupOpen) lives on the row's bound vr though, so
    // we keep the reference here to flip it back to false on close.
    // Null when the picker was opened from a Recent / MyList card
    // (no chevron rotation tracking needed there).
    private VideoResult? _pickerOriginatingSearchRow;

    /// <summary>
    /// Opens the picker for a series VideoResult. <paramref name="resumeEpisodeUrl"/>
    /// pre-selects the saved season when the user is resuming from
    /// Recently watched; pass null when opening fresh from search.
    /// </summary>
    private async void OpenEpisodePicker(VideoResult series, string? resumeEpisodeUrl)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        _pickerSeries = series;
        _pickerResumeEpisodeUrl = resumeEpisodeUrl;
        _pickerSeasons = Array.Empty<SeriesSeason>();
        _pickerCurrentSeriesUrl = series.PageUrl;
        // Hide the language dropdown during the spinner phase. Items
        // (ItemsSource) and selected option are populated by
        // SyncLanguageBoxFromInfo once the /series fetch returns —
        // setting them here would just be discarded by that call.
        _suppressLanguageChange = true;
        EpisodePickerLanguageBox.IsVisible = false;
        _suppressLanguageChange = false;

        // Reset transient UI state. Title + thumb come from the series
        // result up-front (no network needed). Status text + season
        // dropdown stay hidden until the fetch lands so the user sees
        // a clean "poster + title + spinner" layout instead of a
        // half-rendered modal with empty controls.
        EpisodePickerTitle.Text = series.Title;
        EpisodePickerStatus.Text = "";
        EpisodePickerStatus.IsVisible = false;
        EpisodePickerSeasonBox.IsVisible = false;
        EpisodePickerSeasonBox.ItemsSource = null;
        EpisodePickerList.ItemsSource = null;
        // SetPickerThumbnail handles the clear-vs-keep decision based
        // on whether the URL changed — same-URL repeat opens skip the
        // clear (no dark flash), different-URL opens clear so the
        // prior poster doesn't leak under the new fetch.
        SetPickerThumbnail(series.ThumbnailUrl);

        // Stamp the open time so the backdrop click handler can
        // ignore stray pointer events that arrive within the same
        // gesture as the chevron click that opened the modal —
        // those would otherwise immediately re-close the modal,
        // and from the user's perspective the chevron click "did
        // nothing" (the modal flash is too brief to perceive,
        // only the card's hover state cycles visibly).
        _overlayShownAt = DateTime.UtcNow;
        EpisodePickerOverlay.IsVisible = true;

        _pickerCts?.Cancel();
        _pickerCts = new CancellationTokenSource();
        var ct = _pickerCts.Token;

        SeriesInfo? info;
        // Fast path: BsToService caches successful /series fetches for
        // the session, so a repeat open of the same series returns
        // synchronously here and we never enter the spinner state.
        if (vm.BsTo.TryGetCachedSeries(series.PageUrl, out var cachedInfo))
        {
            info = cachedInfo;
        }
        else
        {
            // First open: show the spinner, dim the rest of the modal
            // chrome (status / dropdown stay hidden), and await the
            // network call. The class toggle on EpisodePickerLoadingSpinner
            // gates the infinite rotation animation so it only runs
            // while the spinner is actually visible.
            EpisodePickerLoading.IsVisible = true;
            EpisodePickerLoadingSpinner.Classes.Add("spinning");
            try
            {
                info = await vm.BsTo.GetSeriesAsync(series.PageUrl, ct);
            }
            catch
            {
                info = null;
            }

            if (ct.IsCancellationRequested) return;

            EpisodePickerLoadingSpinner.Classes.Remove("spinning");
            EpisodePickerLoading.IsVisible = false;
        }

        if (info is null || info.Seasons.Count == 0)
        {
            EpisodePickerStatus.IsVisible = true;
            EpisodePickerStatus.Text = "Couldn't load episodes — try again later.";
            return;
        }

        // Loaded successfully — reveal the status line + season +
        // language dropdowns for both the cache-hit and post-fetch
        // paths.
        EpisodePickerStatus.IsVisible = true;
        EpisodePickerSeasonBox.IsVisible = true;
        EpisodePickerLanguageBox.IsVisible = true;
        // Sync the language dropdown from what the scraper actually
        // loaded (info.Language). Needed for shows whose URL has no
        // language suffix but whose only available variant is "de/sub"
        // — without this the dropdown would still show "German" even
        // though Subbed content was loaded. Suppress flag prevents the
        // SelectionChanged handler from firing a refetch since this is
        // a passive sync, not a user action.
        SyncLanguageBoxFromInfo(info);

        EpisodePickerTitle.Text = info.Title;
        // Counts joined by " · " so the user sees the full size of the
        // show at a glance on a single line. Season 0 (specials / OVAs)
        // is split out into its own "N specials" segment instead of
        // inflating the regular season + episode counts — so a show
        // like Naruto Shippuden reads "21 seasons · 500 episodes · 20
        // specials" rather than "22 seasons · 520 episodes". The
        // specials segment is omitted when the show has no Season 0.
        var regularSeasons = info.Seasons.Where(s => s.Number > 0).ToList();
        var seasonCount = regularSeasons.Count;
        var totalEpisodes = regularSeasons.Sum(s => s.Episodes.Count);
        var specialsCount = info.Seasons
            .Where(s => s.Number == 0)
            .Sum(s => s.Episodes.Count);
        var status =
            $"{seasonCount} season{(seasonCount == 1 ? "" : "s")}"
            + " · "
            + $"{totalEpisodes} episode{(totalEpisodes == 1 ? "" : "s")}";
        if (specialsCount > 0)
            status += " · " + $"{specialsCount} special{(specialsCount == 1 ? "" : "s")}";
        EpisodePickerStatus.Text = status;

        // Populate the season dropdown + select the starting season
        // SYNCHRONOUSLY before any await — without this, the TMDb
        // lookup below runs first on repeat opens (BsTo cache hit)
        // and the user sees an empty dropdown / episode list for the
        // duration of that network call before content appears.
        // Reordering puts the data on screen the instant the cache
        // hits; poster resolution runs after and only updates the
        // image when it lands.
        _pickerSeasons = info.Seasons;
        EpisodePickerSeasonBox.ItemsSource = info.Seasons
            .Select(s => new SeasonItem
            {
                Number = s.Number,
                EpisodeCount = s.Episodes.Count,
            })
            .ToList();
        EpisodePickerSeasonBox.Classes.Set("has-many", info.Seasons.Count > 7);

        // Pick which season to show first. Try, in order:
        //   1. Active playback's season (when watching in PiP, prefer
        //      the season the user is currently on regardless of URL
        //      match — matches the user's mental model of "open the
        //      picker for what I'm watching now").
        //   2. URL match against resumeEpisodeUrl, language-stripped
        //      (handles the cross-language case where resume URL
        //      belongs to the OTHER variant — e.g. user last watched
        //      Subbed but the picker is now showing German).
        //   3. Recent entry's LastSeasonNumber (URL-agnostic — covers
        //      cases where the picker fetch returned no match for the
        //      resume URL at all).
        //   4. First numbered season (skips Season 0 specials).
        //   5. Index 0 fallback for shows with only specials.
        int? activeSeason = null;
        if (_currentVideoResult is { } cv
            && !string.IsNullOrEmpty(cv.SeriesPageUrl)
            && cv.SeasonNumber is { } activeSn
            && (PageUrlEquals(cv.SeriesPageUrl, _pickerCurrentSeriesUrl)
                || (cv.SeriesPageUrl!.Contains("bs.to", StringComparison.OrdinalIgnoreCase)
                    && BsToUrl.SameLanguageStripped(cv.SeriesPageUrl, _pickerCurrentSeriesUrl))))
        {
            activeSeason = activeSn;
        }

        var startIdx = -1;
        if (activeSeason is { } sn)
        {
            for (var i = 0; i < info.Seasons.Count; i++)
            {
                if (info.Seasons[i].Number == sn) { startIdx = i; break; }
            }
        }
        if (startIdx < 0 && !string.IsNullOrEmpty(resumeEpisodeUrl))
        {
            var resumeKey = BsToUrl.NormalizeEpisodeKey(resumeEpisodeUrl!);
            for (var i = 0; i < info.Seasons.Count; i++)
            {
                if (info.Seasons[i].Episodes.Any(e =>
                    string.Equals(BsToUrl.NormalizeEpisodeKey(e.Url), resumeKey, StringComparison.OrdinalIgnoreCase)))
                {
                    startIdx = i;
                    break;
                }
            }
        }
        if (startIdx < 0 && !string.IsNullOrEmpty(_pickerCurrentSeriesUrl))
        {
            var rwForSeason = vm.Recent.FindByUrlOrToggled(_pickerCurrentSeriesUrl!);
            if (rwForSeason?.LastSeasonNumber is { } lastSn)
            {
                for (var i = 0; i < info.Seasons.Count; i++)
                {
                    if (info.Seasons[i].Number == lastSn) { startIdx = i; break; }
                }
            }
        }
        if (startIdx < 0)
        {
            for (var i = 0; i < info.Seasons.Count; i++)
            {
                if (info.Seasons[i].Number >= 1) { startIdx = i; break; }
            }
        }
        if (startIdx < 0) startIdx = 0;
        EpisodePickerSeasonBox.SelectedIndex = startIdx;

        // Warm the OTHER language's cache in the background if the
        // user has Recent watch data for it — that's the signal we
        // need the (S+E) → URL map for cross-language progress carry.
        // Skipped (no API call) for shows the user has only ever
        // watched in one language.
        TryPrefetchOtherLanguageVariant(vm, ct);

        // Hook PropertyChanged on the active Recent entries so the
        // row for whatever episode the user is watching ticks its
        // progress bar live (covers the "watching in PiP, opens the
        // modal" flow). Idempotent — clears any prior subscription
        // before reattaching.
        RefreshLiveProgressSubscription(vm);

        // Poster precedence: an upstream-set thumbnail (typically the
        // TMDb poster from search-time enrichment) wins; otherwise
        // try TMDb (cached per series so repeat opens never re-call
        // /search/tv); last resort is bs.to's own scraped poster.
        // Mirrors the user's stated fallback chain: "TMDB if available,
        // else bs.to." All paths run AFTER the dropdown is populated
        // so the await on TMDb can't delay the visible content.
        if (string.IsNullOrEmpty(series.ThumbnailUrl))
        {
            if (_tmdbPosterCache.TryGetValue(series.PageUrl, out var cachedTmdb))
            {
                // Cache hit: use the cached TMDb URL if non-empty,
                // otherwise fall back to bs.to. Empty-string cache
                // entry = "we asked TMDb already and got nothing".
                if (!string.IsNullOrEmpty(cachedTmdb))
                    SetPickerThumbnail(cachedTmdb);
                else if (!string.IsNullOrEmpty(info.ThumbnailUrl))
                    SetPickerThumbnail(info.ThumbnailUrl);
            }
            else if (vm.Sources.TmdbEnabled
                     && !string.IsNullOrWhiteSpace(vm.Sources.TmdbApiKey))
            {
                string? tmdbPoster = null;
                try
                {
                    tmdbPoster = await vm.Tmdb.SearchTvPosterAsync(
                        vm.Sources.TmdbApiKey, info.Title, ct);
                }
                catch { tmdbPoster = null; }

                if (ct.IsCancellationRequested) return;

                // Cache outcome (positive OR negative) so the next
                // open of this series skips the lookup entirely.
                _tmdbPosterCache[series.PageUrl] = tmdbPoster ?? string.Empty;

                if (!string.IsNullOrEmpty(tmdbPoster))
                    SetPickerThumbnail(tmdbPoster);
                else if (!string.IsNullOrEmpty(info.ThumbnailUrl))
                    SetPickerThumbnail(info.ThumbnailUrl);
            }
            else if (!string.IsNullOrEmpty(info.ThumbnailUrl))
            {
                SetPickerThumbnail(info.ThumbnailUrl);
            }
        }
        // (If series.ThumbnailUrl was set, the earlier
        // SetPickerThumbnail(series.ThumbnailUrl) call already painted it.)
    }

    // Hooked from the MainWindow constructor's SizeChanged subscription.
    // Avalonia popups don't auto-reposition on window resize — they
    // stay where they were placed when opened, so the season dropdown
    // would visually detach from its (re-positioned) ComboBox button.
    // Force-close any open dropdown so the user re-opens it freshly
    // anchored to the button's new location.
    private void OnWindowSizeChangedForPopups(object? sender, SizeChangedEventArgs e)
    {
        if (EpisodePickerSeasonBox?.IsDropDownOpen == true)
            EpisodePickerSeasonBox.IsDropDownOpen = false;
    }

    // Re-anchors the dropdown's scroll position every time it opens so
    // the selected season sits at the second visible row (with the
    // previous season above it as context). Mirrors the in-video
    // Episodes popup's PopulateEpisodeList / PopulateSeasonList scroll
    // framing — never preserves a prior scroll state across opens.
    //
    // Three non-obvious bits:
    //   1. Background priority on the offset write — runs AFTER
    //      everything else queued, including FluentTheme's focus-
    //      driven BringIntoView (which fires synchronously after this
    //      event returns when ContainerFromIndex(SelectedIndex).Focus()
    //      is called inside ComboBox.OnPopupOpened, and lands the
    //      selected row near the viewport bottom). With Loaded priority
    //      that auto-scroll would silently overwrite our offset.
    //   2. Anchor off the SELECTED container, not (idx - 1). The
    //      dropdown uses a virtualizing panel, so before the auto-scroll
    //      runs the (idx - 1) container may not be realized. The
    //      selected one always is — FluentTheme just focused it. Its
    //      Bounds.Height gives the row pitch and (Bounds.Y - row)
    //      lands the previous row at the viewport top.
    //   3. SYNCHRONOUS Opacity=0 on the dropdown's ScrollViewer —
    //      without it, the user sees one frame painted at the
    //      FluentTheme position before our Background callback fires
    //      and corrects to the second-row anchor. Hiding bridges that
    //      one-frame gap so the dropdown only ever paints visible at
    //      the correct position.
    private void EpisodePickerSeasonBox_DropDownOpened(object? sender, EventArgs e)
    {
        var idx = EpisodePickerSeasonBox.SelectedIndex;
        if (idx < 0) return;

        // Reach the dropdown's ScrollViewer synchronously via the
        // ComboBox's template part PART_Popup → its Child (the
        // PopupBorder) → descendants. The Popup hosts content in a
        // separate visual root so a plain GetVisualDescendants on the
        // ComboBox itself doesn't reach it.
        var sv = FindSeasonDropdownScrollViewer();
        if (sv is not null) sv.Opacity = 0;

        Dispatcher.UIThread.Post(() =>
        {
            var selected = EpisodePickerSeasonBox.ContainerFromIndex(idx) as Control;
            if (selected is null) { if (sv is not null) sv.Opacity = 1; return; }
            var resolved = sv ?? selected.FindAncestorOfType<ScrollViewer>();
            if (resolved is null) return;
            if (idx == 0)
            {
                resolved.Offset = new Vector(resolved.Offset.X, 0);
            }
            else
            {
                var rowHeight = selected.Bounds.Height;
                var targetY = Math.Max(0, selected.Bounds.Y - rowHeight);
                resolved.Offset = new Vector(resolved.Offset.X, targetY);
            }
            resolved.Opacity = 1;
        }, DispatcherPriority.Background);
    }

    private ScrollViewer? FindSeasonDropdownScrollViewer()
    {
        // The template's Popup (PART_Popup) is reachable via the
        // ComboBox's visual descendants, but the Popup's Child (the
        // PopupBorder) hosts content in a separate visual root, so we
        // descend from the Popup.Child explicitly to find the inner
        // ScrollViewer.
        var popup = EpisodePickerSeasonBox.GetVisualDescendants()
            .OfType<Popup>()
            .FirstOrDefault(p => p.Name == "PART_Popup");
        if (popup?.Child is not Visual content) return null;
        return content.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
    }

    // Reflects the scraper's actual loaded language onto the dropdown
    // without triggering its SelectionChanged refetch, AND hides
    // ComboBoxItems for languages the show doesn't offer (so the
    // user can't pick a variant that would 404). The selected index
    // follows the language string the API returned (anything
    // containing "sub" or that's literally "des" → German Subbed,
    // anything else → German). Falls back to URL-suffix inspection
    // when the API didn't return a language (older container, broken
    // page) so the dropdown still picks something sensible.
    // Stable per-language identity. Index doubles as a category id used
    // to map URLs / scraper-reported codes back to a dropdown option.
    private const int LangIndexGerman = 0;
    private const int LangIndexSubbed = 1;
    private const int LangIndexEnglish = 2;
    private const int LangIndexEnglishSub = 3;

    /// <summary>
    /// Model for a row in the language ComboBox. Used as the items in
    /// <see cref="EpisodePickerLanguageBox"/>'s ItemsSource so the
    /// dropdown only renders the languages we actually want shown.
    /// Earlier code used static x:Name'd ComboBoxItems with IsVisible
    /// toggles, but Avalonia's ComboBox doesn't honor IsVisible=false
    /// on dropdown popup items (it leaves blank slots, or shows the
    /// hidden item anyway) — filtering the ItemsSource is the only
    /// reliable way to actually hide options.
    /// ToString() returns Label so the default ComboBox display +
    /// the FluentTheme ComboBoxItem container both render the right
    /// text without needing an explicit ItemTemplate.
    /// </summary>
    private sealed class LanguageOption
    {
        public int Index { get; init; }
        public string Label { get; init; } = "";
        // bs.to language code. null = German default (no URL suffix).
        public string? Code { get; init; }
        public override string ToString() => Label;
    }

    private static readonly LanguageOption[] AllLanguageOptions =
    {
        new() { Index = LangIndexGerman,    Label = "German",      Code = null },
        new() { Index = LangIndexSubbed,    Label = "German Sub",  Code = BsToUrl.SubbedCode },
        new() { Index = LangIndexEnglish,   Label = "English",     Code = BsToUrl.EnglishCode },
        new() { Index = LangIndexEnglishSub,Label = "English Sub", Code = BsToUrl.EnglishSubCode },
    };

    /// <summary>Stable index for the language a URL's suffix points at.</summary>
    private static int LanguageIndexFor(string? url)
    {
        if (BsToUrl.IsEnglishSub(url)) return LangIndexEnglishSub;
        if (BsToUrl.IsSubbed(url)) return LangIndexSubbed;
        if (BsToUrl.IsEnglish(url)) return LangIndexEnglish;
        return LangIndexGerman;
    }

    /// <summary>The bs.to URL suffix for a stable language index, or
    /// null for the German default (no URL suffix).</summary>
    private static string? LanguageCodeFor(int index) =>
        AllLanguageOptions[index].Code;

    /// <summary>Removes the language at <paramref name="index"/> from
    /// the ComboBox's ItemsSource so it can no longer be picked.</summary>
    private void HideLanguageItem(int index)
    {
        if (EpisodePickerLanguageBox.ItemsSource is not IEnumerable<LanguageOption> current) return;
        var filtered = current.Where(o => o.Index != index).ToList();
        EpisodePickerLanguageBox.ItemsSource = filtered;
    }

    /// <summary>
    /// Maps a bs.to language code (from a select-option value or the
    /// scraper's resolved <see cref="SeriesInfo.Language"/>) to a
    /// dropdown index. Order matters — check more-specific tokens
    /// before more-permissive prefixes:
    ///   1. "jps" / "ens" / "en/sub" → English Sub.
    ///   2. "en" / "english" → English.
    ///   3. "des" / "de/sub" / anything containing "sub" → German Sub.
    ///   4. "de" / starts with "de" → German.
    /// Returns null for unrecognised codes.
    /// </summary>
    private static int? LanguageIndexForCode(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        var lo = code.ToLowerInvariant();
        // English Sub: bs.to's known forms — "jps" (Japanese audio +
        // English subs), "ens", "en/sub". Check before plain English
        // because "en/sub" starts with "en".
        if (lo == "jps" || lo == "ens" || lo == "en/sub")
            return LangIndexEnglishSub;
        if (lo == "en" || lo.StartsWith("en", StringComparison.Ordinal))
            return LangIndexEnglish;
        if (lo == "des" || lo.Contains("sub", StringComparison.Ordinal))
            return LangIndexSubbed;
        if (lo == "de" || lo.StartsWith("de", StringComparison.Ordinal))
            return LangIndexGerman;
        return null;
    }

    private void SyncLanguageBoxFromInfo(SeriesInfo info)
    {
        // Filter visible language options to only what bs.to actually
        // ships for this series. Source of truth, in priority order:
        //   1. info.AvailableLanguages — bs.to's <select.series-language>
        //      options, when the page rendered the picker (typically
        //      shows offering 2+ variants). Filtered against the
        //      persistent phantom store (BsToPhantomLanguages) — bs.to
        //      sometimes lists variants that have no actual content,
        //      and there's no reliable server-side signal to detect
        //      this up front, so we learn phantoms reactively from
        //      failed picker opens and remember them across restarts.
        //   2. info.Language — the single language the scraper just
        //      loaded; used when the page DIDN'T render the picker
        //      (shows offering only one variant), so we surface only
        //      that option instead of showing all three.
        //   3. URL suffix on the open request — last-resort, used when
        //      neither field is populated (old container / parse fail).
        var hasGerman = false;
        var hasSubbed = false;
        var hasEnglish = false;
        var hasEnglishSub = false;
        var phantoms = (DataContext is MainWindowViewModel vmCtx
            && !string.IsNullOrEmpty(_pickerCurrentSeriesUrl))
            ? vmCtx.BsToPhantomLanguages.GetPhantoms(_pickerCurrentSeriesUrl!)
            : null;
        var langs = info.AvailableLanguages;
        if (langs is { Count: > 0 })
        {
            foreach (var l in langs)
            {
                if (l is null) continue;
                if (phantoms is not null && phantoms.Contains(l)) continue;
                switch (LanguageIndexForCode(l))
                {
                    case LangIndexEnglishSub: hasEnglishSub = true; break;
                    case LangIndexEnglish: hasEnglish = true; break;
                    case LangIndexSubbed: hasSubbed = true; break;
                    case LangIndexGerman: hasGerman = true; break;
                }
            }
        }
        // No availableLanguages from the scraper (or every option got
        // filtered out as phantom) — derive the single variant from
        // info.Language or the URL suffix.
        if (!hasGerman && !hasSubbed && !hasEnglish && !hasEnglishSub)
        {
            var fallbackIdx = (!string.IsNullOrEmpty(info.Language)
                ? LanguageIndexForCode(info.Language!)
                : null)
                ?? LanguageIndexFor(_pickerCurrentSeriesUrl);
            switch (fallbackIdx)
            {
                case LangIndexEnglishSub: hasEnglishSub = true; break;
                case LangIndexEnglish: hasEnglish = true; break;
                case LangIndexSubbed: hasSubbed = true; break;
                default: hasGerman = true; break;
            }
        }
        // Build the filtered ItemsSource from the visibility flags.
        // ComboBox.ItemsSource is the only reliable way to actually
        // hide options in Avalonia's dropdown popup — IsVisible=false
        // on individual items leaves blank slots or ignores the flag
        // entirely depending on theme/version.
        var visibleOptions = new List<LanguageOption>();
        if (hasGerman) visibleOptions.Add(AllLanguageOptions[LangIndexGerman]);
        if (hasSubbed) visibleOptions.Add(AllLanguageOptions[LangIndexSubbed]);
        if (hasEnglish) visibleOptions.Add(AllLanguageOptions[LangIndexEnglish]);
        if (hasEnglishSub) visibleOptions.Add(AllLanguageOptions[LangIndexEnglishSub]);
        // Disable the dropdown when bs.to only ships ONE language for
        // this show — there's nothing for the user to pick, and an
        // enabled dropdown that opens to a single option reads as
        // broken UI.
        EpisodePickerLanguageBox.IsEnabled = visibleOptions.Count > 1;

        // Decide which option to select. Prefer info.Language (what
        // the scraper actually loaded), falling back to the URL's
        // suffix when the scraper didn't report one. SelectedItem
        // (not SelectedIndex) is set, since indices into the filtered
        // list don't correspond to the stable language indices.
        var selectedIndex = (string.IsNullOrEmpty(info.Language)
            ? null
            : LanguageIndexForCode(info.Language!))
            ?? LanguageIndexFor(_pickerCurrentSeriesUrl);
        var selectedOption =
            visibleOptions.FirstOrDefault(o => o.Index == selectedIndex)
            ?? visibleOptions.FirstOrDefault();

        _suppressLanguageChange = true;
        EpisodePickerLanguageBox.ItemsSource = visibleOptions;
        EpisodePickerLanguageBox.SelectedItem = selectedOption;
        _suppressLanguageChange = false;
    }

    // (Re-)attaches a PropertyChanged listener to the active language's
    // RecentWatch (if any) AND the cross-language counterpart so the
    // row for whatever episode the user is watching ticks its progress
    // bar live as the player updates the position. Without this the
    // rows are static — populated once when the picker opens, frozen
    // even if PiP playback is mid-episode behind the modal. Idempotent:
    // detaches any prior subscription before attaching the new one.
    // Also wires Recent.Items.CollectionChanged on first call so we
    // can re-resolve subscriptions when UpsertAndSave replaces an
    // entry (first play of a new language variant while the picker
    // is open).
    private void RefreshLiveProgressSubscription(MainWindowViewModel vm)
    {
        if (_pickerRecentSubscription is not null)
        {
            _pickerRecentSubscription.PropertyChanged -= OnPickerRecentProgressChanged;
            _pickerRecentSubscription = null;
        }

        // Wire CollectionChanged once per VM so we re-resolve when
        // entries get replaced by UpsertAndSave. -= is a no-op when
        // not attached, so this is safe to call multiple times.
        if (_pickerRecentItemsHookedVm is not null
            && !ReferenceEquals(_pickerRecentItemsHookedVm, vm))
        {
            _pickerRecentItemsHookedVm.Recent.Items.CollectionChanged
                -= OnPickerRecentItemsChanged;
            _pickerRecentItemsHookedVm = null;
        }
        if (_pickerRecentItemsHookedVm is null)
        {
            vm.Recent.Items.CollectionChanged -= OnPickerRecentItemsChanged;
            vm.Recent.Items.CollectionChanged += OnPickerRecentItemsChanged;
            _pickerRecentItemsHookedVm = vm;
        }

        if (string.IsNullOrEmpty(_pickerCurrentSeriesUrl)) return;

        // FindByUrlOrToggled (not plain Find) so we still resolve to
        // the consolidated entry when _pickerCurrentSeriesUrl is the
        // language variant the entry is NOT keyed by — the merge
        // keeps a single entry per show, but its PageUrl is whichever
        // variant won at merge time, and _pickerCurrentSeriesUrl is
        // whichever variant the user just opened the picker on. Plain
        // Find would silently miss → no live-progress subscription →
        // the modal's progress bar freezes after the first round-trip
        // through a PiP-drag (which mutates Items via UpdatePositionInMemory
        // and re-runs RefreshLiveProgressSubscription).
        _pickerRecentSubscription = vm.Recent.FindByUrlOrToggled(_pickerCurrentSeriesUrl!);
        if (_pickerRecentSubscription is not null)
            _pickerRecentSubscription.PropertyChanged += OnPickerRecentProgressChanged;
    }

    private void OnPickerRecentItemsChanged(
        object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Recent.Items mutated — RefreshLiveProgressSubscription's
        // current targets may have just been replaced (UpsertAndSave
        // is remove-then-insert, so the old reference is dead). Bail
        // if no picker is open — closed picker doesn't care.
        if (DataContext is not MainWindowViewModel vm) return;
        if (string.IsNullOrEmpty(_pickerCurrentSeriesUrl)) return;
        RefreshLiveProgressSubscription(vm);
    }

    private void OnPickerRecentProgressChanged(
        object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RecentWatch.Progress)) return;
        if (sender is not RecentWatch rw) return;
        // Only the actively-playing episode's progress matters for
        // live updates. We identify it via the RecentWatch's "last
        // episode" pointers (which tic alongside PositionMs in
        // UpdatePositionInMemory). Match against the displayed
        // season number first to avoid touching rows in a different
        // season.
        if (rw.LastEpisodeNumber is not { } liveEpNum) return;
        if (rw.LastSeasonNumber is not { } liveSeasonNum) return;
        var idx = EpisodePickerSeasonBox.SelectedIndex;
        if (idx < 0 || idx >= _pickerSeasons.Count) return;
        if (_pickerSeasons[idx].Number != liveSeasonNum) return;

        if (EpisodePickerList.ItemsSource is not System.Collections.IEnumerable enumerable) return;
        EpisodeRow? targetRow = null;
        foreach (var item in enumerable)
        {
            if (item is EpisodeRow row && row.Number == liveEpNum)
            {
                targetRow = row;
                break;
            }
        }
        if (targetRow is null) return;

        // Use the just-mutated entry's Progress directly. This is the
        // language being actively watched, so its position is the
        // freshest source — even when the picker is showing the
        // OTHER language's row, the user is operating on the same
        // logical episode and expects the bar to track playback
        // live. Math.Max-ing against the previously displayed value
        // (which the open-time render set to the cross-language max
        // of saved positions) sounds defensive but actually FREEZES
        // the bar whenever the saved cross-language position is
        // higher than the live one — e.g. user has 90 % saved in
        // Subbed, starts watching German from 10 %, the bar would
        // be stuck at 90 % until live exceeded that. Trust the live
        // tick.
        if (rw.Progress != targetRow.Progress)
            targetRow.Progress = rw.Progress;
    }

    // Warms the BsTo cache for the OTHER language variants so the
    // language dropdown toggle is instant instead of paying a /series
    // round-trip per switch. Fires for each variant the show might
    // expose (no-suffix default, /des, /en) other than the one we
    // already loaded — each one already cached is skipped. Only runs
    // when the user has any Recent entry for this show (signal: a
    // returning viewer who's plausibly going to toggle; shows the user
    // has never watched skip the extra /series calls). Fire-and-forget
    // on a Task.Run so the foreground load isn't delayed. The picker's
    // CancellationToken aborts the calls if the user closes the modal
    // mid-prefetch.
    private void TryPrefetchOtherLanguageVariant(MainWindowViewModel vm, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_pickerCurrentSeriesUrl)) return;
        if (vm.Recent.FindByUrlOrToggled(_pickerCurrentSeriesUrl!) is null) return;

        var bare = BsToUrl.StripLanguage(_pickerCurrentSeriesUrl!);
        var currentLang = BsToUrl.GetLanguage(_pickerCurrentSeriesUrl!);
        var others = new List<string>();
        // Enumerate the four forms — bare (default German), /des
        // (Subbed), /en (English), /jps (English Sub) — and queue
        // prefetches for the ones not currently loaded. Whether each
        // variant actually exists for the show is filtered by the
        // cache layer + scraper; a missing variant just returns null
        // and gets skipped.
        if (currentLang is not null) others.Add(bare);
        if (!string.Equals(currentLang, BsToUrl.SubbedCode, StringComparison.OrdinalIgnoreCase))
            others.Add(BsToUrl.WithLanguage(bare, BsToUrl.SubbedCode));
        if (!string.Equals(currentLang, BsToUrl.EnglishCode, StringComparison.OrdinalIgnoreCase))
            others.Add(BsToUrl.WithLanguage(bare, BsToUrl.EnglishCode));
        if (!string.Equals(currentLang, BsToUrl.EnglishSubCode, StringComparison.OrdinalIgnoreCase))
            others.Add(BsToUrl.WithLanguage(bare, BsToUrl.EnglishSubCode));

        foreach (var otherUrl in others)
        {
            if (string.Equals(otherUrl, _pickerCurrentSeriesUrl, StringComparison.OrdinalIgnoreCase))
                continue;
            if (vm.BsTo.TryGetCachedSeries(otherUrl, out _)) continue;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try { await vm.BsTo.GetSeriesAsync(otherUrl, ct); }
                catch { /* swallow — best-effort cache warm */ }
            });
        }
    }

    // Language switcher between bs.to's three variants of a series:
    // German (base URL or /de), German Subbed (/des), and English
    // (/en). On change we recompute the active series URL, refetch
    // via the existing BsTo cache (separate entries per language
    // URL), and repopulate the season + episode lists. Falls back
    // to a "couldn't load" status when the chosen language has no
    // content for this series — the user can flip back via the same
    // dropdown. Resume positions saved against another language's
    // episode URL are recovered automatically since the per-episode
    // dict keys all canonicalize via NormalizeEpisodeKey.
    private async void EpisodePickerLanguage_Changed(
        object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageChange) return;
        if (string.IsNullOrEmpty(_pickerCurrentSeriesUrl)) return;
        if (DataContext is not MainWindowViewModel vm) return;

        // Capture the still-valid state so we can revert if the new
        // variant turns out to be a phantom option (bs.to lists it in
        // <select.series-language> but loading the URL returns no
        // seasons — common for shows where the de/sub option is shown
        // even though no episodes were actually subbed).
        var previousUrl = _pickerCurrentSeriesUrl!;
        var previousIdx = LanguageIndexFor(previousUrl);
        // Read the selected option from ItemsSource via SelectedItem;
        // SelectedIndex is a position in the filtered list and doesn't
        // map to the stable language indices.
        if (EpisodePickerLanguageBox.SelectedItem is not LanguageOption pickedOption)
            return;
        var pickedLangIdx = pickedOption.Index;

        var newUrl = BsToUrl.WithLanguage(previousUrl, LanguageCodeFor(pickedLangIdx));
        if (string.Equals(newUrl, previousUrl, StringComparison.OrdinalIgnoreCase))
            return;

        _pickerCurrentSeriesUrl = newUrl;

        _pickerCts?.Cancel();
        _pickerCts = new CancellationTokenSource();
        var ct = _pickerCts.Token;

        SeriesInfo? info;
        if (vm.BsTo.TryGetCachedSeries(newUrl, out var cachedInfo))
        {
            info = cachedInfo;
        }
        else
        {
            // Spinner during refetch — same UI as the initial-open
            // loading state.
            EpisodePickerLoading.IsVisible = true;
            EpisodePickerLoadingSpinner.Classes.Add("spinning");
            try
            {
                info = await vm.BsTo.GetSeriesAsync(newUrl, ct);
            }
            catch
            {
                info = null;
            }
            if (ct.IsCancellationRequested) return;
            EpisodePickerLoadingSpinner.Classes.Remove("spinning");
            EpisodePickerLoading.IsVisible = false;
        }

        if (info is null || info.Seasons.Count == 0)
        {
            // Phantom option recovery. bs.to sometimes lists language
            // variants in its <select> that have no actual content
            // (e.g. Naruto lists "en" but no episodes are English;
            // Game of Thrones lists "de/sub" but nothing was subbed).
            // Hide the offending ComboBoxItem locally, persist the
            // phantom flag for this show + language so it stays
            // filtered across app restarts (otherwise the dropdown
            // would re-show the dead option on the next picker open),
            // revert SelectedIndex + URL to the previously-loaded
            // variant, and recompute IsEnabled.
            HideLanguageItem(pickedLangIdx);
            // Persist every <select> code that maps to the picked
            // dropdown index — bs.to may list a language under more
            // than one code form (e.g. both "des" and "de/sub"), so
            // flag all of them so future filters drop them all.
            if (info?.AvailableLanguages is { } allLangs)
            {
                foreach (var code in allLangs)
                {
                    if (string.IsNullOrEmpty(code)) continue;
                    if (LanguageIndexForCode(code) == pickedLangIdx)
                        vm.BsToPhantomLanguages.MarkPhantom(previousUrl, code);
                }
            }
            else
            {
                // No availableLanguages came back at all — flag the
                // canonical code for the picked index so SyncLanguage
                // BoxFromInfo's fallback path also respects this.
                var canonical = LanguageCodeFor(pickedLangIdx) ?? BsToUrl.GermanCode;
                vm.BsToPhantomLanguages.MarkPhantom(previousUrl, canonical);
            }
            _pickerCurrentSeriesUrl = previousUrl;
            _suppressLanguageChange = true;
            // SelectedItem (not SelectedIndex) — find the option in
            // the now-filtered ItemsSource that matches previousIdx.
            if (EpisodePickerLanguageBox.ItemsSource is IEnumerable<LanguageOption> filtered)
            {
                EpisodePickerLanguageBox.SelectedItem =
                    filtered.FirstOrDefault(o => o.Index == previousIdx)
                    ?? filtered.FirstOrDefault();
            }
            _suppressLanguageChange = false;
            var remaining = (EpisodePickerLanguageBox.ItemsSource as IEnumerable<LanguageOption>)?.Count() ?? 0;
            EpisodePickerLanguageBox.IsEnabled = remaining > 1;
            EpisodePickerStatus.Text =
                "That language isn't available for this show.";
            return;
        }

        // Update status text with the new variant's counts (same
        // " · " split as the initial open: regular seasons + episodes,
        // specials only when present).
        var regularSeasons = info.Seasons.Where(s => s.Number > 0).ToList();
        var seasonCount = regularSeasons.Count;
        var totalEpisodes = regularSeasons.Sum(s => s.Episodes.Count);
        var specialsCount = info.Seasons.Where(s => s.Number == 0)
            .Sum(s => s.Episodes.Count);
        var status =
            $"{seasonCount} season{(seasonCount == 1 ? "" : "s")}"
            + " · "
            + $"{totalEpisodes} episode{(totalEpisodes == 1 ? "" : "s")}";
        if (specialsCount > 0)
            status += " · " + $"{specialsCount} special{(specialsCount == 1 ? "" : "s")}";
        EpisodePickerStatus.Text = status;

        // Preserve the user's currently-selected season number across
        // the language switch when possible; fall back to the first
        // numbered season (skipping Season 0 / specials), then to
        // index 0 if the variant has only specials.
        var prevSeasonNumber = (EpisodePickerSeasonBox.SelectedItem as SeasonItem)?.Number;

        _pickerSeasons = info.Seasons;
        EpisodePickerSeasonBox.ItemsSource = info.Seasons
            .Select(s => new SeasonItem
            {
                Number = s.Number,
                EpisodeCount = s.Episodes.Count,
            })
            .ToList();
        EpisodePickerSeasonBox.Classes.Set("has-many", info.Seasons.Count > 7);

        var newIdx = 0;
        if (prevSeasonNumber is { } prevN)
        {
            for (var i = 0; i < info.Seasons.Count; i++)
            {
                if (info.Seasons[i].Number == prevN) { newIdx = i; break; }
            }
        }
        if (newIdx == 0 && prevSeasonNumber is null)
        {
            for (var i = 0; i < info.Seasons.Count; i++)
            {
                if (info.Seasons[i].Number >= 1) { newIdx = i; break; }
            }
        }
        EpisodePickerSeasonBox.SelectedIndex = newIdx;

        // Reflect what the scraper actually loaded — for shows where
        // the user picked "German Subbed" but the variant doesn't
        // exist, the API may have fallen back to the only available
        // language. Re-sync the dropdown so it tracks reality.
        SyncLanguageBoxFromInfo(info);
        // Update _pickerCurrentSeriesUrl to match what we actually
        // loaded — if the scraper resolved a requested variant to a
        // different one (e.g. picker asked for /en but only /de
        // exists, scraper falls back), keep the live URL aligned
        // with the loaded content so subsequent Recent lookups key
        // off the right URL. LanguageCodeFor maps the resolved
        // dropdown index back to the URL suffix.
        if (!string.IsNullOrEmpty(info.Language))
        {
            var idx = LanguageIndexForCode(info.Language!) ?? LangIndexGerman;
            _pickerCurrentSeriesUrl = BsToUrl.WithLanguage(
                _pickerCurrentSeriesUrl ?? "", LanguageCodeFor(idx));
        }

        // After a language switch the OLD variant becomes "the other
        // one" — same prefetch logic as the initial open ensures
        // progress carry works in both switch directions on the next
        // refresh.
        TryPrefetchOtherLanguageVariant(vm, ct);
        // Re-hook the live-progress subscription against the new
        // active variant + its cross-language counterpart.
        RefreshLiveProgressSubscription(vm);
    }

    private void EpisodePickerSeason_Changed(object? sender, SelectionChangedEventArgs e)
        => RefreshEpisodesForCurrentSeason();

    // Rebuilds the episode-list rows for whichever season is currently
    // selected. Extracted from the SelectionChanged handler so the
    // cross-language prefetch path can call it after the OTHER
    // language's SeriesInfo lands — that's how cross-language progress
    // shows up on the first render after a fresh-session prefetch
    // without the user having to manually toggle the dropdown.
    private void RefreshEpisodesForCurrentSeason()
    {
        var idx = EpisodePickerSeasonBox.SelectedIndex;
        if (idx < 0 || idx >= _pickerSeasons.Count)
        {
            EpisodePickerList.ItemsSource = null;
            return;
        }

        // Build EpisodeRow VMs so the per-row ProgressBar has a
        // Progress value to bind to. The consolidated Recent entry's
        // Episodes dict is keyed by language-stripped URL (German and
        // Subbed share one slot via BsToUrl.NormalizeEpisodeKey), and
        // UpdatePositionInMemory writes to that same slot live, so a
        // single dict lookup covers both saved-from-prior-session
        // progress and the live tick of the actively-playing episode.
        var seriesUrl = _pickerCurrentSeriesUrl ?? _pickerSeries?.PageUrl;
        var vm = DataContext as MainWindowViewModel;
        var recent = !string.IsNullOrEmpty(seriesUrl) && vm is not null
            ? vm.Recent.FindByUrlOrToggled(seriesUrl)
            : null;
        var currentSeasonNumber = _pickerSeasons[idx].Number;
        // Highlight URL: prefer the actively-playing episode (typically
        // only set while in PiP — opening the picker while the player
        // is fullscreen unmounts the player surface). When there's no
        // active playback, fall back to RecentWatch.LastEpisodeUrl so
        // the row for the most recently watched episode reads as the
        // anchor.
        var highlightUrl = !string.IsNullOrEmpty(_currentVideoResult?.PageUrl)
            ? _currentVideoResult!.PageUrl
            : recent?.LastEpisodeUrl;

        // Language label for the secondary "Playing in …" line on the
        // currently-playing row. Only computed when there's an active
        // playback (_currentVideoResult set) — the last-watched
        // fallback highlight doesn't get this line, since the user
        // isn't actively watching anything in that case.
        string? playingLanguageLabel = null;
        if (!string.IsNullOrEmpty(_currentVideoResult?.PageUrl))
        {
            var playingLangIdx = LanguageIndexFor(_currentVideoResult!.PageUrl);
            playingLanguageLabel = $"Currently playing in {AllLanguageOptions[playingLangIdx].Label}";
        }

        var rows = _pickerSeasons[idx].Episodes
            .Select(ep =>
            {
                // Episodes dict is keyed by language-stripped URL
                // (NormalizeEpisodeKey), so German and Subbed share a
                // single saved progress slot — no toggle / max needed.
                var progress = 0.0;
                if (recent is not null
                    && recent.Episodes.TryGetValue(BsToUrl.NormalizeEpisodeKey(ep.Url), out var p)
                    && p.LengthMs > 0)
                {
                    progress = Math.Clamp((double)p.PositionMs / p.LengthMs, 0.0, 1.0);
                }

                // Highlight match: language-stripped URL equality, with
                // an (S, E) fallback for shows whose two language
                // variants don't share a title slug (then even
                // NormalizeEpisodeKey can't equate the URLs, but the
                // last-watched season/episode numbers still pin the
                // correct row).
                var isPlayingNow = !string.IsNullOrEmpty(highlightUrl)
                    && string.Equals(
                        BsToUrl.NormalizeEpisodeKey(highlightUrl!),
                        BsToUrl.NormalizeEpisodeKey(ep.Url),
                        StringComparison.OrdinalIgnoreCase);
                if (!isPlayingNow
                    && recent?.LastSeasonNumber is { } hsn
                    && recent.LastEpisodeNumber is { } hen
                    && hsn == currentSeasonNumber
                    && hen == ep.Number)
                {
                    isPlayingNow = true;
                }

                return new EpisodeRow
                {
                    Episode = ep,
                    Progress = progress,
                    IsPlayingNow = isPlayingNow,
                    PlayingLanguageLabel = isPlayingNow ? playingLanguageLabel : null,
                };
            })
            .ToList();
        EpisodePickerList.ItemsSource = rows;

        // Anchor the highlighted episode (active playback or last
        // watched) at the second visible row so the previous episode
        // is the one above it as context — same framing the in-video
        // Episodes popup and the season dropdown use, so all three
        // surfaces feel like the same control. Re-runs every time the
        // user picks a different season too, which also drops any
        // scroll position the user had built up in the prior season —
        // the modal never carries scroll state across season switches.
        // hideUntilSettled=true masks the brief "scroll from 0 to
        // anchor" snap on initial open: the ScrollViewer stays
        // Opacity=0 until the deferred Loaded-priority callback has
        // applied the offset, so the user only ever sees the list
        // at its final scroll position.
        var highlightIndex = rows.FindIndex(r => r.IsPlayingNow);
        ScrollItemToSecond(EpisodePickerList, EpisodePickerScrollViewer,
            highlightIndex, hideUntilSettled: true);
    }

    private void EpisodePickerEpisode_Click(object? sender, RoutedEventArgs e)
    {
        // Tag carries the EpisodeRow VM; pull the wrapped SeriesEpisode out.
        if (sender is not Button { Tag: EpisodeRow row }
            || _pickerSeries is null
            || DataContext is not MainWindowViewModel vm) return;
        var ep = row.Episode;

        // Find which season this episode belongs to so the Recent entry
        // gets the right S/E numbers.
        var seasonNumber = 0;
        foreach (var s in _pickerSeasons)
        {
            if (s.Episodes.Contains(ep)) { seasonNumber = s.Number; break; }
        }

        // Build an episode-flavoured VideoResult: own page URL is the
        // episode URL, but SeriesPageUrl points back to the active
        // language variant of the series (so Recent / MyList key the
        // entry by the URL the user actually picked, not the variant
        // they originally opened the picker from).
        var seriesPageUrl = _pickerCurrentSeriesUrl ?? _pickerSeries.PageUrl;
        var episodeResult = new VideoResult
        {
            Title = ep.Title,
            Source = _pickerSeries.Source,
            PageUrl = ep.Url,
            ThumbnailUrl = _pickerSeries.ThumbnailUrl,
            Year = _pickerSeries.Year,
            Duration = _pickerSeries.Duration,
            Kind = VideoKind.Movie,
            SeriesPageUrl = seriesPageUrl,
            SeriesTitle = _pickerSeries.Title,
            SeriesThumbnailUrl = _pickerSeries.ThumbnailUrl,
            SeasonNumber = seasonNumber,
            EpisodeNumber = ep.Number,
        };

        // Resume position lookup. The per-episode dict is keyed by the
        // language-stripped URL (NormalizeEpisodeKey), so German and
        // Subbed share one slot — and UpdatePositionInMemory keeps it
        // in sync with the live player tick. A single lookup gives us
        // both saved-from-prior-session AND live cross-language
        // carry-over.
        var resumeMs = 0L;
        var recent = vm.Recent.FindByUrlOrToggled(seriesPageUrl);
        if (recent is not null
            && recent.Episodes.TryGetValue(BsToUrl.NormalizeEpisodeKey(ep.Url), out var p))
        {
            resumeMs = p.PositionMs;
        }

        CloseEpisodePicker();
        // Reuse the picker's already-loaded seasons for the in-player
        // Episodes popup cache — no duplicate /series fetch when the
        // user later hovers the Episodes icon during playback.
        PopulateEpisodesPopupCache(seriesPageUrl, _pickerSeasons);
        // Series position change deferred to the Playing event (see
        // OnSeriesActuallyPlaying in MainWindow.Video.cs) — the card
        // stays put through the captcha + load chain and only moves
        // to the top of Recent / MyList once VLC is decoding frames.
        // bs.to episodes need a real-browser captcha solve before
        // playback works — open the embedded WebView overlay rather
        // than going straight to yt-dlp's /extract (which gets
        // refused by Google's bot detection from the docker container).
        OpenBstoCaptchaOverlay(episodeResult, resumeMs);
        e.Handled = true;
    }

    private void EpisodePickerOverlay_BackdropClicked(object? sender, PointerPressedEventArgs e)
    {
        // Suppress backdrop-close for ~250ms after open — pointer
        // events from the SAME gesture that opened the modal can
        // bubble up to the overlay (Avalonia hit-tests the newly-
        // visible overlay as soon as IsVisible flips, even if the
        // press originated on a now-hidden chevron whose action-row
        // has just collapsed). A real user click happens well after
        // 250ms — they need to see the modal first — so this window
        // only ever swallows the bug case.
        if ((DateTime.UtcNow - _overlayShownAt).TotalMilliseconds < 250)
        {
            e.Handled = true;
            return;
        }
        // Cancel path — drop the picker-open card highlight so the
        // Recent / MyList card returns to its idle state. The episode-
        // pick path doesn't go through here; it calls CloseEpisodePicker
        // directly and leaves _pendingPlayingPageUrl set so the card
        // stays highlighted through the captcha + load chain until
        // OnPlayRequested clears it.
        ClearPickerPendingHighlight();
        CloseEpisodePicker();
    }

    private void EpisodePickerCard_Pressed(object? sender, PointerPressedEventArgs e)
        => e.Handled = true;

    private void EpisodePickerClose_Click(object? sender, RoutedEventArgs e)
    {
        ClearPickerPendingHighlight();
        CloseEpisodePicker();
    }

    private void ClearPickerPendingHighlight()
    {
        _pendingPlayingPageUrl = null;
        ApplyRecentPlayingHighlightNow();
    }

    private void CloseEpisodePicker()
    {
        _pickerCts?.Cancel();
        _pickerCts = null;
        // Drop the live-progress PropertyChanged subscription so the
        // RecentWatch entry doesn't keep firing into a closed picker
        // (and so the GC doesn't keep the picker alive via the
        // subscription's delegate).
        if (_pickerRecentSubscription is not null)
        {
            _pickerRecentSubscription.PropertyChanged -= OnPickerRecentProgressChanged;
            _pickerRecentSubscription = null;
        }
        if (_pickerRecentItemsHookedVm is not null)
        {
            _pickerRecentItemsHookedVm.Recent.Items.CollectionChanged
                -= OnPickerRecentItemsChanged;
            _pickerRecentItemsHookedVm = null;
        }
        // Flip the search-row chevron back to closed. Tracked in
        // _pickerOriginatingSearchRow because _pickerSeries is the
        // synthetic VideoResult built with the language-effective
        // URL — its IsEpisodesPopupOpen isn't bound to anything in
        // the UI. The original search-row vr IS bound to the chevron
        // and that's the one we need to flip.
        if (_pickerOriginatingSearchRow is not null)
        {
            _pickerOriginatingSearchRow.IsEpisodesPopupOpen = false;
            _pickerOriginatingSearchRow = null;
        }
        _pickerSeries = null;
        _pickerResumeEpisodeUrl = null;
        _pickerSeasons = Array.Empty<SeriesSeason>();
        _pickerCurrentSeriesUrl = null;
        EpisodePickerOverlay.IsVisible = false;
        EpisodePickerSeasonBox.ItemsSource = null;
        EpisodePickerList.ItemsSource = null;
        // Stop the spinner if the user closes mid-load — leaving the
        // infinite animation attached keeps invalidating the target
        // even though the parent is hidden.
        EpisodePickerLoading.IsVisible = false;
        EpisodePickerLoadingSpinner.Classes.Remove("spinning");
    }

    private void SetPickerThumbnail(string? url)
    {
        var newUrl = url ?? string.Empty;
        if (newUrl == _currentPickerThumbUrl)
        {
            // Same URL as currently displayed — nothing to do. Repeat
            // opens of the same series fall through this path and the
            // existing bitmap stays painted (no Source=null + SetSource
            // round-trip that would briefly clear a perfectly-good
            // cached image to dark).
            return;
        }
        _currentPickerThumbUrl = newUrl;
        // URL changed (cross-show, or empty→resolved within one open).
        // Clear the previous bitmap explicitly so the prior poster
        // doesn't linger under the new URL while AsyncImageLoader
        // fetches/cache-resolves; the dark Tertiary Border background
        // takes over for the gap. Then hand off to AsyncImageLoader,
        // whose process-wide cache makes this near-instant whenever
        // the new URL has been seen before anywhere else in the app.
        EpisodePickerThumb.Source = null;
        AsyncImageLoader.ImageLoader.SetSource(EpisodePickerThumb, newUrl);
    }
}
