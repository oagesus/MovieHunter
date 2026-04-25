from fastapi import FastAPI, HTTPException, Query
from pydantic import BaseModel
import yt_dlp

app = FastAPI(title="MovieHunter yt-dlp API")


class ExtractResponse(BaseModel):
    url: str
    audio_url: str | None = None
    http_user_agent: str | None = None
    http_referer: str | None = None
    title: str | None = None
    duration: float | None = None
    extractor: str | None = None


@app.get("/healthz")
def healthz():
    return {"ok": True, "yt_dlp_version": yt_dlp.version.__version__}


@app.get("/extract", response_model=ExtractResponse)
def extract(url: str = Query(..., description="Page URL to resolve to a direct stream")):
    opts = {
        "quiet": True,
        "no_warnings": True,
        "noplaylist": True,
        "skip_download": True,
        "extractor_args": {
            # `tv` is SABR-free for most public videos; the PO-token plugin
            # covers `default`/`web_embedded` fallbacks via the bgutil sidecar.
            "youtube": {"player_client": ["tv", "default", "web_embedded"]},
            "youtubepot-bgutilhttp": {"base_url": ["http://bgutil:4416"]},
        },
    }
    try:
        with yt_dlp.YoutubeDL(opts) as ydl:
            info = ydl.extract_info(url, download=False)
    except yt_dlp.utils.DownloadError as e:
        raise HTTPException(status_code=404, detail=str(e))
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"{type(e).__name__}: {e}")

    picked = _pick_streams(info)
    if not picked["url"]:
        raise HTTPException(status_code=404, detail="No playable stream URL found")

    return ExtractResponse(
        url=picked["url"],
        audio_url=picked["audio_url"],
        http_user_agent=picked["http_user_agent"],
        http_referer=picked["http_referer"],
        title=info.get("title"),
        duration=info.get("duration"),
        extractor=info.get("extractor"),
    )


def _pick_streams(info: dict):
    formats = info.get("formats") or []

    def vcodec(f): return f.get("vcodec") or "none"
    def acodec(f): return f.get("acodec") or "none"

    def headers_of(f):
        h = f.get("http_headers") or {}
        return h.get("User-Agent"), h.get("Referer")

    usable = [f for f in formats if f.get("url")]

    # Prefer any single-file combined format, regardless of protocol
    combined = [f for f in usable if vcodec(f) != "none" and acodec(f) != "none"]
    combined.sort(key=lambda f: (f.get("height") or 0, f.get("tbr") or 0), reverse=True)
    if combined:
        f = combined[0]
        ua, ref = headers_of(f)
        return {"url": f["url"], "audio_url": None,
                "http_user_agent": ua, "http_referer": ref}

    # Otherwise split streams (video-only + audio-only) — played via VLC slave
    video_only = [f for f in usable if vcodec(f) != "none" and acodec(f) == "none"]
    audio_only = [f for f in usable if vcodec(f) == "none" and acodec(f) != "none"]
    video_only.sort(key=lambda f: (f.get("height") or 0, f.get("tbr") or 0), reverse=True)
    audio_only.sort(key=lambda f: (f.get("abr") or 0, f.get("tbr") or 0), reverse=True)

    if video_only and audio_only:
        v = video_only[0]
        ua, ref = headers_of(v)
        return {"url": v["url"], "audio_url": audio_only[0]["url"],
                "http_user_agent": ua, "http_referer": ref}

    if usable:
        f = usable[0]
        ua, ref = headers_of(f)
        return {"url": f["url"], "audio_url": None,
                "http_user_agent": ua, "http_referer": ref}

    return {"url": info.get("url"), "audio_url": None,
            "http_user_agent": None, "http_referer": None}
