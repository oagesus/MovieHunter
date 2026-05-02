using System;

namespace MovieHunter.Services;

public static class AppConfig
{
    public static string SearxngUrl =>
        Environment.GetEnvironmentVariable("MOVIEHUNTER_SEARXNG_URL")
        ?? "http://localhost:8888";

    public static string YtDlpApiUrl =>
        Environment.GetEnvironmentVariable("MOVIEHUNTER_YTDLP_URL")
        ?? "http://localhost:9000";

    public const int HttpTimeoutSeconds = 20;
    public const int SearchTimeoutSeconds = 6;
    // Covers yt-dlp /extract calls. Movies and bs.to-resolved hoster
    // URLs (Voe, Doodstream, etc.) all finish in <10 s; 60 s is plenty
    // of margin. The bs.to captcha solve happens client-side now, in
    // the embedded WebView, and isn't part of the yt-dlp round-trip.
    public const int YtDlpTimeoutSeconds = 60;
}
