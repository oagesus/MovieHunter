using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MovieHunter.Services;

/// <summary>
/// A TMDb match with the title variants we need to query downstream sources
/// in multiple languages.
/// </summary>
public record TmdbMovie(
    int Id,
    string OriginalTitle,
    string? GermanTitle,
    string? EnglishTitle,
    string? Year,
    string? Overview,
    string? PosterUrl,
    double Popularity,
    double? VoteAverage,
    string? ImdbId,
    int? CollectionId)
{
    /// <summary>Unique set of titles to use when searching downstream sources.</summary>
    public IReadOnlyList<string> QueryTitles
    {
        get
        {
            var list = new List<string>();
            void add(string? s) { if (!string.IsNullOrWhiteSpace(s) && !list.Contains(s!)) list.Add(s!); }
            add(OriginalTitle);
            add(GermanTitle);
            add(EnglishTitle);
            return list;
        }
    }
}

public class TmdbClient
{
    private const string Base = "https://api.themoviedb.org/3";
    private const string ImageBase = "https://image.tmdb.org/t/p/w342";

    private readonly HttpClient _http;

    public TmdbClient(HttpClient http) => _http = http;

    /// <summary>
    /// Verifies an API key by hitting the lightweight /configuration endpoint.
    /// Returns true if TMDb accepts the key.
    /// </summary>
    public async Task<bool> ValidateKeyAsync(string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return false;
        var url = $"{Base}/configuration?api_key={Uri.EscapeDataString(apiKey)}";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// Searches TMDb for movies matching the query. Returns up to `cap` results
    /// sorted by popularity descending. Returns empty on error or missing key.
    /// </summary>
    public async Task<IReadOnlyList<TmdbMovie>> SearchMoviesAsync(
        string apiKey, string query, string? year, int cap, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(query))
            return Array.Empty<TmdbMovie>();

        var url = $"{Base}/search/movie?api_key={Uri.EscapeDataString(apiKey)}" +
                  $"&query={Uri.EscapeDataString(query)}";
        if (!string.IsNullOrWhiteSpace(year))
            url += $"&year={Uri.EscapeDataString(year)}";

        List<TmdbMovie> results;
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<TmdbMovie>();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("results", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<TmdbMovie>();

            results = new List<TmdbMovie>();
            foreach (var e in arr.EnumerateArray())
            {
                var id = e.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number
                    ? idProp.GetInt32() : 0;
                if (id == 0) continue;

                var originalTitle = e.TryGetProperty("original_title", out var ot)
                    ? ot.GetString() ?? "" : "";
                var title = e.TryGetProperty("title", out var t) ? t.GetString() : null;
                var release = e.TryGetProperty("release_date", out var r)
                    ? r.GetString() : null;
                var overview = e.TryGetProperty("overview", out var ov) ? ov.GetString() : null;
                var posterPath = e.TryGetProperty("poster_path", out var pp)
                    ? pp.GetString() : null;
                var popularity = e.TryGetProperty("popularity", out var pop) && pop.ValueKind == JsonValueKind.Number
                    ? pop.GetDouble() : 0;
                var vote = e.TryGetProperty("vote_average", out var va) && va.ValueKind == JsonValueKind.Number
                    ? va.GetDouble() : (double?)null;

                results.Add(new TmdbMovie(
                    Id: id,
                    OriginalTitle: originalTitle,
                    GermanTitle: null,
                    EnglishTitle: title,
                    Year: release?.Length >= 4 ? release[..4] : null,
                    Overview: overview,
                    PosterUrl: !string.IsNullOrEmpty(posterPath) ? ImageBase + posterPath : null,
                    Popularity: popularity,
                    VoteAverage: vote,
                    ImdbId: null,
                    CollectionId: null));
            }
        }
        catch (HttpRequestException) { return Array.Empty<TmdbMovie>(); }
        catch (TaskCanceledException) { return Array.Empty<TmdbMovie>(); }

        // Sort by popularity desc and cap. Filter out very low-popularity
        // amateur/noise entries (e.g. YouTuber clips titled the same as a movie).
        results = results
            .Where(m => m.Popularity >= 0.5)
            .OrderByDescending(m => m.Popularity)
            .Take(cap)
            .ToList();

        // Enrich with German title, IMDb ID and collection membership.
        await Task.WhenAll(results.Select(async (m, i) =>
        {
            var (de, imdb, collection) = await GetDetailsAsync(apiKey, m.Id, ct);
            results[i] = m with { GermanTitle = de, ImdbId = imdb, CollectionId = collection };
        }));

        return results;
    }

    /// <summary>
    /// Fetches the German title, IMDb ID, and collection ID (if any)
    /// for a single movie. Returns (null, null, null) on failure.
    /// </summary>
    private async Task<(string? GermanTitle, string? ImdbId, int? CollectionId)>
        GetDetailsAsync(string apiKey, int tmdbId, CancellationToken ct)
    {
        var url = $"{Base}/movie/{tmdbId}?api_key={Uri.EscapeDataString(apiKey)}" +
                  "&language=de-DE&append_to_response=external_ids";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return (null, null, null);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            string? german = null;
            if (root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                german = t.GetString();

            string? imdb = null;
            if (root.TryGetProperty("external_ids", out var ext)
                && ext.TryGetProperty("imdb_id", out var im)
                && im.ValueKind == JsonValueKind.String)
                imdb = im.GetString();

            int? collectionId = null;
            if (root.TryGetProperty("belongs_to_collection", out var col)
                && col.ValueKind == JsonValueKind.Object
                && col.TryGetProperty("id", out var cid)
                && cid.ValueKind == JsonValueKind.Number)
                collectionId = cid.GetInt32();

            return (german, imdb, collectionId);
        }
        catch (HttpRequestException) { return (null, null, null); }
        catch (TaskCanceledException) { return (null, null, null); }
    }

    /// <summary>
    /// A TMDb collection (franchise) with its localized name and movies.
    /// </summary>
    public record TmdbCollection(
        int Id,
        string? GermanName,
        string? EnglishName,
        IReadOnlyList<TmdbMovie> Parts);

    /// <summary>
    /// Fetches a TMDb collection — name in German AND English + all parts.
    /// Makes two parallel API calls (one per language) to get both names.
    /// </summary>
    public async Task<TmdbCollection?> GetCollectionAsync(
        string apiKey, int collectionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var germanTask = FetchOneCollectionAsync(apiKey, collectionId, "de-DE", ct);
        var englishTask = FetchOneCollectionAsync(apiKey, collectionId, "en-US", ct);
        await Task.WhenAll(germanTask, englishTask);

        var german = await germanTask;
        var english = await englishTask;
        if (german is null && english is null) return null;

        // Merge the two parts lists by movie ID: German record's
        // GermanTitle + English record's EnglishTitle on the same TmdbMovie.
        var byId = new Dictionary<int, TmdbMovie>();
        foreach (var m in german?.Parts ?? Array.Empty<TmdbMovie>())
            byId[m.Id] = m;
        foreach (var m in english?.Parts ?? Array.Empty<TmdbMovie>())
        {
            if (byId.TryGetValue(m.Id, out var existing))
                byId[m.Id] = existing with { EnglishTitle = m.EnglishTitle };
            else
                byId[m.Id] = m;
        }
        var parts = byId.Values.OrderBy(m => m.Year ?? "9999").ToList();

        return new TmdbCollection(
            Id: collectionId,
            GermanName: german?.Name,
            EnglishName: english?.Name,
            Parts: parts);
    }

    private async Task<(string? Name, IReadOnlyList<TmdbMovie> Parts)?>
        FetchOneCollectionAsync(string apiKey, int collectionId, string language, CancellationToken ct)
    {
        var url = $"{Base}/collection/{collectionId}?api_key={Uri.EscapeDataString(apiKey)}" +
                  $"&language={language}";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            string? name = null;
            if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                name = n.GetString();

            var results = new List<TmdbMovie>();
            if (root.TryGetProperty("parts", out var parts)
                && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in parts.EnumerateArray())
                {
                    var id = p.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number
                        ? idProp.GetInt32() : 0;
                    if (id == 0) continue;

                    var title = p.TryGetProperty("title", out var t) ? t.GetString() : null;
                    var originalTitle = p.TryGetProperty("original_title", out var ot)
                        ? ot.GetString() ?? "" : "";
                    var release = p.TryGetProperty("release_date", out var r) ? r.GetString() : null;
                    var overview = p.TryGetProperty("overview", out var ov) ? ov.GetString() : null;
                    var posterPath = p.TryGetProperty("poster_path", out var pp)
                        ? pp.GetString() : null;
                    var popularity = p.TryGetProperty("popularity", out var pop) && pop.ValueKind == JsonValueKind.Number
                        ? pop.GetDouble() : 0;
                    var vote = p.TryGetProperty("vote_average", out var va) && va.ValueKind == JsonValueKind.Number
                        ? va.GetDouble() : (double?)null;

                    results.Add(new TmdbMovie(
                        Id: id,
                        OriginalTitle: originalTitle,
                        GermanTitle: language == "de-DE" ? title : null,
                        EnglishTitle: language == "en-US" ? title : originalTitle,
                        Year: release?.Length >= 4 ? release[..4] : null,
                        Overview: overview,
                        PosterUrl: !string.IsNullOrEmpty(posterPath) ? ImageBase + posterPath : null,
                        Popularity: popularity,
                        VoteAverage: vote,
                        ImdbId: null,
                        CollectionId: collectionId));
                }
            }

            return (name, (IReadOnlyList<TmdbMovie>)results
                .OrderBy(m => m.Year ?? "9999")
                .ToList());
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
    }
}
