using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using MovieHunter.Models;

namespace MovieHunter.Services;

// Per-episode resume position, keyed by episode URL inside a series'
// RecentWatch.Episodes dictionary. Lets the episode picker show
// individual progress for every episode the user has watched, not just
// the most recent one. Movies don't use this — their progress lives in
// the parent RecentWatch's PositionMs/LengthMs directly.
public class EpisodeProgress
{
    public long PositionMs { get; set; }
    public long LengthMs { get; set; }
    public DateTime LastWatchedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>0.0 – 1.0 fraction watched, 0 when length is unknown.</summary>
    public double Progress => LengthMs > 0
        ? Math.Clamp((double)PositionMs / LengthMs, 0.0, 1.0)
        : 0.0;
}

public partial class RecentWatch : ObservableObject
{
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    // Series URL for series entries, movie URL otherwise. The "key" used
    // by Recent.Find / Upsert / position updates.
    public string PageUrl { get; init; } = "";
    // Observable so the on-startup TMDb backfill (BackfillBstoPostersAsync
    // on MainWindowViewModel) flips the bound Image control over to the
    // newly-resolved poster URL without needing to re-create the entry.
    [ObservableProperty] private string? _thumbnailUrl;
    public string? Year { get; init; }
    public string? Duration { get; init; }

    // NotifyPropertyChangedFor(Progress) so the bound progress bar
    // updates as the player ticks (UpdatePositionAndSave on pause/seek
    // mutates these fields in place — the computed Progress wouldn't
    // otherwise raise PropertyChanged on its own).
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Progress))]
    private long _positionMs;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Progress))]
    private long _lengthMs;
    [ObservableProperty] private DateTime _lastWatchedUtc = DateTime.UtcNow;
    // Mirrors MyList membership so the poster chip on the recent card
    // reflects saved-state instantly. Synced after load and on
    // ToggleMyList_Click in the View.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MyListTooltip))]
    private bool _isInMyList;

    /// <summary>"Add to my list" / "Remove from my list" — bound by the
    /// poster-chip ToolTip on Recently-watched cards so the tooltip
    /// text flips alongside the plus/check icon swap.</summary>
    public string MyListTooltip => IsInMyList ? "Remove from my list" : "Add to my list";

    // ── Series tracking ──────────────────────────────────────────────
    // Set when this entry represents a series. Stays null for movies.
    // The pair (LastEpisodeUrl, LastSeasonNumber, LastEpisodeNumber,
    // LastEpisodeTitle) describes the most recently played episode.
    // PositionMs/LengthMs above refer to that episode — so resuming a
    // series picks up at the saved offset of the last watched episode.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSeries)),
     NotifyPropertyChangedFor(nameof(EpisodeSubtitle))]
    private string? _lastEpisodeUrl;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EpisodeSubtitle))]
    private string? _lastEpisodeTitle;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EpisodeSubtitle))]
    private int? _lastSeasonNumber;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EpisodeSubtitle))]
    private int? _lastEpisodeNumber;

    // Per-episode resume positions for series. Keyed by the language-
    // stripped episode URL (BsToUrl.NormalizeEpisodeKey) so German
    // (.../de) and Subbed (.../des) variants of the same episode share
    // a single slot — one canonical progress per logical episode.
    // Populated alongside the series-level position writes so the
    // episode picker can render individual progress bars per row,
    // not just for the last-watched episode. Movies leave this empty.
    public Dictionary<string, EpisodeProgress> Episodes { get; init; }
        = new(StringComparer.OrdinalIgnoreCase);

    public bool IsSeries => !string.IsNullOrEmpty(LastEpisodeUrl);

    /// <summary>"S02E05 · Title" line for the recent card subtitle.
    /// Null for movies so the binding hides itself.</summary>
    public string? EpisodeSubtitle
    {
        get
        {
            if (!IsSeries) return null;
            var prefix = LastSeasonNumber is { } s && LastEpisodeNumber is { } e
                ? $"S{s:00}E{e:00}"
                : null;
            if (prefix is null) return LastEpisodeTitle;
            return string.IsNullOrEmpty(LastEpisodeTitle) ? prefix : $"{prefix} · {LastEpisodeTitle}";
        }
    }

    /// <summary>0.0 – 1.0 fraction watched, 0 when length is unknown.</summary>
    public double Progress => LengthMs > 0
        ? Math.Clamp((double)PositionMs / LengthMs, 0.0, 1.0)
        : 0.0;

    /// <summary>
    /// True when the user has any saved playback progress — either a
    /// non-zero top-level position, or any per-episode entry with a
    /// non-zero position. Drives the recent-card hover badge: cards
    /// without progress show "Start playing"; cards with progress
    /// show "Continue playing".
    /// </summary>
    public bool IsStarted
    {
        get
        {
            if (PositionMs > 0) return true;
            foreach (var ep in Episodes.Values)
                if (ep.PositionMs > 0) return true;
            return false;
        }
    }

    /// <summary>
    /// Constructs a VideoResult ready to feed to PlayResultAsync.
    /// For movies: same fields as the original. For series: builds an
    /// EPISODE result (PageUrl=last episode URL, Series* fields populated
    /// so OnPlayRequested keys subsequent updates by the series URL).
    /// </summary>
    public VideoResult ToVideoResult()
    {
        if (IsSeries)
        {
            // SeriesPageUrl must match the LANGUAGE of the episode
            // we're about to play (LastEpisodeUrl). After cross-
            // language consolidation, this entry's stored PageUrl is
            // whichever variant won the merge — possibly any of the
            // three language URLs even when LastEpisodeUrl is a
            // different one. If we ship a VideoResult whose
            // SeriesPageUrl language doesn't match its PageUrl
            // language, downstream surfaces (in-video Episodes popup,
            // picker open) load the WRONG variant's season list and
            // the actively-playing episode never matches anything in
            // it. Derive SeriesPageUrl from the language of
            // LastEpisodeUrl so they stay aligned across all three
            // variants (German, Subbed, English).
            var seriesUrl = PageUrl;
            if (!string.IsNullOrEmpty(LastEpisodeUrl)
                && PageUrl.Contains("bs.to", StringComparison.OrdinalIgnoreCase))
            {
                seriesUrl = BsToUrl.WithLanguage(PageUrl, BsToUrl.GetLanguage(LastEpisodeUrl));
            }
            return new VideoResult
            {
                Title = LastEpisodeTitle ?? Title,
                Source = Source,
                PageUrl = LastEpisodeUrl!,
                ThumbnailUrl = ThumbnailUrl,
                Year = Year,
                Duration = Duration,
                IsInMyList = IsInMyList,
                Kind = VideoKind.Movie,
                SeriesPageUrl = seriesUrl,
                SeriesTitle = Title,
                SeriesThumbnailUrl = ThumbnailUrl,
                SeasonNumber = LastSeasonNumber,
                EpisodeNumber = LastEpisodeNumber,
            };
        }
        return new VideoResult
        {
            Title = Title,
            Source = Source,
            PageUrl = PageUrl,
            ThumbnailUrl = ThumbnailUrl,
            Year = Year,
            Duration = Duration,
            IsInMyList = IsInMyList,
        };
    }
}

/// <summary>
/// Persists recently-watched movies (title, source, poster, last play
/// position) to <c>%AppData%\MovieHunter\recentlyWatched.json</c>.
/// Most-recent first; no cap.
/// </summary>
public class RecentlyWatched
{
    public ObservableCollection<RecentWatch> Items { get; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MovieHunter", "recentlyWatched.json");

    private static double ProgressFraction(EpisodeProgress p) =>
        p.LengthMs > 0 ? (double)p.PositionMs / p.LengthMs : 0.0;

    public void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            using var stream = File.OpenRead(FilePath);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array) return;

            Items.Clear();
            foreach (var el in items.EnumerateArray())
            {
                var pageUrl = Str(el, "pageUrl");
                if (string.IsNullOrWhiteSpace(pageUrl)) continue;

                var episodes = new Dictionary<string, EpisodeProgress>(StringComparer.OrdinalIgnoreCase);
                if (el.TryGetProperty("episodes", out var epsEl)
                    && epsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in epsEl.EnumerateObject())
                    {
                        if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                        // Collapse per-language keys (.../de + .../des) onto
                        // the same canonical episode slot. When older saves
                        // contain both, keep whichever has more progress.
                        var key = BsToUrl.NormalizeEpisodeKey(prop.Name);
                        var entry = new EpisodeProgress
                        {
                            PositionMs = Int64(prop.Value, "positionMs"),
                            LengthMs = Int64(prop.Value, "lengthMs"),
                            LastWatchedUtc = prop.Value.TryGetProperty("lastWatchedUtc", out var ew)
                                && ew.TryGetDateTime(out var ed) ? ed : DateTime.UtcNow,
                        };
                        if (episodes.TryGetValue(key, out var existing)
                            && ProgressFraction(existing) >= ProgressFraction(entry))
                            continue;
                        episodes[key] = entry;
                    }
                }

                Items.Add(new RecentWatch
                {
                    Title = Str(el, "title") ?? "",
                    Source = Str(el, "source") ?? "",
                    PageUrl = pageUrl!,
                    ThumbnailUrl = Str(el, "thumbnailUrl"),
                    Year = Str(el, "year"),
                    Duration = Str(el, "duration"),
                    PositionMs = Int64(el, "positionMs"),
                    LengthMs = Int64(el, "lengthMs"),
                    LastEpisodeUrl = Str(el, "lastEpisodeUrl"),
                    LastEpisodeTitle = Str(el, "lastEpisodeTitle"),
                    LastSeasonNumber = NullableInt(el, "lastSeasonNumber"),
                    LastEpisodeNumber = NullableInt(el, "lastEpisodeNumber"),
                    LastWatchedUtc = el.TryGetProperty("lastWatchedUtc", out var w)
                        && w.TryGetDateTime(out var d) ? d : DateTime.UtcNow,
                    Episodes = episodes,
                });
            }
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var obj = new
            {
                items = Items.Select(i => new
                {
                    title = i.Title,
                    source = i.Source,
                    pageUrl = i.PageUrl,
                    thumbnailUrl = i.ThumbnailUrl,
                    year = i.Year,
                    duration = i.Duration,
                    positionMs = i.PositionMs,
                    lengthMs = i.LengthMs,
                    lastEpisodeUrl = i.LastEpisodeUrl,
                    lastEpisodeTitle = i.LastEpisodeTitle,
                    lastSeasonNumber = i.LastSeasonNumber,
                    lastEpisodeNumber = i.LastEpisodeNumber,
                    lastWatchedUtc = i.LastWatchedUtc,
                    // Per-episode resume map for series. Serialized as a
                    // plain object whose keys are episode URLs — read
                    // back into RecentWatch.Episodes on Load().
                    episodes = i.Episodes.ToDictionary(
                        kv => kv.Key,
                        kv => (object)new
                        {
                            positionMs = kv.Value.PositionMs,
                            lengthMs = kv.Value.LengthMs,
                            lastWatchedUtc = kv.Value.LastWatchedUtc,
                        }),
                }).ToList(),
            };
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>
    /// Inserts or updates the entry keyed by <see cref="VideoResult.RecentKey"/>
    /// (series URL for episodes, page URL for movies) and floats it to
    /// the top, then persists. For series episodes, the entry's title
    /// + thumbnail come from the series-context fields and the
    /// last-episode fields are populated. Cross-language consolidation:
    /// when a bs.to series is watched in any other language variant
    /// (German default / /de / /des Subbed / /en English), we reuse
    /// the existing entry instead of creating a duplicate so Recent
    /// stays at one card per show. Per-episode progress collapses
    /// onto language-normalized keys (BsToUrl.NormalizeEpisodeKey) so
    /// each logical episode occupies exactly one slot in Episodes
    /// regardless of which language was played.
    /// </summary>
    public void UpsertAndSave(VideoResult source, long positionMs, long lengthMs)
    {
        var key = source.RecentKey;
        if (string.IsNullOrWhiteSpace(key)) return;
        var isSeries = !string.IsNullOrEmpty(source.SeriesPageUrl);

        // Carry the existing per-episode progress map across the
        // remove + reinsert so previously-watched episodes keep their
        // saved positions. Without this, every Upsert would wipe the
        // dictionary and the picker would only ever show progress for
        // the freshly-played episode. Also pull from the toggled-
        // language variant's entry if one exists — that's the
        // consolidation step that prevents per-language duplicates.
        var episodes = new Dictionary<string, EpisodeProgress>(StringComparer.OrdinalIgnoreCase);
        var isBsto = key.Contains("bs.to", StringComparison.OrdinalIgnoreCase);
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            var matchesDirect = string.Equals(
                Items[i].PageUrl, key, StringComparison.OrdinalIgnoreCase);
            // Match across language variants (German default / /de /
            // /des / /en) by comparing language-stripped URLs. Replaces
            // the older 2-way Toggle pattern which only handled
            // German ⇄ Subbed and missed English.
            var matchesStripped = !matchesDirect
                && isBsto
                && BsToUrl.SameLanguageStripped(Items[i].PageUrl, key);
            if (!matchesDirect && !matchesStripped) continue;
            // Carry per-episode progress across the remove + reinsert,
            // normalizing keys so old per-language entries collapse onto
            // a single canonical slot. On collision keep whichever has
            // higher progress.
            foreach (var kv in Items[i].Episodes)
            {
                var normKey = BsToUrl.NormalizeEpisodeKey(kv.Key);
                if (episodes.TryGetValue(normKey, out var existing)
                    && ProgressFraction(existing) >= ProgressFraction(kv.Value))
                    continue;
                episodes[normKey] = kv.Value;
            }
            Items.RemoveAt(i);
        }
        if (isSeries && !string.IsNullOrEmpty(source.PageUrl))
        {
            episodes[BsToUrl.NormalizeEpisodeKey(source.PageUrl)] = new EpisodeProgress
            {
                PositionMs = Math.Max(0, positionMs),
                LengthMs = Math.Max(0, lengthMs),
                LastWatchedUtc = DateTime.UtcNow,
            };
        }

        Items.Insert(0, new RecentWatch
        {
            // For series, store the series identity (title / thumb) so
            // the recent card shows the show, not just the episode.
            Title = isSeries ? (source.SeriesTitle ?? source.Title) : source.Title,
            Source = source.Source,
            PageUrl = key,
            ThumbnailUrl = isSeries
                ? (source.SeriesThumbnailUrl ?? source.ThumbnailUrl)
                : source.ThumbnailUrl,
            Year = source.Year,
            Duration = source.Duration,
            PositionMs = Math.Max(0, positionMs),
            LengthMs = Math.Max(0, lengthMs),
            LastEpisodeUrl = isSeries ? source.PageUrl : null,
            LastEpisodeTitle = isSeries ? source.Title : null,
            LastSeasonNumber = isSeries ? source.SeasonNumber : null,
            LastEpisodeNumber = isSeries ? source.EpisodeNumber : null,
            LastWatchedUtc = DateTime.UtcNow,
            Episodes = episodes,
        });

        Save();
    }

    /// <summary>
    /// Updates an existing entry's progress in place (no remove + insert)
    /// so the bound UI doesn't recreate the card's visual container —
    /// avoids the brief overlay flicker on pause / close. Falls back to
    /// <see cref="UpsertAndSave"/> if no entry exists yet for the key.
    /// For series, also writes the last-episode fields so the resume
    /// pointer follows the actively-played episode.
    /// </summary>
    public void UpdatePositionAndSave(VideoResult source, long positionMs, long lengthMs)
    {
        var key = source.RecentKey;
        if (string.IsNullOrWhiteSpace(key)) return;
        var existing = FindByUrlOrToggled(key);
        if (existing is null)
        {
            UpsertAndSave(source, positionMs, lengthMs);
            return;
        }
        existing.PositionMs = Math.Max(0, positionMs);
        existing.LengthMs = Math.Max(0, lengthMs);
        existing.LastWatchedUtc = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(source.SeriesPageUrl))
        {
            existing.LastEpisodeUrl = source.PageUrl;
            existing.LastEpisodeTitle = source.Title;
            existing.LastSeasonNumber = source.SeasonNumber;
            existing.LastEpisodeNumber = source.EpisodeNumber;
            // Mirror into the per-episode map so the picker remembers
            // this episode's progress even after the user starts a
            // different one.
            if (!string.IsNullOrEmpty(source.PageUrl))
            {
                existing.Episodes[BsToUrl.NormalizeEpisodeKey(source.PageUrl)] = new EpisodeProgress
                {
                    PositionMs = Math.Max(0, positionMs),
                    LengthMs = Math.Max(0, lengthMs),
                    LastWatchedUtc = DateTime.UtcNow,
                };
            }
        }
        Save();
    }

    /// <summary>
    /// In-memory-only update so the bound progress bar can tick along
    /// with the player without hammering disk on every TimeChanged.
    /// Persistence still happens on pause / close / end via
    /// <see cref="UpdatePositionAndSave"/>.
    /// </summary>
    public void UpdatePositionInMemory(VideoResult source, long positionMs, long lengthMs)
    {
        var key = source.RecentKey;
        if (string.IsNullOrWhiteSpace(key)) return;
        var existing = FindByUrlOrToggled(key);
        if (existing is null) return;
        existing.PositionMs = Math.Max(0, positionMs);
        existing.LengthMs = Math.Max(0, lengthMs);
        // Tick the per-episode map too so when the picker re-opens it
        // sees the current live position, not the last persisted one.
        if (!string.IsNullOrEmpty(source.SeriesPageUrl)
            && !string.IsNullOrEmpty(source.PageUrl))
        {
            existing.Episodes[BsToUrl.NormalizeEpisodeKey(source.PageUrl)] = new EpisodeProgress
            {
                PositionMs = Math.Max(0, positionMs),
                LengthMs = Math.Max(0, lengthMs),
                LastWatchedUtc = DateTime.UtcNow,
            };
        }
    }

    public RecentWatch? Find(string pageUrl) =>
        Items.FirstOrDefault(i => i.PageUrl == pageUrl);

    /// <summary>
    /// Looks up an entry by URL, falling back to a language-stripped
    /// match (German default / /de / /des / /en for bs.to) when there's
    /// no direct hit. Used by Update / MoveToTop / Upsert so playback
    /// in a different language variant still finds and updates the
    /// user's existing entry rather than creating a duplicate. Plain
    /// <see cref="Find"/> stays exact-match for callers that need
    /// single-URL lookup.
    /// </summary>
    public RecentWatch? FindByUrlOrToggled(string pageUrl)
    {
        if (string.IsNullOrEmpty(pageUrl)) return null;
        var direct = Find(pageUrl);
        if (direct is not null) return direct;
        if (!pageUrl.Contains("bs.to", StringComparison.OrdinalIgnoreCase)) return null;
        return Items.FirstOrDefault(i =>
            BsToUrl.SameLanguageStripped(i.PageUrl, pageUrl));
    }

    /// <summary>
    /// One-shot migration: collapses bs.to entries that represent the
    /// SAME show in different language variants (URLs differing only
    /// in their /de / /des / /en suffix, or the no-suffix default) into
    /// a single entry. Earlier app builds keyed Recent by the exact
    /// URL the user played from, so a user who watched any combination
    /// of German / German Subbed / English of the same show ended up
    /// with separate cards on the Recently watched panel. New plays
    /// now consolidate via FindByUrlOrToggled, but historical
    /// duplicates need this migration to clean up. Keeps the entry
    /// with the freshest LastWatchedUtc, merges the others' Episodes
    /// dictionaries into it, and removes the losers. Saves once if
    /// anything changed. Idempotent — safe to run on every start.
    /// </summary>
    public void MergeLanguageDuplicates()
    {
        var changed = false;
        // Walk the list once, marking duplicates against a "winning"
        // entry per series. Use the language-stripped URL as the
        // bucket key so all language variants (no-suffix / /de / /des
        // / /en) of the same show land in the same group.
        var byBaseUrl = new Dictionary<string, List<RecentWatch>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rw in Items)
        {
            if (string.IsNullOrEmpty(rw.PageUrl)) continue;
            if (!rw.PageUrl.Contains("bs.to", StringComparison.OrdinalIgnoreCase)) continue;
            var baseUrl = BsToUrl.StripLanguage(rw.PageUrl);
            if (!byBaseUrl.TryGetValue(baseUrl, out var bucket))
                byBaseUrl[baseUrl] = bucket = new List<RecentWatch>();
            bucket.Add(rw);
        }

        foreach (var (_, bucket) in byBaseUrl)
        {
            if (bucket.Count < 2) continue;
            // Winner = most recent LastWatchedUtc.
            bucket.Sort((a, b) => b.LastWatchedUtc.CompareTo(a.LastWatchedUtc));
            var winner = bucket[0];
            for (var i = 1; i < bucket.Count; i++)
            {
                var loser = bucket[i];
                // Merge episode progress maps. Both sides have already
                // been normalized in Load() so a same-episode collision
                // means the same canonical key — keep whichever has
                // the higher progress fraction.
                foreach (var kv in loser.Episodes)
                {
                    if (winner.Episodes.TryGetValue(kv.Key, out var existing)
                        && ProgressFraction(existing) >= ProgressFraction(kv.Value))
                        continue;
                    winner.Episodes[kv.Key] = kv.Value;
                }
                Items.Remove(loser);
                changed = true;
            }
        }
        if (changed) Save();
    }

    /// <summary>
    /// Finds the freshest Recent entry for a series across all language
    /// variants (no-suffix / /de / /des / /en for bs.to). Each variant
    /// historically had its own URL and its own Recent entry; MyList /
    /// search rows only know one of them, so this widens the lookup so
    /// they still see updates from a variant they don't track. After
    /// consolidation Items typically has a single entry per show, but
    /// keeping this multi-variant pick is robust to mid-migration
    /// states. Returns whichever entry has the more recent
    /// <see cref="RecentWatch.LastWatchedUtc"/>; null when no variant
    /// has a Recent entry. For non-bs.to URLs this degrades to a single
    /// <see cref="Find"/> lookup.
    /// </summary>
    public RecentWatch? FindMostRecentAcrossLanguages(string pageUrl)
    {
        if (string.IsNullOrEmpty(pageUrl)) return null;
        if (!pageUrl.Contains("bs.to", StringComparison.OrdinalIgnoreCase))
            return Find(pageUrl);
        RecentWatch? best = null;
        foreach (var i in Items)
        {
            if (!BsToUrl.SameLanguageStripped(i.PageUrl, pageUrl)) continue;
            if (best is null || i.LastWatchedUtc > best.LastWatchedUtc)
                best = i;
        }
        return best;
    }

    /// <summary>
    /// If the movie is already in Recently watched, removes its entry
    /// and re-inserts it at the top so the list reflects the most
    /// recent click immediately (without waiting for the stream URL
    /// extraction → OnPlayRequested → UpsertAndSave round-trip, which
    /// can take ~500 ms). No-op if the movie isn't tracked yet (a fresh
    /// search result), or if it's already at position 0. Persists when
    /// something moved.
    /// </summary>
    public void MoveToTopAndSave(VideoResult source)
    {
        var key = source.RecentKey;
        if (string.IsNullOrWhiteSpace(key)) return;
        // Cross-language fallback so a Subbed-URL play floats the
        // German-keyed entry to the top (and vice versa).
        var entry = FindByUrlOrToggled(key);
        if (entry is null) return;
        var idx = Items.IndexOf(entry);
        if (idx <= 0) return;
        entry.LastWatchedUtc = DateTime.UtcNow;
        Items.RemoveAt(idx);
        Items.Insert(0, entry);
        Save();
    }

    private static string? Str(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static long Int64(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return 0;
        return v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    }

    private static int? NullableInt(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number ? v.GetInt32() : (int?)null;
    }
}
