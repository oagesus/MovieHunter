using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using MovieHunter.ViewModels;
using MovieHunter.Views;

namespace MovieHunter;

public partial class App : Application
{
    private static ResourceDictionary? _draculaOverrides;
    private static ResourceDictionary? _netflixOverrides;
    private static ResourceDictionary? _primeVideoOverrides;
    private static ResourceDictionary? _disneyPlusOverrides;
    private static ResourceDictionary? _catppuccinOverrides;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel();
            ApplyTheme(vm.Sources.Theme);

            desktop.MainWindow = new MainWindow { DataContext = vm };
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(string theme)
    {
        if (Current is not { } app) return;

        // Strip any custom-palette overrides first, then add back only the
        // one the selected theme needs.
        app.Resources.MergedDictionaries.Remove(GetDraculaOverrides());
        app.Resources.MergedDictionaries.Remove(GetNetflixOverrides());
        app.Resources.MergedDictionaries.Remove(GetPrimeVideoOverrides());
        app.Resources.MergedDictionaries.Remove(GetDisneyPlusOverrides());
        app.Resources.MergedDictionaries.Remove(GetCatppuccinOverrides());

        switch (theme)
        {
            case "Light":
                app.RequestedThemeVariant = ThemeVariant.Light;
                break;
            case "Dark":
                app.RequestedThemeVariant = ThemeVariant.Dark;
                break;
            case "Dracula":
                app.RequestedThemeVariant = ThemeVariant.Dark;
                app.Resources.MergedDictionaries.Add(GetDraculaOverrides());
                break;
            case "Netflix":
                app.RequestedThemeVariant = ThemeVariant.Dark;
                app.Resources.MergedDictionaries.Add(GetNetflixOverrides());
                break;
            case "PrimeVideo":
                app.RequestedThemeVariant = ThemeVariant.Dark;
                app.Resources.MergedDictionaries.Add(GetPrimeVideoOverrides());
                break;
            case "DisneyPlus":
                app.RequestedThemeVariant = ThemeVariant.Dark;
                app.Resources.MergedDictionaries.Add(GetDisneyPlusOverrides());
                break;
            case "Catppuccin":
                app.RequestedThemeVariant = ThemeVariant.Dark;
                app.Resources.MergedDictionaries.Add(GetCatppuccinOverrides());
                break;
            default:
                app.RequestedThemeVariant = ThemeVariant.Default;
                break;
        }
    }

    private static ResourceDictionary GetDraculaOverrides() =>
        _draculaOverrides ??= (ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://MovieHunter/Themes/DraculaOverrides.axaml"));

    private static ResourceDictionary GetNetflixOverrides() =>
        _netflixOverrides ??= (ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://MovieHunter/Themes/NetflixOverrides.axaml"));

    private static ResourceDictionary GetPrimeVideoOverrides() =>
        _primeVideoOverrides ??= (ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://MovieHunter/Themes/PrimeVideoOverrides.axaml"));

    private static ResourceDictionary GetDisneyPlusOverrides() =>
        _disneyPlusOverrides ??= (ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://MovieHunter/Themes/DisneyPlusOverrides.axaml"));

    private static ResourceDictionary GetCatppuccinOverrides() =>
        _catppuccinOverrides ??= (ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://MovieHunter/Themes/CatppuccinOverrides.axaml"));
}