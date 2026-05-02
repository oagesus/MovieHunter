using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using MovieHunter.Models;

namespace MovieHunter.Services;

public partial class MyListEntry : ObservableObject
{
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public string PageUrl { get; init; } = "";
    // Observable so the on-startup TMDb backfill on MainWindowViewModel
    // can flip the bound Image control over once a poster URL resolves.
    [ObservableProperty] private string? _thumbnailUrl;
    public string? Year { get; init; }
    public string? Duration { get; init; }

    [ObservableProperty] private DateTime _addedUtc = DateTime.UtcNow;

    // Mirrors RecentWatch's progress tracking so the My-list card can
    // show (and live-update) a progress bar matching the one on the
    // Recently watched card. NotifyPropertyChangedFor(Progress) makes
    // the bound bar repaint when the player ticks.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Progress))]
    private long _positionMs;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Progress))]
    private long _lengthMs;

    // Series-only: most-recently-played episode metadata. Mirrored from
    // the matching RecentWatch entry whenever we play an episode of this
    // series so the My-list card can show "S1E20 · EpisodeName" the same
    // way Recently watched does. Persisted alongside everything else.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EpisodeSubtitle))]
    private string? _lastEpisodeTitle;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EpisodeSubtitle))]
    private int? _lastSeasonNumber;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EpisodeSubtitle))]
    private int? _lastEpisodeNumber;

    /// <summary>0.0 – 1.0 fraction watched, 0 when length is unknown.</summary>
    public double Progress => LengthMs > 0
        ? Math.Clamp((double)PositionMs / LengthMs, 0.0, 1.0)
        : 0.0;

    /// <summary>
    /// True when the user has any saved playback progress (PositionMs &gt; 0).
    /// Drives the My-list-card hover badge: cards without progress show
    /// "Start playing"; cards with progress show "Continue playing".
    /// MyList only tracks the top-level position (it doesn't carry a
    /// per-episode dict like RecentWatch), so the check is single-field.
    /// </summary>
    public bool IsStarted => PositionMs > 0;

    /// <summary>
    /// Mirrors <see cref="RecentWatch.IsSeries"/> — drives the
    /// chevron-down "more episodes" chip on the My-list poster card so
    /// it only shows for series entries (currently bs.to). RecentWatch
    /// has a richer signal (LastEpisodeUrl), but My-list doesn't track
    /// per-episode state, so we lean on the source URL the same way
    /// MainWindow.Cards.IsBstoUrl does.
    /// </summary>
    public bool IsSeries => !string.IsNullOrEmpty(PageUrl)
        && PageUrl.Contains("bs.to", StringComparison.OrdinalIgnoreCase);

    /// <summary>"S02E05 · Title" line, mirroring RecentWatch.EpisodeSubtitle.
    /// Null when no episode has been played yet so the binding hides
    /// itself. Movies always return null since LastEpisodeNumber stays
    /// unset.</summary>
    public string? EpisodeSubtitle
    {
        get
        {
            if (!IsSeries) return null;
            var prefix = LastSeasonNumber is { } s && LastEpisodeNumber is { } e
                ? $"S{s:00}E{e:00}"
                : null;
            if (prefix is null && string.IsNullOrEmpty(LastEpisodeTitle))
                return null;
            if (prefix is null) return LastEpisodeTitle;
            return string.IsNullOrEmpty(LastEpisodeTitle) ? prefix : $"{prefix} · {LastEpisodeTitle}";
        }
    }

    public VideoResult ToVideoResult() => new()
    {
        Title = Title,
        Source = Source,
        PageUrl = PageUrl,
        ThumbnailUrl = ThumbnailUrl,
        Year = Year,
        Duration = Duration,
        IsInMyList = true,
    };
}

/// <summary>
/// Persists the user's saved-for-later list to
/// <c>%AppData%\MovieHunter\myList.json</c>. Most-recent first; no cap.
/// Mirrors <see cref="RecentlyWatched"/>.
/// </summary>
public class MyList
{
    public ObservableCollection<MyListEntry> Items { get; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MovieHunter", "myList.json");

    public bool Contains(string? pageUrl) =>
        !string.IsNullOrWhiteSpace(pageUrl)
        && Items.Any(i => i.PageUrl == pageUrl);

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

                Items.Add(new MyListEntry
                {
                    Title = Str(el, "title") ?? "",
                    Source = Str(el, "source") ?? "",
                    PageUrl = pageUrl!,
                    ThumbnailUrl = Str(el, "thumbnailUrl"),
                    Year = Str(el, "year"),
                    Duration = Str(el, "duration"),
                    PositionMs = Int64(el, "positionMs"),
                    LengthMs = Int64(el, "lengthMs"),
                    LastEpisodeTitle = Str(el, "lastEpisodeTitle"),
                    LastSeasonNumber = NullableInt(el, "lastSeasonNumber"),
                    LastEpisodeNumber = NullableInt(el, "lastEpisodeNumber"),
                    AddedUtc = el.TryGetProperty("addedUtc", out var w)
                        && w.TryGetDateTime(out var d) ? d : DateTime.UtcNow,
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
                    lastEpisodeTitle = i.LastEpisodeTitle,
                    lastSeasonNumber = i.LastSeasonNumber,
                    lastEpisodeNumber = i.LastEpisodeNumber,
                    addedUtc = i.AddedUtc,
                }).ToList(),
            };
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>
    /// Adds the entry and persists. Returns true if it was added; false
    /// if it was already there. The caller can pass an existing
    /// position/length so a movie that's already in Recently watched
    /// starts the My-list entry pre-populated. <paramref name="preserveAtTopPageUrl"/>
    /// is the PageUrl of a movie that should stay pinned at index 0
    /// (typically the currently-playing one) — when supplied and that
    /// entry is at the top, the new entry slots in at index 1 instead.
    /// </summary>
    public bool Add(VideoResult source, long positionMs = 0, long lengthMs = 0,
                    string? preserveAtTopPageUrl = null)
    {
        if (string.IsNullOrWhiteSpace(source.PageUrl)) return false;
        if (Contains(source.PageUrl)) return false;
        // Slot in at index 1 if the currently-playing movie is already
        // pinned at index 0 — without this the new addition pushes the
        // playing movie down to index 1, which feels wrong (the user
        // expects "what I'm watching now" to stay on top).
        var insertIndex = 0;
        if (!string.IsNullOrEmpty(preserveAtTopPageUrl)
            && !string.Equals(preserveAtTopPageUrl, source.PageUrl,
                              StringComparison.OrdinalIgnoreCase)
            && Items.Count > 0
            && string.Equals(Items[0].PageUrl, preserveAtTopPageUrl,
                             StringComparison.OrdinalIgnoreCase))
        {
            insertIndex = 1;
        }
        Items.Insert(insertIndex, new MyListEntry
        {
            Title = source.Title,
            Source = source.Source,
            PageUrl = source.PageUrl,
            ThumbnailUrl = source.ThumbnailUrl,
            Year = source.Year,
            Duration = source.Duration,
            PositionMs = Math.Max(0, positionMs),
            LengthMs = Math.Max(0, lengthMs),
            AddedUtc = DateTime.UtcNow,
        });
        Save();
        return true;
    }

    public bool Remove(string? pageUrl)
    {
        if (string.IsNullOrWhiteSpace(pageUrl)) return false;
        var removed = false;
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].PageUrl == pageUrl)
            {
                Items.RemoveAt(i);
                removed = true;
            }
        }
        if (removed) Save();
        return removed;
    }

    /// <summary>
    /// Toggles membership and persists. Returns the new state
    /// (true = saved, false = removed). <paramref name="preserveAtTopPageUrl"/>
    /// is forwarded to <see cref="Add"/> so a new addition slots in
    /// after the currently-playing movie instead of pushing it down.
    /// </summary>
    public bool Toggle(VideoResult source, long positionMs = 0, long lengthMs = 0,
                       string? preserveAtTopPageUrl = null)
    {
        if (string.IsNullOrWhiteSpace(source.PageUrl)) return false;
        if (Contains(source.PageUrl))
        {
            Remove(source.PageUrl);
            return false;
        }
        Add(source, positionMs, lengthMs, preserveAtTopPageUrl);
        return true;
    }

    /// <summary>
    /// Copies position/length from the supplied <see cref="RecentlyWatched"/>
    /// entries into any My-list items that share a PageUrl. Called once
    /// at startup so movies that were added to My-list BEFORE we tracked
    /// progress (or watched again from another tab) still show a correct
    /// progress bar without requiring the user to play them first. Saves
    /// once at the end if anything changed.
    /// </summary>
    public void SeedProgressFromRecent(RecentlyWatched recent)
    {
        var changed = false;
        foreach (var entry in Items)
        {
            var rw = recent.Find(entry.PageUrl);
            if (rw is null) continue;
            // Only overwrite if Recent has a higher / fresher position;
            // otherwise an in-flight My-list-side update could be lost.
            if (rw.PositionMs > entry.PositionMs)
            {
                entry.PositionMs = rw.PositionMs;
                changed = true;
            }
            if (rw.LengthMs > 0 && entry.LengthMs <= 0)
            {
                entry.LengthMs = rw.LengthMs;
                changed = true;
            }
        }
        if (changed) Save();
    }

    /// <summary>
    /// Mirror of <see cref="SeedProgressFromRecent"/> for the per-series
    /// last-watched-episode metadata. Covers two cases the per-play
    /// <see cref="UpdateLastEpisodeAndSave"/> can't reach:
    ///   1. Entries saved before LastEpisode* fields existed at all.
    ///   2. Entries added to My-list AFTER the show was already being
    ///      watched (Add creates the entry without the episode info,
    ///      and there's no replay between Add and the next render).
    /// Idempotent — only writes when the My-list value is empty AND
    /// Recent has something. Saves once at the end if anything changed.
    /// </summary>
    /// <summary>
    /// One-shot repair pass for MyList entries that were saved with the
    /// EPISODE URL as their PageUrl (and the EPISODE title as their
    /// Title) instead of the series URL / show name. Comes from a prior
    /// version of <see cref="MainWindow.Cards.ToggleMyList_Click"/> that
    /// passed <see cref="RecentWatch.ToVideoResult"/> straight to
    /// <see cref="Toggle"/>; that helper returns the episode shape for
    /// series, so MyList ended up keyed by the wrong URL and the card
    /// showed the episode title instead of the show name.
    ///
    /// For each broken entry, we look it up in Recently watched by both
    /// LastEpisodeUrl and the per-episode progress map. If we find the
    /// owning series, we either drop the broken entry (when a correctly-
    /// keyed entry already exists — duplicate from the buggy path) or
    /// rekey it in place at the same index with the show's URL, title,
    /// thumbnail, and current LastEpisode* metadata. Saves once if
    /// anything actually changed.
    /// </summary>
    public void MigrateBstoEpisodeKeys(RecentlyWatched recent)
    {
        // Map every episode URL we know about back to its parent series
        // RecentWatch — covers both the most-recent-episode pointer and
        // any per-episode progress entries left from prior plays.
        var episodeUrlToSeries = new Dictionary<string, RecentWatch>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var rw in recent.Items)
        {
            if (!rw.IsSeries) continue;
            if (!string.IsNullOrEmpty(rw.LastEpisodeUrl))
                episodeUrlToSeries[rw.LastEpisodeUrl!] = rw;
            foreach (var key in rw.Episodes.Keys)
                episodeUrlToSeries[key] = rw;
        }
        if (episodeUrlToSeries.Count == 0) return;

        // Walk back-to-front so any RemoveAt doesn't shift indices we're
        // about to use. Each pass either fixes-in-place or removes.
        var changed = false;
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            var entry = Items[i];
            if (string.IsNullOrEmpty(entry.PageUrl)) continue;
            // Already-correct entries resolve via the standard
            // series-URL key in Recently watched, so skip them.
            if (recent.Find(entry.PageUrl) is not null) continue;
            // Otherwise: does the entry's "PageUrl" actually match an
            // episode URL we have a parent for? If yes, repair.
            if (!episodeUrlToSeries.TryGetValue(entry.PageUrl, out var owner)) continue;

            var alreadyHasCorrect = Items.Any(e =>
                e != entry && string.Equals(
                    e.PageUrl, owner.PageUrl, StringComparison.OrdinalIgnoreCase));
            if (alreadyHasCorrect)
            {
                Items.RemoveAt(i);
                changed = true;
                continue;
            }
            // Replace at the same index so the user's ordering is
            // preserved. PageUrl is init-only, so we have to materialise
            // a new MyListEntry rather than mutate.
            Items[i] = new MyListEntry
            {
                Title = owner.Title,
                Source = owner.Source,
                PageUrl = owner.PageUrl,
                ThumbnailUrl = owner.ThumbnailUrl ?? entry.ThumbnailUrl,
                Year = owner.Year,
                Duration = owner.Duration,
                PositionMs = owner.PositionMs > 0 ? owner.PositionMs : entry.PositionMs,
                LengthMs = owner.LengthMs > 0 ? owner.LengthMs : entry.LengthMs,
                LastEpisodeTitle = owner.LastEpisodeTitle,
                LastSeasonNumber = owner.LastSeasonNumber,
                LastEpisodeNumber = owner.LastEpisodeNumber,
                AddedUtc = entry.AddedUtc,
            };
            changed = true;
        }
        if (changed) Save();
    }

    public void SeedLastEpisodeFromRecent(RecentlyWatched recent)
    {
        var changed = false;
        foreach (var entry in Items)
        {
            var rw = recent.Find(entry.PageUrl);
            if (rw is null || !rw.IsSeries) continue;
            if (string.IsNullOrEmpty(entry.LastEpisodeTitle)
                && !string.IsNullOrEmpty(rw.LastEpisodeTitle))
            {
                entry.LastEpisodeTitle = rw.LastEpisodeTitle;
                changed = true;
            }
            if (entry.LastSeasonNumber is null && rw.LastSeasonNumber is not null)
            {
                entry.LastSeasonNumber = rw.LastSeasonNumber;
                changed = true;
            }
            if (entry.LastEpisodeNumber is null && rw.LastEpisodeNumber is not null)
            {
                entry.LastEpisodeNumber = rw.LastEpisodeNumber;
                changed = true;
            }
        }
        if (changed) Save();
    }

    /// <summary>
    /// Looks up an entry by URL, falling back to the language-toggled
    /// URL (German ⇄ German Subbed for bs.to) when there's no direct
    /// match. The user only ever has ONE My-list entry per show
    /// (keyed by whichever language they added), so playback in the
    /// other variant has to find that one entry to update progress /
    /// last-episode metadata. Without this fallback, watching the
    /// "wrong" language silently no-ops the My-list update and the
    /// card freezes on the original language's data.
    /// </summary>
    private MyListEntry? FindByUrlOrToggled(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var direct = Items.FirstOrDefault(i => i.PageUrl == key);
        if (direct is not null) return direct;
        if (!key.Contains("bs.to", StringComparison.OrdinalIgnoreCase)) return null;
        // Match across all language variants (no-suffix / /de / /des
        // / /en) by language-stripped URL so a watch in any variant
        // updates the existing entry rather than creating a duplicate.
        return Items.FirstOrDefault(i =>
            BsToUrl.SameLanguageStripped(i.PageUrl, key));
    }

    /// <summary>
    /// In-memory-only progress update so the bound progress bar can tick
    /// alongside the player. Persistence still happens on pause / close
    /// / end via <see cref="UpdatePositionAndSave"/>.
    /// </summary>
    public void UpdatePositionInMemory(VideoResult source, long positionMs, long lengthMs)
    {
        // Key by RecentKey (series URL for episodes, page URL for
        // movies) so a series episode finds its My-list entry, which
        // is keyed by the series URL. PageUrl alone would be the
        // episode URL — never matching the saved series URL — so the
        // in-memory progress update silently no-op'd and the My-list
        // progress bar stopped updating during playback. Same key
        // semantics as Recent and MoveToTopAndSave.
        var key = source.RecentKey;
        if (string.IsNullOrWhiteSpace(key)) return;
        var existing = FindByUrlOrToggled(key);
        if (existing is null) return;
        existing.PositionMs = Math.Max(0, positionMs);
        existing.LengthMs = Math.Max(0, lengthMs);
    }

    /// <summary>
    /// Updates the entry's progress in place AND persists. No-op if the
    /// movie isn't saved to My-list (so playback of un-saved movies
    /// doesn't accidentally create a My-list entry).
    /// </summary>
    public void UpdatePositionAndSave(VideoResult source, long positionMs, long lengthMs)
    {
        // Same RecentKey routing as UpdatePositionInMemory above.
        var key = source.RecentKey;
        if (string.IsNullOrWhiteSpace(key)) return;
        var existing = FindByUrlOrToggled(key);
        if (existing is null) return;
        existing.PositionMs = Math.Max(0, positionMs);
        existing.LengthMs = Math.Max(0, lengthMs);
        Save();
    }

    /// <summary>
    /// If the movie is already in My-list, removes its entry and
    /// re-inserts it at the top, so the list's display order reflects
    /// the most recent play (same pattern as RecentlyWatched.UpsertAndSave).
    /// No-op if the movie isn't saved (we don't auto-add on play) or if
    /// it's already at index 0. Persists when something moved. Uses
    /// <see cref="VideoResult.RecentKey"/> so a series-episode VideoResult
    /// (PageUrl = episode URL, SeriesPageUrl = series URL) finds its
    /// My-list entry, which is keyed by the series URL — without this
    /// the reorder for bs.to series silently no-op'd because the
    /// episode URL never matched the saved series URL.
    /// </summary>
    public void MoveToTopAndSave(VideoResult source)
    {
        var key = source.RecentKey;
        if (string.IsNullOrWhiteSpace(key)) return;
        // Cross-language fallback: a Subbed-URL play should still
        // float the German-keyed entry to the top (and vice versa) —
        // the user has only one MyList entry per show.
        var existing = FindByUrlOrToggled(key);
        if (existing is null) return;
        var idx = Items.IndexOf(existing);
        if (idx <= 0) return;
        Items.RemoveAt(idx);
        Items.Insert(0, existing);
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
        return v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    }

    /// <summary>
    /// Mirrors RecentlyWatched's last-episode write into the matching
    /// My-list entry so the My-list card subtitle stays in sync. No-op
    /// when the source isn't a series episode (no SeriesPageUrl) or
    /// when the series isn't saved to My-list. Persists if anything
    /// actually changed; the IsSeries / EpisodeSubtitle observables
    /// fire so the bound subtitle TextBlock repaints in place.
    /// </summary>
    public void UpdateLastEpisodeAndSave(VideoResult source)
    {
        // Only series episodes carry SeriesPageUrl. Movies skip — they
        // have no "last episode" concept, and writing the movie's own
        // title into LastEpisodeTitle would just be dead data.
        if (string.IsNullOrEmpty(source.SeriesPageUrl)) return;
        var key = source.RecentKey;
        if (string.IsNullOrWhiteSpace(key)) return;
        var existing = FindByUrlOrToggled(key);
        if (existing is null) return;
        var changed = false;
        if (existing.LastEpisodeTitle != source.Title)
        {
            existing.LastEpisodeTitle = source.Title;
            changed = true;
        }
        if (existing.LastSeasonNumber != source.SeasonNumber)
        {
            existing.LastSeasonNumber = source.SeasonNumber;
            changed = true;
        }
        if (existing.LastEpisodeNumber != source.EpisodeNumber)
        {
            existing.LastEpisodeNumber = source.EpisodeNumber;
            changed = true;
        }
        if (changed) Save();
    }
}
