using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MovieHunter.Models;

namespace MovieHunter.Services;

// Pure data transport types for the episode picker. Populated from the
// yt-dlp container's /series endpoint (which scrapes bs.to's series page
// HTML and groups episodes by season).
public record SeriesEpisode(
    int Number,
    string Title,
    string? Language,
    string Url,
    // Per-language availability — bs.to greys out episodes that have
    // no hosters for the loaded language variant. False means the
    // picker should disable the row for THIS language; the same
    // episode may still be playable in another language (handled by
    // letting the user switch language in the dropdown).
    bool Available = true);

public record SeriesSeason(
    int Number,
    IReadOnlyList<SeriesEpisode> Episodes);

public record SeriesInfo(
    string Title,
    string? ThumbnailUrl,
    IReadOnlyList<SeriesSeason> Seasons,
    // The language code the scraper actually loaded ("de", "de/sub",
    // etc.). Used by the picker's language dropdown to show the right
    // variant as selected — needed when a show's URL has no /des
    // suffix but its only available variant is German Subbed (the
    // page's <select> promotes "de/sub" when "de" isn't offered).
    // Null when the scraper couldn't determine it (older API, broken
    // page, etc.) — the caller falls back to URL inspection then.
    string? Language = null,
    // All language codes the page's series-language <select> exposes.
    // Empty when the page didn't render a picker. Lets the C# UI hide
    // dropdown options that would 404 if selected.
    IReadOnlyList<string>? AvailableLanguages = null);

/// <summary>
/// Fetches season + episode listings for a bs.to series page. Calls the
/// yt-dlp container's /series endpoint, which does the actual scraping
/// (BeautifulSoup) so all bs.to-specific selector logic lives in one
/// place. Returns null on network / parse failure — the caller surfaces
/// that as an empty picker with an error toast.
/// </summary>
// URL helpers for the bs.to language variants. bs.to mirrors the same
// series and episode under up to four language URLs:
//   Series German (default):  https://bs.to/serie/Naruto
//   Series German (explicit): https://bs.to/serie/Naruto/de
//   Series German Subbed:     https://bs.to/serie/Naruto/des
//   Series English:           https://bs.to/serie/Naruto/en
//   Series English Subbed:    https://bs.to/serie/Naruto/jps
//   Episode German:           https://bs.to/serie/Naruto/1/2-Title/de
//   Episode German Subbed:    https://bs.to/serie/Naruto/1/2-Title/des
//   Episode English:          https://bs.to/serie/Naruto/1/2-Title/en
//   Episode English Subbed:   https://bs.to/serie/Naruto/1/2-Title/jps
// All variants of the same episode share a single saved progress
// slot via `NormalizeEpisodeKey` (which strips any trailing language
// suffix), and series-level URL matching across variants goes
// through `SameLanguageStripped` (compares language-stripped forms
// instead of toggling between specific pairs).
public static class BsToUrl
{
    public const string GermanCode = "de";
    public const string SubbedCode = "des";
    public const string EnglishCode = "en";
    public const string EnglishSubCode = "jps";

    private const string SubbedSuffix = "/des";
    private const string GermanSuffix = "/de";
    private const string EnglishSuffix = "/en";
    private const string EnglishSubSuffix = "/jps";

    // Order doesn't affect EndsWith correctness ("/des" doesn't end
    // with "/de" — the last 3 chars are "des" not "/de"), but we list
    // longest-first as a defensive convention in case future suffixes
    // overlap.
    private static readonly string[] AllSuffixes =
        { EnglishSubSuffix, SubbedSuffix, GermanSuffix, EnglishSuffix };

    public static bool IsSubbed(string? url) =>
        !string.IsNullOrEmpty(url)
        && url!.EndsWith(SubbedSuffix, StringComparison.OrdinalIgnoreCase);

    public static bool IsEnglish(string? url) =>
        !string.IsNullOrEmpty(url)
        && url!.EndsWith(EnglishSuffix, StringComparison.OrdinalIgnoreCase);

    public static bool IsEnglishSub(string? url) =>
        !string.IsNullOrEmpty(url)
        && url!.EndsWith(EnglishSubSuffix, StringComparison.OrdinalIgnoreCase);

    public static string StripSubbed(string url) =>
        IsSubbed(url) ? url[..^SubbedSuffix.Length] : url;

    public static string AddSubbed(string url) =>
        IsSubbed(url) ? url : url.TrimEnd('/') + SubbedSuffix;

    /// <summary>
    /// Strips any recognized trailing language suffix (/de, /des, /en)
    /// and returns the bare URL. Non-bs.to or already-bare URLs are
    /// returned unchanged (modulo a trailing-slash trim).
    /// </summary>
    public static string StripLanguage(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var trimmed = url.TrimEnd('/');
        foreach (var suffix in AllSuffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return trimmed[..^suffix.Length];
        }
        return trimmed;
    }

    /// <summary>
    /// Returns the language code of the URL ("de", "des", "en"), or
    /// null if the URL has no recognized language suffix (the bare
    /// form, which bs.to treats as the show's default language —
    /// typically German).
    /// </summary>
    public static string? GetLanguage(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var trimmed = url.TrimEnd('/');
        foreach (var suffix in AllSuffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return suffix.Substring(1);
        }
        return null;
    }

    /// <summary>
    /// Returns the URL with the given language suffix appended. Pass
    /// an empty string or null to get the bare URL (no suffix), which
    /// is bs.to's default-language form. Use <see cref="GermanCode"/>,
    /// <see cref="SubbedCode"/>, or <see cref="EnglishCode"/> for the
    /// language code.
    /// </summary>
    public static string WithLanguage(string url, string? language)
    {
        var bare = StripLanguage(url).TrimEnd('/');
        return string.IsNullOrEmpty(language) ? bare : bare + "/" + language;
    }

    /// <summary>
    /// Backwards-compat shim: ForLanguage(url, subbed) → WithLanguage
    /// (url, subbed ? "des" : null). Prefer <see cref="WithLanguage"/>
    /// for new code so English variants round-trip correctly.
    /// </summary>
    public static string ForLanguage(string url, bool subbed) =>
        subbed ? WithLanguage(url, SubbedCode) : StripLanguage(url);

    /// <summary>
    /// Toggle between German default (no suffix) and Subbed. Kept for
    /// the few callers that genuinely want a 2-way swap; for general
    /// cross-language URL matching use <see cref="SameLanguageStripped"/>
    /// which handles all three variants uniformly.
    /// </summary>
    public static string Toggle(string url) =>
        IsSubbed(url) ? StripSubbed(url) : AddSubbed(url);

    /// <summary>
    /// True if both URLs reference the same logical resource ignoring
    /// language suffix — i.e. their language-stripped forms match.
    /// Empty / null inputs return false. Use this in preference to
    /// `Toggle(a) == b` patterns since it covers all three language
    /// variants (German, Subbed, English) without caller-side casing.
    /// </summary>
    public static bool SameLanguageStripped(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return string.Equals(
            StripLanguage(a!), StripLanguage(b!),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns a canonical episode key by stripping any trailing
    /// language suffix. German /de, Subbed /des, English /en variants
    /// of the same episode all map to the same key — used to dedupe
    /// per-episode progress so all language variants share a single
    /// saved position.
    /// </summary>
    public static string NormalizeEpisodeKey(string url) => StripLanguage(url);
}

public class BsToService
{
    private readonly HttpClient _http;

    // Session-scope cache of successful /series fetches. Keyed by the
    // bs.to series URL (case-insensitive); each entry carries a UTC
    // timestamp so we can age it out — past the TTL the cache is
    // treated as a miss and the next open re-fetches, which is how a
    // newly-uploaded episode reaches the user without an app restart.
    // Lifetime of the BsToService instance is the app's lifetime
    // (singleton-ish via the VM); within that the TTL caps how stale
    // any individual entry can get.
    private readonly Dictionary<string, (SeriesInfo Info, DateTime FetchedAtUtc)> _seriesCache =
        new(StringComparer.OrdinalIgnoreCase);

    // Cache age past which an entry is considered stale and refetched
    // on the next open. 15 min covers a typical viewing session
    // (repeat opens within a sitting are instant) while keeping the
    // worst-case staleness short enough that a freshly-uploaded
    // episode shows up the next time the user opens the picker.
    private static readonly TimeSpan SeriesCacheTtl = TimeSpan.FromMinutes(15);

    public BsToService(HttpClient http) => _http = http;

    /// <summary>
    /// Synchronous cache check — returns true and the cached info if a
    /// previous <see cref="GetSeriesAsync"/> call for the same URL
    /// completed successfully AND the cached entry is still within the
    /// TTL window. Lets callers skip loading UI entirely on repeat
    /// opens.
    /// </summary>
    public bool TryGetCachedSeries(string seriesUrl, out SeriesInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(seriesUrl)) return false;
        if (!_seriesCache.TryGetValue(seriesUrl, out var entry)) return false;
        if (DateTime.UtcNow - entry.FetchedAtUtc > SeriesCacheTtl) return false;
        info = entry.Info;
        return true;
    }

    /// <summary>
    /// Searches bs.to's series directory by title substring. The Python
    /// service caches the full directory (~1.3 MB) so this is a fast
    /// in-memory filter after the first call. bs.to itself has no
    /// HTTP-GET search endpoint, hence the directory-and-filter approach
    /// that matches every working scraper out there.
    /// </summary>
    public async Task<IReadOnlyList<VideoResult>> SearchAsync(
        string query, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<VideoResult>();
        // Clamp matches the server's bounds (1..2000) so we don't send a
        // value that would 422 — anything outside the range falls back
        // to the default 30 if we silently drop the param. Clamping
        // here means a misconfigured ResultsPerSource still surfaces
        // some results.
        var clamped = Math.Clamp(limit, 1, 2000);
        var url = $"{AppConfig.YtDlpApiUrl.TrimEnd('/')}/bsto/search"
                  + $"?q={Uri.EscapeDataString(query)}"
                  + $"&limit={clamped}";

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<VideoResult>();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
                return Array.Empty<VideoResult>();

            var list = new List<VideoResult>();
            foreach (var r in results.EnumerateArray())
            {
                var pageUrl = Str(r, "url");
                if (string.IsNullOrWhiteSpace(pageUrl)) continue;
                list.Add(new VideoResult
                {
                    Title = Str(r, "title") ?? pageUrl!,
                    Source = EngineDisplayName.For("bsto"),
                    PageUrl = pageUrl!,
                    ThumbnailUrl = Str(r, "thumbnailUrl"),
                    Kind = VideoKind.Series,
                });
            }
            return list;
        }
        catch (HttpRequestException) { return Array.Empty<VideoResult>(); }
        catch (TaskCanceledException) { return Array.Empty<VideoResult>(); }
        catch (JsonException) { return Array.Empty<VideoResult>(); }
    }

    public async Task<SeriesInfo?> GetSeriesAsync(string seriesUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(seriesUrl)) return null;

        // Cache hit AND within TTL — return synchronously (well, on the
        // next await continuation) so callers don't re-hit the API on
        // every open. Stale entries (past TTL) fall through and refetch
        // so newly-uploaded episodes reach the user.
        if (TryGetCachedSeries(seriesUrl, out var cached) && cached is not null)
            return cached;

        var url = $"{AppConfig.YtDlpApiUrl.TrimEnd('/')}/series"
                  + $"?url={Uri.EscapeDataString(seriesUrl)}";

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var title = Str(root, "title") ?? "Series";
            var thumb = Str(root, "thumbnailUrl");
            var language = Str(root, "language");

            var seasons = new List<SeriesSeason>();
            if (root.TryGetProperty("seasons", out var seasonArr)
                && seasonArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in seasonArr.EnumerateArray())
                {
                    var seasonNo = Int(s, "number");
                    var episodes = new List<SeriesEpisode>();
                    if (s.TryGetProperty("episodes", out var epArr)
                        && epArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in epArr.EnumerateArray())
                        {
                            var epUrl = Str(e, "url");
                            if (string.IsNullOrWhiteSpace(epUrl)) continue;
                            // `available` defaults to true when the
                            // python doesn't return the field (older
                            // container) so the picker degrades to
                            // its previous always-enabled behavior.
                            var available = true;
                            if (e.TryGetProperty("available", out var av)
                                && av.ValueKind == JsonValueKind.False)
                                available = false;
                            episodes.Add(new SeriesEpisode(
                                Number: Int(e, "number"),
                                Title: Str(e, "title") ?? "",
                                Language: Str(e, "language"),
                                Url: epUrl!,
                                Available: available));
                        }
                    }
                    if (episodes.Count > 0)
                        seasons.Add(new SeriesSeason(seasonNo, episodes));
                }
            }

            var availableLanguages = new List<string>();
            if (root.TryGetProperty("availableLanguages", out var alArr)
                && alArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var lv in alArr.EnumerateArray())
                {
                    if (lv.ValueKind == JsonValueKind.String)
                    {
                        var s = lv.GetString();
                        if (!string.IsNullOrEmpty(s)) availableLanguages.Add(s);
                    }
                }
            }

            var info = new SeriesInfo(
                title, thumb, seasons,
                Language: language,
                AvailableLanguages: availableLanguages.Count > 0 ? availableLanguages : null);
            // Only cache successful, non-empty fetches — empty results
            // usually mean a transient parse failure on the scraper
            // side, and we'd rather retry on the next open than serve
            // a "no episodes" cache. Stamp UtcNow so the TTL check in
            // TryGetCachedSeries can age this entry out later.
            if (seasons.Count > 0)
                _seriesCache[seriesUrl] = (info, DateTime.UtcNow);
            return info;
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static int Int(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return 0;
        return v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    }
}
