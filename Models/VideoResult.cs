using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MovieHunter.Models;

// Movie = single playable thing (clicking the card starts playback).
// Series = a TV show with seasons/episodes; clicking the card opens the
// episode-picker overlay instead of starting playback directly.
public enum VideoKind { Movie, Series }

public partial class VideoResult : ObservableObject
{
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public string PageUrl { get; init; } = "";
    // Settable (not init-only) so the search aggregator can enrich
    // bs.to series results with a TMDb-fetched poster after the
    // VideoResult has been constructed by BsToService. ObservableProperty
    // would also work, but ThumbnailUrl is read by AsyncImageLoader on
    // first bind and the enrichment happens before the result is yielded
    // to the UI, so plain settable is sufficient.
    public string? ThumbnailUrl { get; set; }
    public string? Duration { get; init; }
    public string? Year { get; init; }
    public string? Language { get; init; }

    // Result kind. Defaults to Movie so existing code paths (hdfilme,
    // search results, recent / my-list cards constructed elsewhere) need
    // no changes — series only come from engines that explicitly set
    // this in SearxngClient.
    public VideoKind Kind { get; init; } = VideoKind.Movie;

    /// <summary>True when <see cref="Kind"/> is Series. Boolean form
    /// for XAML IsVisible bindings — they don't accept enum equality
    /// directly without a converter.</summary>
    public bool IsSeries => Kind == VideoKind.Series;

    // Series-context fields, set on the EPISODE VideoResults the picker
    // hands to PlayResultAsync. Stay null on every movie result and on
    // the series VideoResults that come back from search (those have
    // Kind=Series + null series-context — the URL itself is the
    // series page).
    //
    // When SeriesPageUrl is set, Recent / MyList key the entry by the
    // series URL (not the episode URL), so the same series doesn't
    // sprout one Recent card per episode and resume picks up the last
    // watched episode instead of the first one.
    public string? SeriesPageUrl { get; init; }
    public string? SeriesTitle { get; init; }
    public string? SeriesThumbnailUrl { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }

    // The "key" used by Recent / MyList: series URL for episodes,
    // own URL for movies and series-shell results.
    public string RecentKey => string.IsNullOrEmpty(SeriesPageUrl) ? PageUrl : SeriesPageUrl!;

    // Mutable runtime state — set by the My-List sync after load and by
    // ToggleMyList_Click. Notifies so the poster chip flips state in place.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MyListTooltip))]
    private bool _isInMyList;

    // True when this result represents the video the user is currently
    // watching. Drives the search-result row's accent highlight (and
    // corresponding icon-color flip), kept in sync from MainWindow on
    // each playback state change. Series rows match the active video
    // by series URL; movie rows match by their own page URL.
    [ObservableProperty] private bool _isCurrentlyPlaying;

    // True while the episode-picker modal is open for THIS specific
    // series result. Drives the chevron-button rotation on the search
    // row (rotates 180° when open, back to 0° when closed). Set true
    // in OpenEpisodes_Click before the modal opens, cleared in
    // CloseEpisodePicker. Stays false for movies and for other
    // surfaces (Recent / MyList) since their templates bind to
    // different data types.
    [ObservableProperty] private bool _isEpisodesPopupOpen;

    /// <summary>"Add to my list" / "Remove from my list" — bound by the
    /// poster-chip ToolTip on search-result rows so the tooltip flips
    /// in step with the chip's plus/check icon swap.</summary>
    public string MyListTooltip => IsInMyList ? "Remove from my list" : "Add to my list";

    // "S02E05 · Title" line shown bottom-aligned on search-result rows
    // for series the user has already watched. Pushed in by
    // RefreshAllLastWatchedFromRecent (looks up RecentWatch.EpisodeSubtitle
    // by RecentKey) — null on movies and on series with no Recent entry,
    // which hides the bound TextBlock via HasLastWatchedEpisode.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLastWatchedEpisode))]
    private string? _lastWatchedEpisodeLabel;

    public bool HasLastWatchedEpisode => !string.IsNullOrEmpty(LastWatchedEpisodeLabel);

    // 0.0 – 1.0 watched fraction of the last-watched episode. Pushed in
    // by RefreshAllLastWatchedFromRecent alongside the label, sourced
    // from RecentWatch.Progress (PositionMs / LengthMs of the last
    // episode the user actually played). Bound by the per-row
    // ProgressBar on the search-result row so the user can see how
    // far through that episode they are at a glance — same visual
    // treatment the episode-modal list uses.
    [ObservableProperty]
    private double _lastWatchedEpisodeProgress;

    /// <summary>
    /// Year / Language / Duration joined by "·" for display, omitting any
    /// that are empty. e.g. "2023 · 141 min" or "2002 · English · 161 min".
    /// </summary>
    public string MetaLine
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(Year)) parts.Add(Year!);
            if (!string.IsNullOrWhiteSpace(Language)) parts.Add(Language!);
            if (!string.IsNullOrWhiteSpace(Duration)) parts.Add(Duration!);
            return string.Join(" · ", parts);
        }
    }
}
