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
    public const int YtDlpTimeoutSeconds = 60;
}
