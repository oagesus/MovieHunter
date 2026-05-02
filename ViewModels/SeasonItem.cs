namespace MovieHunter.ViewModels;

// Row VM for the season ComboBox. The dropdown's ItemTemplate renders
// the number prominently and the episode count in a smaller, dimmer
// trailing TextBlock; ToString returns just "Season N" so the closed-
// combo display falls back to that label without the count clutter.
public sealed class SeasonItem
{
    public int Number { get; init; }
    public int EpisodeCount { get; init; }
    // Drives the .playing-now highlight on the in-player Episodes popup
    // season list, so the user sees at a glance which season their
    // current episode belongs to. Always false for the original
    // dropdown's items — only the popup builder sets it.
    public bool IsCurrentSeason { get; init; }

    public override string ToString() => $"Season {Number}";
}
