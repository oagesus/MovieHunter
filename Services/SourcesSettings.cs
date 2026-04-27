using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MovieHunter.Services;

public partial class SourceToggle : ObservableObject
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";

    [ObservableProperty] private bool _enabled = true;
}

public partial class SourcesSettings : ObservableObject
{
    [ObservableProperty] private int _resultsPerSource = 15;

    [ObservableProperty] private bool _tmdbEnabled;
    [ObservableProperty] private string _tmdbApiKey = "";

    [ObservableProperty] private string _theme = "LightLavender";

    [ObservableProperty] private int _volume = 100;

    public ObservableCollection<SourceToggle> Entries { get; } = new();

    private static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MovieHunter", "sources.json");

    public SourcesSettings()
    {
        PropertyChanged += (_, _) => Save();
    }

    public void Load()
    {
        if (File.Exists(SettingsFilePath))
        {
            try
            {
                using var stream = File.OpenRead(SettingsFilePath);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;

                if (root.TryGetProperty("resultsPerSource", out var rps)
                    && rps.ValueKind == JsonValueKind.Number)
                {
                    ResultsPerSource = Math.Max(1, rps.GetInt32());
                }

                if (root.TryGetProperty("tmdbEnabled", out var te))
                {
                    TmdbEnabled = te.ValueKind == JsonValueKind.True;
                }

                if (root.TryGetProperty("tmdbApiKey", out var tk)
                    && tk.ValueKind == JsonValueKind.String)
                {
                    TmdbApiKey = tk.GetString() ?? "";
                }

                if (root.TryGetProperty("theme", out var th)
                    && th.ValueKind == JsonValueKind.String)
                {
                    var t = th.GetString();
                    if (t == "System" || t == "Light" || t == "Dark"
                        || t == "Dracula" || t == "Netflix"
                        || t == "PrimeVideo" || t == "DisneyPlus"
                        || t == "Catppuccin"
                        || t == "LightLavender" || t == "LightMint"
                        || t == "LightApricot") Theme = t;
                }

                if (root.TryGetProperty("volume", out var vol)
                    && vol.ValueKind == JsonValueKind.Number)
                {
                    Volume = Math.Clamp(vol.GetInt32(), 0, 100);
                }
            }
            catch { }
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
            var obj = new
            {
                resultsPerSource = ResultsPerSource,
                tmdbEnabled = TmdbEnabled,
                tmdbApiKey = TmdbApiKey,
                theme = Theme,
                volume = Volume,
                sources = Entries.Select(s => new
                {
                    name = s.Name,
                    enabled = s.Enabled,
                }).ToList()
            };
            File.WriteAllText(SettingsFilePath,
                JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void MergeWithLive(IReadOnlyList<string> liveEngineNames)
    {
        var saved = LoadSavedStates();

        // Remove any previously-registered toggles that are no longer live
        // (e.g. Internet Archive leftover from earlier versions, or a
        // SearXNG engine that was removed from settings.yml).
        var toRemove = Entries
            .Where(s => !liveEngineNames.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        foreach (var e in toRemove)
        {
            e.PropertyChanged -= OnSourceChanged;
            Entries.Remove(e);
        }

        foreach (var name in liveEngineNames)
        {
            if (Entries.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var enabled = !saved.TryGetValue(name, out var s) || s;
            var toggle = new SourceToggle
            {
                Name = name,
                DisplayName = EngineDisplayName.For(name),
                Enabled = enabled,
            };
            toggle.PropertyChanged += OnSourceChanged;
            Entries.Add(toggle);
        }

        Save();
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e) => Save();

    private static Dictionary<string, bool> LoadSavedStates()
    {
        var dict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(SettingsFilePath)) return dict;
        try
        {
            using var stream = File.OpenRead(SettingsFilePath);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("sources", out var sources)
                || sources.ValueKind != JsonValueKind.Array)
                return dict;

            foreach (var e in sources.EnumerateArray())
            {
                if (!e.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                var name = n.GetString();
                if (string.IsNullOrEmpty(name)) continue;
                var enabled = !e.TryGetProperty("enabled", out var en) || en.GetBoolean();
                dict[name] = enabled;
            }
        }
        catch { }
        return dict;
    }
}
