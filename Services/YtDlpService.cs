using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MovieHunter.Services;

public record StreamResult(
    string Url,
    string? AudioUrl,
    string? HttpUserAgent,
    string? HttpReferer);

public class YtDlpService
{
    private readonly HttpClient _http;

    public YtDlpService(HttpClient http) => _http = http;

    public async Task<StreamResult?> ExtractStreamUrlAsync(string pageUrl, CancellationToken ct)
    {
        var url = $"{AppConfig.YtDlpApiUrl.TrimEnd('/')}/extract"
                  + $"?url={Uri.EscapeDataString(pageUrl)}";

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var videoUrl = GetStr(doc.RootElement, "url");
            if (string.IsNullOrWhiteSpace(videoUrl)) return null;

            return new StreamResult(
                videoUrl!,
                GetStr(doc.RootElement, "audio_url"),
                GetStr(doc.RootElement, "http_user_agent"),
                GetStr(doc.RootElement, "http_referer"));
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
    }

    /// <summary>
    /// Resolves a Vidmoly wrapper URL (/w/&lt;id&gt;, /v/&lt;id&gt;, /&lt;id&gt;) to
    /// its underlying /embed-&lt;id&gt;.html URL — possibly on a different
    /// mirror domain. Returns null on any failure. Used by the bs.to
    /// captcha overlay's Vidmoly chained flow: Vidmoly added a
    /// Cloudflare Turnstile gate on every /embed- page so the
    /// extractor can't pull the m3u8 server-side anymore; instead we
    /// load the embed URL into the in-app WebView, let Turnstile pass
    /// in a real browser context, and capture the m3u8 there.
    /// </summary>
    public async Task<string?> ResolveVidmolyEmbedUrlAsync(string wrapperUrl, CancellationToken ct)
    {
        var url = $"{AppConfig.YtDlpApiUrl.TrimEnd('/')}/vidmoly/embed"
                  + $"?url={Uri.EscapeDataString(wrapperUrl)}";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return GetStr(doc.RootElement, "embed_url");
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
    }

    private static string? GetStr(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
