using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MovieHunter.Services;

public class SearxngConfigClient
{
    private readonly HttpClient _http;

    public SearxngConfigClient(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<string>> GetVideoEnginesAsync(CancellationToken ct)
    {
        var url = $"{AppConfig.SearxngUrl.TrimEnd('/')}/config";

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<string>();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("engines", out var engines)
                || engines.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            var list = new List<string>();
            foreach (var e in engines.EnumerateArray())
            {
                if (e.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False)
                    continue;
                if (!e.TryGetProperty("categories", out var cats) || cats.ValueKind != JsonValueKind.Array)
                    continue;

                var hasVideos = cats.EnumerateArray().Any(c =>
                    c.ValueKind == JsonValueKind.String &&
                    string.Equals(c.GetString(), "videos", StringComparison.OrdinalIgnoreCase));
                if (!hasVideos) continue;

                if (e.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String)
                {
                    var n = name.GetString();
                    if (!string.IsNullOrEmpty(n)) list.Add(n!);
                }
            }
            return list.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (HttpRequestException) { return Array.Empty<string>(); }
        catch (TaskCanceledException) { return Array.Empty<string>(); }
    }
}
