using System.Collections.Generic;

namespace MovieHunter.Models;

public class VideoResult
{
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public string PageUrl { get; init; } = "";
    public string? ThumbnailUrl { get; init; }
    public string? Duration { get; init; }
    public string? Year { get; init; }
    public string? Language { get; init; }
    public string? Description { get; init; }

    // Optional TMDb enrichment (populated when TMDb lookup is enabled and
    // the result matches a known movie).
    public int? TmdbId { get; init; }
    public string? ImdbId { get; init; }
    public double? Rating { get; init; }

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
