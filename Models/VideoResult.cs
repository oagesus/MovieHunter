using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MovieHunter.Models;

public partial class VideoResult : ObservableObject
{
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public string PageUrl { get; init; } = "";
    public string? ThumbnailUrl { get; init; }
    public string? Duration { get; init; }
    public string? Year { get; init; }
    public string? Language { get; init; }

    // Mutable runtime state — set by the My-List sync after load and by
    // ToggleMyList_Click. Notifies so the poster chip flips state in place.
    [ObservableProperty] private bool _isInMyList;

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
