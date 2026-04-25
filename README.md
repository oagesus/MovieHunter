# MovieHunter

Search for a movie, click play, watch it — all in one window.

When you pick a result, [yt-dlp](https://github.com/yt-dlp/yt-dlp)
follows the movie page through any embed iframes to the actual video host,
extracts the direct stream URL, and the app plays it in its built-in player.

## Sources

- HDfilme (hdfilme.win)

## Features

- Inline video playback (no external player), with play/pause, ±10 s skip,
  mute, vertical volume slider, fullscreen
- Recently-watched view with poster cards, progress bar, and resume from
  the saved timestamp
- Theme picker — System / Light / Dark / Dracula / Catppuccin / Netflix /
  Prime Video / Disney+
- Optional TMDB integration for cross-language title matching (translates
  search queries so e.g. *Pirates of the Caribbean* finds *Fluch der Karibik*)

## Setup

```bash
git clone https://github.com/oagesus/MovieHunter.git
cd MovieHunter
cp docker/searxng/settings.yml.example docker/searxng/settings.yml
docker compose -f docker/docker-compose.yml up -d --build
dotnet run
```

## License

[MIT](LICENSE)
