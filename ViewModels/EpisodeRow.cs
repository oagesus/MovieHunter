using CommunityToolkit.Mvvm.ComponentModel;
using MovieHunter.Services;

namespace MovieHunter.ViewModels;

// Row VM for the episode-picker list. Wraps a SeriesEpisode and adds
// a Progress fraction so the per-row ProgressBar can bind. Progress is
// 0 for unwatched episodes and (PositionMs / LengthMs) for episodes
// the user has watched. ObservableProperty so the picker can tick the
// progress bar live while the user is watching in PiP — without it
// the row's Progress would freeze at whatever value was set when the
// modal opened.
public sealed partial class EpisodeRow : ObservableObject
{
    public SeriesEpisode Episode { get; init; } = null!;
    public int Number => Episode.Number;
    public string Title => Episode.Title;
    [ObservableProperty] private double _progress;
    // True when this episode is the one currently playing (typically
    // visible to the user only via PiP, since opening the picker while
    // a video is fullscreen would close the player surface). Drives the
    // "playing-now" highlight class on the row Button so the active
    // episode reads as selected, mirroring how the open season dropdown
    // marks the selected season.
    public bool IsPlayingNow { get; init; }
    // Per-language availability: false when bs.to greys this episode
    // out for the loaded language (no hosters listed). The picker
    // disables the row Button's IsEnabled, dims the text, and skips
    // the captcha-open click. The user can switch language in the
    // dropdown to play the same episode in a variant where bs.to has
    // hosters.
    public bool IsAvailable => Episode.Available;

    // Localized "Playing in German" / "Playing in English Sub" /
    // … label shown beneath the title on the row that's currently
    // playing — and only on that row. Set ONLY for actively-playing
    // episodes (not for the last-watched fallback highlight); empty
    // otherwise. Lets the user see which language variant the player
    // is actually streaming, so when they're browsing the picker in a
    // different language tab it's obvious they'd be switching variant
    // rather than starting fresh.
    public string? PlayingLanguageLabel { get; init; }

    // Drives IsVisible on the secondary "Playing in …" line. Bool
    // shape so the XAML can bind directly without a converter.
    public bool HasPlayingLanguageLabel => !string.IsNullOrEmpty(PlayingLanguageLabel);
}
