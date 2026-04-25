using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using MovieHunter.Models;

namespace MovieHunter.Services;

public partial class RecentWatch : ObservableObject
{
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public string PageUrl { get; init; } = "";
    public string? ThumbnailUrl { get; init; }
    public string? Year { get; init; }
    public string? Duration { get; init; }

    [ObservableProperty] private long _positionMs;
    [ObservableProperty] private long _lengthMs;
    [ObservableProperty] private DateTime _lastWatchedUtc = DateTime.UtcNow;

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
    };
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
                    LastWatchedUtc = el.TryGetProperty("lastWatchedUtc", out var w)
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
                    lastWatchedUtc = i.LastWatchedUtc,
                }).ToList(),
            };
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>
    /// Inserts or updates the entry for <paramref name="source"/>.PageUrl
    /// and floats it to the top, then persists to disk. No-op if the
    /// position is zero and no existing entry exists (prevents saving
    /// "never-started" plays).
    /// </summary>
    public void UpsertAndSave(VideoResult source, long positionMs, long lengthMs)
    {
        if (string.IsNullOrWhiteSpace(source.PageUrl)) return;

        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].PageUrl == source.PageUrl)
                Items.RemoveAt(i);
        }

        Items.Insert(0, new RecentWatch
        {
            Title = source.Title,
            Source = source.Source,
            PageUrl = source.PageUrl,
            ThumbnailUrl = source.ThumbnailUrl,
            Year = source.Year,
            Duration = source.Duration,
            PositionMs = Math.Max(0, positionMs),
            LengthMs = Math.Max(0, lengthMs),
            LastWatchedUtc = DateTime.UtcNow,
        });

        Save();
    }

    public RecentWatch? Find(string pageUrl) =>
        Items.FirstOrDefault(i => i.PageUrl == pageUrl);

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
