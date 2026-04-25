# MovieHunter

Avalonia desktop app (Windows / Linux / macOS) that searches a self-hosted
SearxNG instance for movies, resolves direct stream URLs through a custom
yt-dlp extractor pipeline, and plays them inline via LibVLC. Includes a
recently-watched list with resume-from-position, multi-flavor theme picker
(System / Light / Dark / Dracula / Catppuccin / Netflix / Prime Video /
Disney+), and Netflix-style player chrome.

## Stack

- **UI**: Avalonia 12 + FluentTheme (.NET 10, `WindowDecorations="None"`,
  custom title bar)
- **Search**: SearxNG with a custom XPath engine for hdfilme.win
- **Extraction**: FastAPI service wrapping yt-dlp, with custom plugins for
  hdfilme → meinecloud → doodstream / mixdrop
- **Playback**: LibVLCSharp with software-render callbacks
- **Persistence**: `%AppData%\MovieHunter\sources.json` (theme, volume,
  TMDb key, source toggles) and `recentlyWatched.json` (watch history +
  resume positions)

## Getting started

### 1. Backend services (Docker)

```bash
cd docker
cp searxng/settings.yml.example searxng/settings.yml
# Generate a fresh secret_key for SearxNG and replace
# REPLACE_WITH_A_RANDOM_64_HEX_CHAR_STRING in settings.yml:
#   openssl rand -hex 32
# (or python -c "import secrets; print(secrets.token_hex(32))")
docker compose up -d --build
```

This starts:
- `redis` — SearxNG cache backend
- `searxng` — search aggregator on `http://localhost:8888`
- `bgutil` — YouTube PO-token sidecar
- `ytdlp-api` — FastAPI extractor on `http://localhost:9000`

### 2. Run the app

```bash
dotnet run
```

## Project layout

```
MovieHunter/
├── App.axaml(.cs)              # Application + theme switching
├── Views/MainWindow.axaml(.cs) # Title bar, search, results, player, recents
├── ViewModels/                 # MainWindowViewModel
├── Services/                   # SearxngClient, YtDlpService, TmdbClient,
│                               # AggregatedSearchService, RecentlyWatched,
│                               # SourcesSettings
├── Models/VideoResult.cs
├── Themes/                     # Dracula, Netflix, Prime Video, Disney+,
│                               # Catppuccin override dictionaries
└── docker/
    ├── docker-compose.yml
    ├── searxng/settings.yml.example
    └── ytdlp-api/
        ├── Dockerfile, main.py
        └── yt_dlp_plugins/extractor/
            ├── testurl.py     # hdfilme.win
            ├── meinecloud.py  # iframe → server list
            ├── doodstream.py
            └── mixdrop.py
```

## License

[MIT](LICENSE)
