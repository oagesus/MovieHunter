using System;
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
    public string? ThumbnailUrl { get; init; }
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

    /// <summary>0.0 – 1.0 fraction watched, 0 when length is unknown.</summary>
    public double Progress => LengthMs > 0
        ? Math.Clamp((double)PositionMs / LengthMs, 0.0, 1.0)
        : 0.0;

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

    public MyListEntry? Find(string? pageUrl) =>
        string.IsNullOrWhiteSpace(pageUrl)
            ? null
            : Items.FirstOrDefault(i => i.PageUrl == pageUrl);

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
                    addedUtc = i.AddedUtc,
                }).ToList(),
            };
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>
    /// Adds the entry (or floats it to the top) and persists. Returns
    /// true if it was added; false if it was already there. The caller
    /// can pass an existing position/length so a movie that's already
    /// in Recently watched starts the My-list entry pre-populated.
    /// </summary>
    public bool Add(VideoResult source, long positionMs = 0, long lengthMs = 0)
    {
        if (string.IsNullOrWhiteSpace(source.PageUrl)) return false;
        if (Contains(source.PageUrl)) return false;
        Items.Insert(0, new MyListEntry
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
    /// (true = saved, false = removed).
    /// </summary>
    public bool Toggle(VideoResult source, long positionMs = 0, long lengthMs = 0)
    {
        if (string.IsNullOrWhiteSpace(source.PageUrl)) return false;
        if (Contains(source.PageUrl))
        {
            Remove(source.PageUrl);
            return false;
        }
        Add(source, positionMs, lengthMs);
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
    /// In-memory-only progress update so the bound progress bar can tick
    /// alongside the player. Persistence still happens on pause / close
    /// / end via <see cref="UpdatePositionAndSave"/>.
    /// </summary>
    public void UpdatePositionInMemory(VideoResult source, long positionMs, long lengthMs)
    {
        if (string.IsNullOrWhiteSpace(source.PageUrl)) return;
        var existing = Items.FirstOrDefault(i => i.PageUrl == source.PageUrl);
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
        if (string.IsNullOrWhiteSpace(source.PageUrl)) return;
        var existing = Items.FirstOrDefault(i => i.PageUrl == source.PageUrl);
        if (existing is null) return;
        existing.PositionMs = Math.Max(0, positionMs);
        existing.LengthMs = Math.Max(0, lengthMs);
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
}
