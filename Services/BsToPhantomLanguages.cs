using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MovieHunter.Services;

/// <summary>
/// Persistent per-show set of bs.to language codes that have been
/// observed to be phantom (listed in the page's
/// <c>&lt;select.series-language&gt;</c> but with no actual content —
/// e.g. Naruto lists "en" even though no episodes are English; Game
/// of Thrones lists "de/sub" even though nothing was subbed).
///
/// bs.to gives no reliable server-side signal to distinguish phantom
/// from real options up front, and the page's HTML can't tell them
/// apart either (a phantom URL serves a fallback-language page). The
/// app discovers phantoms reactively when the user picks a language
/// and the season fetch comes back empty. This store remembers those
/// failures across restarts so the dropdown stays correctly filtered
/// even on first picker open.
///
/// Stored at <c>%AppData%\MovieHunter\bstoPhantomLanguages.json</c>.
/// Keyed by language-stripped series URL so all variants of the same
/// show share one phantom set.
/// </summary>
public class BsToPhantomLanguages
{
    private readonly Dictionary<string, HashSet<string>> _phantoms =
        new(StringComparer.OrdinalIgnoreCase);

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MovieHunter", "bstoPhantomLanguages.json");

    public void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            using var stream = File.OpenRead(FilePath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String
                        && item.GetString() is { Length: > 0 } s)
                        set.Add(s);
                }
                if (set.Count > 0) _phantoms[prop.Name] = set;
            }
        }
        catch { /* best-effort: corrupt file = empty store */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var obj = _phantoms.ToDictionary(
                kv => kv.Key,
                kv => (object)kv.Value.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray());
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    private static string KeyFor(string seriesUrl) =>
        BsToUrl.StripLanguage(seriesUrl);

    /// <summary>
    /// Returns the set of language codes flagged phantom for the given
    /// series. Empty set when nothing is flagged. Result is read-only
    /// from the caller's perspective.
    /// </summary>
    public IReadOnlyCollection<string> GetPhantoms(string seriesUrl)
    {
        if (string.IsNullOrEmpty(seriesUrl)) return Array.Empty<string>();
        return _phantoms.TryGetValue(KeyFor(seriesUrl), out var set)
            ? set
            : (IReadOnlyCollection<string>)Array.Empty<string>();
    }

    /// <summary>
    /// True if <paramref name="languageCode"/> has been flagged
    /// phantom for the show at <paramref name="seriesUrl"/>.
    /// </summary>
    public bool IsPhantom(string seriesUrl, string languageCode)
    {
        if (string.IsNullOrEmpty(seriesUrl) || string.IsNullOrEmpty(languageCode))
            return false;
        return _phantoms.TryGetValue(KeyFor(seriesUrl), out var set)
            && set.Contains(languageCode);
    }

    /// <summary>
    /// Records that <paramref name="languageCode"/> is a phantom for
    /// the show at <paramref name="seriesUrl"/>. Persists immediately.
    /// </summary>
    public void MarkPhantom(string seriesUrl, string languageCode)
    {
        if (string.IsNullOrEmpty(seriesUrl) || string.IsNullOrEmpty(languageCode))
            return;
        var key = KeyFor(seriesUrl);
        if (!_phantoms.TryGetValue(key, out var set))
            _phantoms[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (set.Add(languageCode)) Save();
    }
}
