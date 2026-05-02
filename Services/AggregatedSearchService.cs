using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MovieHunter.Models;

namespace MovieHunter.Services;

/// <summary>
/// Aggregates search results from SearXNG-backed engines (hdfilme, etc).
///
/// When TMDb is enabled, it is used for two things:
///   (1) Translation — the user's query is looked up so we can ADD an extra
///       hdfilme query with the franchise name or movie title in the other
///       language. (Nothing is ever filtered out by TMDb.)
///   (2) Ranking — results that match a TMDb candidate (the franchise's
///       films or the standalone matched movie) are sorted to the top of
///       the result list. Non-matching results stay below in their natural
///       order.
/// </summary>
public class AggregatedSearchService
{
    private readonly SearxngClient _sx;
    private readonly TmdbClient _tmdb;
    private readonly BsToService _bsto;

    // Engine name for bs.to (matches the Sources toggle injected in
    // MainWindowViewModel and the EngineDisplayName mapping). bs.to is
    // routed through BsToService — not SearXNG — because bs.to has no
    // usable HTTP-GET search endpoint.
    private const string BstoEngine = "bsto";

    private const int MaxConcurrentQueries = 2;
    private const int MinJitterMs = 120;
    private const int MaxJitterMs = 350;
    private const int MaxPagesPerQuery = 3;
    // Hard cap on parallel TMDb poster lookups per search. bs.to's
    // substring-match directory can return hundreds of hits for short
    // queries (e.g. "A" → ~2k results); fanning out one TMDb request
    // per hit would burst past TMDb's rate limit and stall the search
    // behind a multi-minute Task.WhenAll. Capping the fan-out keeps
    // the search responsive — the first 50 series cards get posters,
    // the rest stay text-only with the dark Border fallback. The
    // episode picker does its own on-demand TMDb lookup for cards
    // that didn't get a poster here, so opening any non-enriched
    // card still surfaces the right artwork on demand.
    private const int MaxTmdbPosterEnrichmentPerSearch = 50;

    public AggregatedSearchService(SearxngClient sx, TmdbClient tmdb, BsToService bsto)
    {
        _sx = sx;
        _tmdb = tmdb;
        _bsto = bsto;
    }

    public IReadOnlyList<string> LastUnresponsiveEngines => _sx.LastUnresponsiveEngines;

    public async IAsyncEnumerable<VideoResult> SearchAsync(
        string title, string? year, SourcesSettings sources,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cap = sources.ResultsPerSource;
        var timeout = AppConfig.SearchTimeoutSeconds;

        var enabledEngines = sources.Entries
            .Where(s => s.Enabled)
            .Select(s => s.Name)
            .ToList();
        // bs.to bypasses SearXNG (no usable HTTP-GET search) — pull it
        // out of the engines list we send to SearXNG, then dispatch it
        // separately below if it's enabled.
        var bstoEnabled = enabledEngines.Any(
            n => string.Equals(n, BstoEngine, StringComparison.OrdinalIgnoreCase));
        var sxEngines = enabledEngines
            .Where(n => !string.Equals(n, BstoEngine, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var hasAnyEntries = sources.Entries.Count > 0;
        IReadOnlyList<string>? engineNames = hasAnyEntries ? sxEngines : null;
        IReadOnlyDictionary<string, int>? caps = hasAnyEntries
            ? sxEngines.ToDictionary(n => n, _ => cap, StringComparer.OrdinalIgnoreCase)
            : null;

        var (queries, candidates) = await BuildQueriesAndCandidatesAsync(title, year, sources, ct);

        using var semaphore = new SemaphoreSlim(MaxConcurrentQueries);
        var rnd = new Random();

        async Task<IReadOnlyList<VideoResult>> QueryAllPages(string q)
        {
            var massaged = MassageQuery(q);
            var all = new List<VideoResult>();
            for (var page = 1; page <= MaxPagesPerQuery; page++)
            {
                var thisPage = page;
                await semaphore.WaitAsync(ct);
                IReadOnlyList<VideoResult> pageHits;
                try
                {
                    await Task.Delay(rnd.Next(MinJitterMs, MaxJitterMs), ct);
                    pageHits = await SafeRun(
                        () => _sx.SearchAsync(massaged, engineNames, caps, timeout, thisPage, ct));
                }
                finally { semaphore.Release(); }

                if (pageHits.Count == 0) break;
                all.AddRange(pageHits);
            }
            return all;
        }

        var tasks = queries.Select(QueryAllPages).ToList();
        // bs.to fans out separately — one call to the Python service
        // per search (the directory + filter happens server-side and
        // is cached). The user's configured "results per source" cap
        // is forwarded as the server's `limit` param so a 1000-cap
        // setting actually surfaces up to 1000 bs.to hits instead of
        // the server's 30-default. Empty list when bs.to is disabled.
        if (bstoEnabled)
        {
            tasks.Add(SafeRun(() => _bsto.SearchAsync(title, cap, ct)));
        }

        // Collect first so we can rank by TMDb-match score before yielding.
        var scored = new List<(VideoResult Hit, double Score, int OriginalOrder)>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderCounter = 0;
        // Series hits without a thumbnail (bs.to's directory is text-only)
        // queued for TMDb poster enrichment. Resolved in parallel after
        // the search aggregation finishes so each lookup runs concurrently
        // instead of blocking the next batch.
        var posterEnrichmentTasks = new List<Task>();
        var canEnrichWithTmdb = sources.TmdbEnabled
            && !string.IsNullOrWhiteSpace(sources.TmdbApiKey);

        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks);
            tasks.Remove(finished);
            var batch = await finished;

            foreach (var hit in batch)
            {
                if (string.IsNullOrWhiteSpace(hit.PageUrl)) continue;
                if (!seenUrls.Add(hit.PageUrl)) continue;

                if (canEnrichWithTmdb
                    && hit.Kind == VideoKind.Series
                    && string.IsNullOrEmpty(hit.ThumbnailUrl)
                    && posterEnrichmentTasks.Count < MaxTmdbPosterEnrichmentPerSearch)
                {
                    var capturedHit = hit;
                    posterEnrichmentTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var poster = await _tmdb.SearchTvPosterAsync(
                                sources.TmdbApiKey, capturedHit.Title, ct);
                            if (!string.IsNullOrEmpty(poster))
                                capturedHit.ThumbnailUrl = poster;
                        }
                        catch { /* TMDb lookup failures fall back to no thumb */ }
                    }, ct));
                }

                var score = candidates.Count > 0 ? BestMatchScore(hit, candidates) : 0;
                scored.Add((hit, score, orderCounter++));
            }
        }

        // Wait for TMDb poster lookups to land before yielding so the
        // search-result row appears with its poster already in place
        // (no late "thumbnail pops in" jump). Bounded by TMDb's response
        // time — for a typical 10-result bs.to batch this adds ~300 ms
        // total since the lookups run in parallel.
        if (posterEnrichmentTasks.Count > 0)
            await Task.WhenAll(posterEnrichmentTasks);

        // Rank: higher score first; among equals, preserve original order.
        scored.Sort((a, b) =>
        {
            var cmp = b.Score.CompareTo(a.Score);
            return cmp != 0 ? cmp : a.OriginalOrder.CompareTo(b.OriginalOrder);
        });

        foreach (var (hit, _, _) in scored) yield return hit;
    }

    /// <summary>
    /// Returns both the hdfilme query list and the list of TMDb candidates
    /// used for ranking. Candidates are either the franchise collection's
    /// films (for franchises) or just the top matched movie.
    /// </summary>
    private async Task<(List<string> Queries, List<TmdbMovie> Candidates)>
        BuildQueriesAndCandidatesAsync(
            string title, string? year, SourcesSettings sources, CancellationToken ct)
    {
        var literal = string.IsNullOrWhiteSpace(year) ? title.Trim() : $"{title.Trim()} {year}";
        var queries = new List<string> { literal };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { literal };
        var candidates = new List<TmdbMovie>();

        if (!sources.TmdbEnabled || string.IsNullOrWhiteSpace(sources.TmdbApiKey))
            return (queries, candidates);

        IReadOnlyList<TmdbMovie> matches;
        try
        {
            matches = await _tmdb.SearchMoviesAsync(sources.TmdbApiKey, title, year, cap: 5, ct);
        }
        catch { return (queries, candidates); }

        if (matches.Count == 0) return (queries, candidates);

        var top = matches[0];
        string? germanName;
        string? englishName;

        if (top.CollectionId is int cid)
        {
            var collection = await _tmdb.GetCollectionAsync(sources.TmdbApiKey, cid, ct);
            if (collection is not null)
            {
                candidates.AddRange(collection.Parts);
                germanName = StripCollectionSuffix(collection.GermanName);
                englishName = StripCollectionSuffix(collection.EnglishName);
            }
            else
            {
                candidates.Add(top);
                germanName = top.GermanTitle;
                englishName = top.EnglishTitle ?? top.OriginalTitle;
            }
        }
        else
        {
            candidates.Add(top);
            germanName = top.GermanTitle;
            englishName = top.EnglishTitle ?? top.OriginalTitle;
        }

        foreach (var name in new[] { germanName, englishName })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (seen.Add(name)) queries.Add(name);
        }

        return (queries, candidates);
    }

    /// <summary>
    /// Highest similarity score between a hit's title and any candidate's
    /// German/English/Original titles. Score in [0, ~1.15] — year match
    /// adds up to +0.15 bonus.
    /// </summary>
    private static double BestMatchScore(VideoResult hit, IReadOnlyList<TmdbMovie> candidates)
    {
        if (string.IsNullOrWhiteSpace(hit.Title)) return 0;

        double best = 0;
        foreach (var m in candidates)
        {
            double titleScore = 0;
            foreach (var cand in new[] { m.GermanTitle, m.OriginalTitle, m.EnglishTitle })
            {
                if (string.IsNullOrWhiteSpace(cand)) continue;
                titleScore = Math.Max(titleScore, TitleSimilarity(hit.Title, cand!));
            }

            // Year bonus.
            if (int.TryParse(hit.Year, out var hy) && int.TryParse(m.Year, out var my))
            {
                var diff = Math.Abs(hy - my);
                if (diff == 0) titleScore += 0.15;
                else if (diff == 1) titleScore += 0.05;
            }

            if (titleScore > best) best = titleScore;
        }
        return best;
    }

    private static double TitleSimilarity(string a, string b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return 0;
        if (na == nb) return 1.0;

        if (na.Contains(nb) && IsSubstantial(nb)) return 0.85;
        if (nb.Contains(na) && IsSubstantial(na)) return 0.85;

        var tokA = na.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokB = nb.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokA.Length == 0 || tokB.Length == 0) return 0;

        var setA = new HashSet<string>(tokA);
        var setB = new HashSet<string>(tokB);
        var shared = setA.Intersect(setB).Count();
        var denom = Math.Min(tokA.Length, tokB.Length);
        return denom == 0 ? 0 : Math.Min(0.85, (double)shared / denom * 0.9);
    }

    private static bool IsSubstantial(string normalized)
    {
        if (normalized.Length < 5) return false;
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length >= 2;
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (char.IsWhiteSpace(c) || c is '-' or ':' or ',' or '.' or '!' or '?' or '\'')
                sb.Append(' ');
        }
        var flat = Regex.Replace(sb.ToString().Trim(), @"\s+", " ");
        flat = Regex.Replace(flat, @"\b(teil|part|pt)\s+", "", RegexOptions.IgnoreCase);
        flat = Regex.Replace(flat, @"\b(x|ix|viii|vii|vi|v|iv|iii|ii|i)\b",
            m => RomanToInt(m.Value).ToString(), RegexOptions.IgnoreCase);
        return Regex.Replace(flat.Trim(), @"\s+", " ");
    }

    private static int RomanToInt(string r) => r.ToLowerInvariant() switch
    {
        "i" => 1, "ii" => 2, "iii" => 3, "iv" => 4, "v" => 5,
        "vi" => 6, "vii" => 7, "viii" => 8, "ix" => 9, "x" => 10,
        _ => 0,
    };

    /// <summary>
    /// hdfilme's substring search treats `&amp;` literally, but its titles
    /// almost always use "and". Normalize before sending.
    /// </summary>
    private static string MassageQuery(string q)
    {
        var result = Regex.Replace(q, @"\s*&\s*", " and ");
        return Regex.Replace(result.Trim(), @"\s+", " ");
    }

    /// <summary>
    /// "The Fast and the Furious Collection" → "The Fast and the Furious"
    /// "Harry Potter Filmreihe" → "Harry Potter"
    /// </summary>
    private static string? StripCollectionSuffix(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var cleaned = Regex.Replace(
            name,
            @"\s*(?:-\s*)?(Filmreihe|Collection|Saga|Trilogy|Trilogie|Quadrilogy|Pentalogy|Series|Serie|Film\s*Series)\s*$",
            "",
            RegexOptions.IgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static async Task<IReadOnlyList<VideoResult>> SafeRun(
        Func<Task<IReadOnlyList<VideoResult>>> fn)
    {
        try { return await fn(); }
        catch { return Array.Empty<VideoResult>(); }
    }
}
