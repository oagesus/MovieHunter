using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MovieHunter.Views;

// Theme picker — opens the modal overlay, applies theme overrides via
// App.ApplyTheme, and keeps both pill groups (title-bar flyout +
// Settings panel) in sync. Two groups exist for the same set of
// themes; whichever the user clicks, the other reflects the
// selection so they always agree.
public partial class MainWindow
{
    private void ThemeBtn_Click(object? sender, RoutedEventArgs e)
        => ThemePickerOverlay.IsVisible = true;

    private void ThemePickerOverlay_BackdropClicked(object? sender, PointerPressedEventArgs e)
        => ThemePickerOverlay.IsVisible = false;

    private void ThemePickerCard_Pressed(object? sender, PointerPressedEventArgs e)
        => e.Handled = true;

    private void DraculaTheme_Checked(object? sender, RoutedEventArgs e)
        => SelectTheme(sender, "Dracula");

    private void NetflixTheme_Checked(object? sender, RoutedEventArgs e)
        => SelectTheme(sender, "Netflix");

    private void PrimeVideoTheme_Checked(object? sender, RoutedEventArgs e)
        => SelectTheme(sender, "PrimeVideo");

    private void DisneyPlusTheme_Checked(object? sender, RoutedEventArgs e)
        => SelectTheme(sender, "DisneyPlus");

    private void CatppuccinTheme_Checked(object? sender, RoutedEventArgs e)
        => SelectTheme(sender, "Catppuccin");

    private void LightLavenderTheme_Checked(object? sender, RoutedEventArgs e)
        => SelectTheme(sender, "LightLavender");

    private void LightMintTheme_Checked(object? sender, RoutedEventArgs e)
        => SelectTheme(sender, "LightMint");

    private void LightApricotTheme_Checked(object? sender, RoutedEventArgs e)
        => SelectTheme(sender, "LightApricot");

    private bool _syncingThemePills;

    private void SelectTheme(object? sender, string theme)
    {
        if (sender is not RadioButton { IsChecked: true }) return;
        // Re-entry guard: SyncThemePills sets IsChecked on the matching
        // pill in the *other* group, which re-fires this handler. The
        // theme is already applied — short-circuit.
        if (_syncingThemePills) return;

        App.ApplyTheme(theme);
        if (DataContext is ViewModels.MainWindowViewModel vm)
            vm.Sources.Theme = theme;
        SyncThemePills(theme);
        // Dismiss the title-bar theme picker after a selection — instant
        // visual feedback that the theme has been applied.
        if (ThemePickerOverlay is not null) ThemePickerOverlay.IsVisible = false;
    }

    // Two pill groups exist for the same set of themes (title-bar palette
    // flyout + Settings panel). Whichever the user clicks, both sets must
    // reflect the new selection: we set IsChecked on every matching pill
    // and paint accent/accent-soft imperatively (DynamicResource inside
    // Flyout content lags behind merged-dictionary swaps; setting the
    // brushes directly with the just-applied resources avoids that).
    private void SyncThemePills(string theme)
    {
        var accent = ResolveBrush("SystemAccentColorBrush", this);
        var accentSoft = ResolveBrush("AccentSoftBrush", this);

        _syncingThemePills = true;
        try
        {
            foreach (var (pill, t) in AllThemePills())
            {
                if (t == theme)
                {
                    if (pill.IsChecked != true) pill.IsChecked = true;
                    if (accent is not null) pill.BorderBrush = accent;
                    if (accentSoft is not null) pill.Background = accentSoft;
                }
                else
                {
                    if (pill.IsChecked == true) pill.IsChecked = false;
                    pill.ClearValue(TemplatedControl.BorderBrushProperty);
                    pill.ClearValue(TemplatedControl.BackgroundProperty);
                }
            }
        }
        finally { _syncingThemePills = false; }
    }

    private IEnumerable<(RadioButton pill, string theme)> AllThemePills()
    {
        // Title-bar palette flyout group.
        yield return (LightApricotThemeRadio,    "LightApricot");
        yield return (CatppuccinThemeRadio,      "Catppuccin");
        yield return (DisneyPlusThemeRadio,      "DisneyPlus");
        yield return (DraculaThemeRadio,         "Dracula");
        yield return (LightLavenderThemeRadio,   "LightLavender");
        yield return (LightMintThemeRadio,       "LightMint");
        yield return (NetflixThemeRadio,         "Netflix");
        yield return (PrimeVideoThemeRadio,      "PrimeVideo");
        // Settings panel group.
        yield return (SettingsLightApricotRadio, "LightApricot");
        yield return (SettingsCatppuccinRadio,   "Catppuccin");
        yield return (SettingsDisneyPlusRadio,   "DisneyPlus");
        yield return (SettingsDraculaRadio,      "Dracula");
        yield return (SettingsLightLavenderRadio,"LightLavender");
        yield return (SettingsLightMintRadio,    "LightMint");
        yield return (SettingsNetflixRadio,      "Netflix");
        yield return (SettingsPrimeVideoRadio,   "PrimeVideo");
    }

    private static Avalonia.Media.IBrush? ResolveBrush(string key, Control origin)
    {
        if (origin.TryFindResource(key, origin.ActualThemeVariant, out var v)
            && v is Avalonia.Media.IBrush b)
            return b;
        if (Application.Current?.TryFindResource(key, origin.ActualThemeVariant, out var v2) == true
            && v2 is Avalonia.Media.IBrush b2)
            return b2;
        return null;
    }
}
