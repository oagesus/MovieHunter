using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MovieHunter.Models;

namespace MovieHunter.Services;

public class SearxngClient
{
    private readonly HttpClient _http;

    public IReadOnlyList<string> LastUnresponsiveEngines { get; private set; } = Array.Empty<string>();

    public SearxngClient(HttpClient http) => _http = http;

    public Task<IReadOnlyList<VideoResult>> SearchAsync(
        string query,
        IReadOnlyList<string>? engines,
        IReadOnlyDictionary<string, int>? perEngineCap,
        int timeoutSeconds,
        CancellationToken ct)
        => SearchAsync(query, engines, perEngineCap, timeoutSeconds, pageno: 1, ct);

    public async Task<IReadOnlyList<VideoResult>> SearchAsync(
        string query,
        IReadOnlyList<string>? engines,
        IReadOnlyDictionary<string, int>? perEngineCap,
        int timeoutSeconds,
        int pageno,
        CancellationToken ct)
    {
        var url = $"{AppConfig.SearxngUrl.TrimEnd('/')}/search"
                  + $"?q={Uri.EscapeDataString(query)}"
                  + "&format=json&language=auto"
                  + $"&timeout_limit={Math.Max(1, timeoutSeconds)}"
                  + $"&pageno={Math.Max(1, pageno)}";

        if (engines is { Count: > 0 })
            url += $"&engines={Uri.EscapeDataString(string.Join(",", engines))}";
        else
            url += "&categories=videos";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds) + 3));

        HttpResponseMessage resp;
        try { resp = await _http.GetAsync(url, timeoutCts.Token); }
        catch (HttpRequestException) { return Array.Empty<VideoResult>(); }
        catch (TaskCanceledException) { return Array.Empty<VideoResult>(); }
        if (!resp.IsSuccessStatusCode) return Array.Empty<VideoResult>();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        LastUnresponsiveEngines = ParseUnresponsive(doc.RootElement);

        var list = new List<VideoResult>();
        if (!doc.RootElement.TryGetProperty("results", out var results)) return list;

        var perEngineCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in results.EnumerateArray())
        {
            var u = Str(r, "url");
            if (string.IsNullOrWhiteSpace(u)) continue;

            var engine = Str(r, "engine") ?? "web";

            if (perEngineCap is not null)
            {
                if (!perEngineCap.TryGetValue(engine, out var cap))
                    continue; // result tagged with an engine we didn't request

                perEngineCount.TryGetValue(engine, out var count);
                if (count >= cap) continue;
                perEngineCount[engine] = count + 1;
            }

            var content = Str(r, "content");
            var (parsedYear, parsedDuration) = ParseYearAndDuration(content);

            list.Add(new VideoResult
            {
                Title = Str(r, "title") ?? u!,
                Source = EngineDisplayName.For(engine),
                PageUrl = u!,
                ThumbnailUrl = ResolveUrl(Str(r, "thumbnail"), u!),
                Duration = Str(r, "length") ?? parsedDuration,
                Year = parsedYear,
            });
        }
        return list;
    }

    private static string? Str(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>
    /// Resolves a thumbnail URL against the result's page URL when the
    /// thumbnail is relative ("/uploads/...") or protocol-relative
    /// ("//static.hdfilme.win/..."). Returns null/unchanged if absolute.
    /// </summary>
    private static string? ResolveUrl(string? thumb, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(thumb)) return null;
        if (thumb.StartsWith("http://") || thumb.StartsWith("https://")) return thumb;
        if (thumb.StartsWith("//")) return "https:" + thumb;

        try
        {
            var page = new Uri(pageUrl);
            return new Uri(page, thumb).ToString();
        }
        catch { return thumb; }
    }

    /// <summary>
    /// Extracts year (e.g. "2023") and duration (e.g. "141 min") from the
    /// content text hdfilme's meta div provides — something like
    /// "2023 141 min HD". Returns nulls if not found.
    /// </summary>
    private static (string? Year, string? Duration) ParseYearAndDuration(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return (null, null);

        string? year = null;
        var yearMatch = Regex.Match(content, @"\b(19|20)\d{2}\b");
        if (yearMatch.Success) year = yearMatch.Value;

        string? duration = null;
        var durationMatch = Regex.Match(content, @"(\d+)\s*min", RegexOptions.IgnoreCase);
        if (durationMatch.Success) duration = $"{durationMatch.Groups[1].Value} min";

        return (year, duration);
    }

    private static IReadOnlyList<string> ParseUnresponsive(JsonElement root)
    {
        if (!root.TryGetProperty("unresponsive_engines", out var ue)
            || ue.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var item in ue.EnumerateArray())
        {
            // Each entry is typically ["engine_name", "reason"]
            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 0
                && item[0].ValueKind == JsonValueKind.String)
            {
                var name = item[0].GetString();
                if (!string.IsNullOrEmpty(name)) list.Add(name!);
            }
            else if (item.ValueKind == JsonValueKind.String)
            {
                var name = item.GetString();
                if (!string.IsNullOrEmpty(name)) list.Add(name!);
            }
        }
        return list;
    }
}
