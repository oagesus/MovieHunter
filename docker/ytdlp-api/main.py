from contextlib import asynccontextmanager
from fastapi import FastAPI, HTTPException, Query
from pydantic import BaseModel
import re
import time
import threading
import httpx
from bs4 import BeautifulSoup
import yt_dlp

# Realistic browser headers used for bs.to scraping. Anything that looks
# like a default Python User-Agent gets a 403 / captcha on bs.to.
BROWSER_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/131.0.0.0 Safari/537.36"
    ),
    "Accept-Language": "de-DE,de;q=0.9,en-US;q=0.8,en;q=0.7",
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
}


# Module-level persistent httpx.Client for bs.to fetches. Sharing one
# Client across /series calls keeps DNS / TLS / connection pool / and
# (critically) bs.to's DDoS-Guard session cookies warm. bs.to is
# protected by DDoS-Guard (Server: ddos-guard, sets __ddg1_/__ddg8_/
# __ddg9_/__ddg10_ cookies) — its first response to a fresh session
# is a "cold" response that may be missing dynamic elements; once
# those __ddg* cookies are set, subsequent requests get the proper
# page. Per-call Clients created inside `with httpx.Client():` were
# cold every call, so the first show in a container session reliably
# hit the cold response. httpx.Client is documented as thread-safe;
# cookies are kept in a shared jar that's safe for concurrent
# reads/writes. Initialised eagerly at module import so it's ready
# before any FastAPI worker threads start handling requests.
_BSTO_CLIENT = httpx.Client(
    headers=BROWSER_HEADERS,
    follow_redirects=True,
    timeout=15.0,
)


def _warm_bsto_session() -> None:
    """Pre-warms the module-level bs.to Client by hitting bs.to's
    home page AND the /serie directory. The first request gets
    DDoS-Guard's __ddg* cookies set on the shared jar; subsequent
    requests (including the actual /series fetches) reuse them and
    bypass the cold-session interstitial DDoS-Guard would otherwise
    serve. Hitting multiple URLs primes both / and /serie path
    scopes and gives DDoS-Guard enough rounds to issue all cookies.
    Best-effort: failures are swallowed so a flaky first-boot bs.to
    doesn't keep the container down.
    """
    for warm_url in ("https://bs.to/", "https://bs.to/serie", "https://bs.to/"):
        try:
            _BSTO_CLIENT.get(warm_url)
        except Exception:
            pass


@asynccontextmanager
async def _lifespan(app: FastAPI):
    # Startup: warm the bs.to client BEFORE serving any requests.
    # Using the modern lifespan API (rather than the deprecated
    # @app.on_event("startup")) so the warm-up is guaranteed to run
    # to completion before uvicorn starts accepting connections.
    _warm_bsto_session()
    yield
    # Shutdown: close the persistent client cleanly so connections
    # don't leak when the container shuts down.
    try:
        _BSTO_CLIENT.close()
    except Exception:
        pass


app = FastAPI(title="MovieHunter yt-dlp API", lifespan=_lifespan)

# bs.to series-directory cache. The directory page (https://bs.to/serie)
# lists every series on the site as <a href="serie/<slug>" title="...">.
# It's ~1.3 MB so we fetch it once, hold for an hour, and filter
# locally for every search.
_BSTO_DIR_TTL_SECONDS = 3600
_bsto_dir_lock = threading.Lock()
_bsto_dir_cache: list[dict] = []
_bsto_dir_cached_at: float = 0.0


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


@app.get("/vidmoly/embed")
def vidmoly_embed(url: str = Query(..., description="Vidmoly wrapper URL (/w/<id>, /v/<id>, or /<id>)")):
    """Resolves a Vidmoly wrapper URL to its underlying /embed-<id>.html
    URL. Vidmoly added a Cloudflare Turnstile gate in front of the embed
    pages, so we can't extract the m3u8 server-side anymore — the C# app
    loads the embed URL into its WebView2 control instead, lets Turnstile
    pass in a real browser context, and captures the m3u8 from the
    JWPlayer setup. The wrapper page (/v/<id>, Nuxt SPA) is NOT gated and
    serializes the actual embed URL into __NUXT_DATA__ — possibly on a
    DIFFERENT mirror (e.g. /w/ on .me points at /embed- on .biz), so we
    can't synthesize it from the request host. We have to read the SSR
    payload."""
    m = re.match(
        r"https?://(?:www\.)?(?:vidmoly\.[a-z]+|vmoly\.[a-z]+)/(?:embed-|w/|v/)?([a-z0-9]+)",
        url, re.IGNORECASE,
    )
    if not m:
        raise HTTPException(status_code=400, detail="not a Vidmoly URL")
    code = m.group(1)
    # Try the Nuxt /v/ wrapper first (the only form we've seen serialize
    # the embed URL on every mirror). Fall through to a same-host synth
    # if parsing fails so we still return SOMETHING useful.
    try:
        host_match = re.match(r"https?://(?:www\.)?([^/]+)", url)
        host = host_match.group(1) if host_match else "vidmoly.me"
        wrapper = f"https://{host}/v/{code}"
        with httpx.Client(headers=BROWSER_HEADERS, follow_redirects=True, timeout=15.0) as c:
            r = c.get(wrapper)
        if r.status_code == 200:
            ssr = re.search(
                r'https?://[^\s"\'<>\\]+/embed-[A-Za-z0-9]+(?:\.html)?',
                r.text,
            )
            if ssr:
                return {"embed_url": ssr.group(0), "code": code}
    except Exception:
        pass
    # Last-resort fallback — same-host synth. Cross-mirror /w/ pages
    # need the SSR match above; this fires when SSR fetch failed and
    # is at least a reasonable guess.
    return {"embed_url": f"https://{host}/embed-{code}.html", "code": code}


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


@app.get("/bsto/diagnose")
def bsto_diagnose(url: str = Query(..., description="bs.to episode URL to test")):
    """Diagnostic endpoint that runs the full SeleniumBase resolve and
    returns a structured report (success/error/step). Use this with
    `curl http://localhost:8080/bsto/diagnose?url=<encoded>` to see
    exactly where the captcha pipeline is failing — no yt-dlp wrapping
    in the way."""
    import os
    out: dict = {
        "url": url,
        "extension_dir": os.environ.get("BSTO_RECAPTCHA_EXTENSION", "/opt/recaptcha-solver"),
    }
    try:
        from yt_dlp_plugins.extractor.bsto import _resolve_via_seleniumbase
    except Exception as e:
        out["error"] = f"failed to import bsto extractor: {e}"
        return out

    out["extension_dir_exists"] = os.path.isdir(out["extension_dir"])
    if out["extension_dir_exists"]:
        try:
            out["extension_files"] = sorted(os.listdir(out["extension_dir"]))[:20]
        except Exception:
            pass

    try:
        hoster_url, title = _resolve_via_seleniumbase(url)
        out["success"] = True
        out["hoster_url"] = hoster_url
        out["title"] = title
    except Exception as e:
        out["success"] = False
        out["error"] = f"{type(e).__name__}: {e}"
    return out


@app.get("/bsto/search")
def bsto_search(
    q: str = Query(..., description="search query (substring of series title)"),
    limit: int = Query(
        30,
        ge=1,
        le=2000,
        description="max number of hits to return (clamped to [1, 2000])",
    ),
):
    """Substring search across the cached bs.to series directory.

    bs.to has no usable HTTP-GET search endpoint, so we fetch the full
    A-Z directory at https://bs.to/serie once (cached for an hour) and
    filter locally. Returns up to ``limit`` hits ranked by how early in
    the title the query matches (prefix > substring). Default 30 keeps
    short / common queries from flooding the UI when no explicit cap is
    sent; the C# AggregatedSearchService passes the user's configured
    "results per source" value, which is typically much higher.
    """
    needle = (q or "").strip().lower()
    if not needle:
        return {"results": []}

    try:
        directory = _bsto_directory()
    except httpx.HTTPError as e:
        raise HTTPException(status_code=502, detail=f"bs.to directory fetch failed: {e}")

    matches: list[tuple[int, dict]] = []
    for entry in directory:
        title_lower = entry["title_lower"]
        idx = title_lower.find(needle)
        if idx < 0:
            continue
        # Lower index = better match. Prefix-of-word bumps over mid-word.
        rank = idx
        if idx == 0 or (idx > 0 and not title_lower[idx - 1].isalnum()):
            rank -= 100
        matches.append((rank, entry))

    matches.sort(key=lambda m: (m[0], m[1]["title"]))
    return {
        "results": [
            {"title": e["title"], "url": e["url"], "thumbnailUrl": None}
            for _, e in matches[:limit]
        ]
    }


def _bsto_directory() -> list[dict]:
    """Lazily-loaded + TTL-cached series directory.

    Each entry: {"title": str, "title_lower": str, "url": str (absolute,
    points at https://bs.to/serie/<slug>)}.
    """
    global _bsto_dir_cache, _bsto_dir_cached_at
    now = time.monotonic()
    with _bsto_dir_lock:
        if _bsto_dir_cache and (now - _bsto_dir_cached_at) < _BSTO_DIR_TTL_SECONDS:
            return _bsto_dir_cache

        with httpx.Client(headers=BROWSER_HEADERS, follow_redirects=True, timeout=20.0) as client:
            resp = client.get("https://bs.to/serie")
            resp.raise_for_status()
        soup = BeautifulSoup(resp.text, "html.parser")

        entries: list[dict] = []
        seen_urls: set[str] = set()
        # bs.to's directory page wraps each genre/letter group in
        # <div class="genre"><ul><li><a href="serie/<slug>" ...>Title</a>.
        for a in soup.select("#seriesContainer .genre ul li a[href]"):
            href = a.get("href", "").strip()
            title = (a.get("title") or a.get_text() or "").strip()
            if not href or not title:
                continue
            if "serie/" not in href:
                continue
            url = _absolutize(href, "https://bs.to/")
            if url in seen_urls:
                continue
            seen_urls.add(url)
            entries.append({
                "title": title,
                "title_lower": title.lower(),
                "url": url,
            })

        # Fallback: if the .genre selector returned nothing (HTML
        # changed), pull every distinct serie/<slug> link instead.
        if not entries:
            for a in soup.find_all("a", href=True):
                href = a["href"].strip()
                if not re.match(r"^/?serie/[\w-]+/?$", href):
                    continue
                title = (a.get("title") or a.get_text() or "").strip()
                if not title:
                    continue
                url = _absolutize(href, "https://bs.to/")
                if url in seen_urls:
                    continue
                seen_urls.add(url)
                entries.append({
                    "title": title,
                    "title_lower": title.lower(),
                    "url": url,
                })

        _bsto_dir_cache = entries
        _bsto_dir_cached_at = now
        return entries


@app.get("/series")
def series(url: str = Query(..., description="bs.to series page URL")):
    """Returns season + episode listings for a bs.to series page.

    Selectors verified against the live site:
      title        : section.serie h2  (the show's name)
      cover        : section.serie #sp_right img
      season tabs  : #seasons ul li.s<N> a  (class encodes season number)
      season page  : same URL with /<lang>/ suffix; fetch each season URL
                     and parse table.episodes tr
      episode num  : tr td:nth-child(1) a  (link text is the number)
      episode title: tr td:nth-child(2) a strong

    bs.to has no per-row language flag — language is whole-page via the
    URL suffix /de or /en. We default to /de (the German dub) since
    bs.to's primary audience is German viewers.
    """
    if not url.startswith("http"):
        raise HTTPException(status_code=400, detail="Invalid bs.to URL")

    # Capture the language hint from the URL. bs.to's known suffixes:
    #   /de     → German (also no-suffix is treated as German default)
    #   /des    → Subbed (Japanese audio + German subs); some shows
    #             expose this as /de/sub instead.
    #   /en     → English
    #   /jps    → English Sub (Japanese audio + English subs); some
    #             shows expose this as /ens or /en/sub instead.
    # Resolved against actual page <select> options below since bs.to
    # is inconsistent about exact value strings across shows.
    trimmed_url = url.rstrip("/").lower()
    url_wants_english_sub = (
        trimmed_url.endswith("/jps")
        or trimmed_url.endswith("/ens")
        or trimmed_url.endswith("/en/sub"))
    url_wants_subbed = (
        not url_wants_english_sub
        and (trimmed_url.endswith("/des") or trimmed_url.endswith("/de/sub")))
    url_wants_english = (
        not url_wants_english_sub
        and not url_wants_subbed
        and trimmed_url.endswith("/en"))
    url_wants_german = (
        not url_wants_english_sub
        and not url_wants_subbed
        and not url_wants_english
        and trimmed_url.endswith("/de"))

    # Normalise to the language-anchored series root, so /serie/<slug>,
    # /serie/<slug>/des, and /serie/<slug>/1/de all resolve to the
    # canonical series page.
    series_root = re.sub(
        r"^(https?://[^/]+/serie/[\w-]+).*$", r"\1", url, count=1, flags=re.IGNORECASE)

    try:
        # Use the module-level persistent client so the first /series
        # call benefits from the startup warm-up (cookies / TLS /
        # connection pool already populated). Per-call `with
        # httpx.Client()` blocks would discard that warm state and
        # land on bs.to's cold-session HTML, which is missing the
        # <link rel="canonical"> meta the validator needs.
        client = _BSTO_CLIENT

        base_resp = client.get(series_root)
        base_resp.raise_for_status()
        base_soup = BeautifulSoup(base_resp.text, "html.parser")

        title = _bsto_title(base_soup) or "Series"
        thumb = _bsto_thumb(base_soup, series_root)
        season_numbers = _bsto_season_numbers(base_soup)
        available_languages = _bsto_available_languages(base_soup)

        # Mirror bs.to's <select.series-language> options directly
        # to the C# layer — no server-side phantom-validation. bs.to
        # is the source of truth for what's offered, and any signal
        # we tried to derive from the response HTML to flag phantom
        # variants (canonical URL meta, episode link suffix, etc.)
        # turned out unreliable. If bs.to lists a phantom option, the
        # C# layer catches it reactively on the user's first click
        # and persists the flag via BsToPhantomLanguages so it stays
        # hidden across restarts.

        # Resolve the URL's language hint against the page's options.
        # Order matters — English Sub first ("jps"/"ens"/"en/sub"),
        # then plain Subbed ("des"/"de/sub"/anything with "sub"),
        # then English ("en"), then German. Falls through to the
        # page's default-language preference, then to "de" last.
        language = None
        if url_wants_english_sub:
            for opt in available_languages:
                lo = opt.lower()
                if lo == "jps" or lo == "ens" or lo == "en/sub":
                    language = opt
                    break
        elif url_wants_subbed:
            for opt in available_languages:
                lo = opt.lower()
                if lo == "des" or lo == "de/sub" or "sub" in lo:
                    language = opt
                    break
        elif url_wants_english:
            for opt in available_languages:
                lo = opt.lower()
                if lo == "en" or "engl" in lo:
                    language = opt
                    break
        elif url_wants_german:
            for opt in available_languages:
                if opt.lower() == "de":
                    language = opt
                    break
        if not language:
            default = _bsto_default_language(base_soup)
            if default and default in available_languages:
                language = default
            elif available_languages:
                language = available_languages[0]
            else:
                language = "de"

        seasons: list[dict] = []
        for num in season_numbers:
            season_url = f"{series_root}/{num}/{language}"
            try:
                r = client.get(season_url)
                r.raise_for_status()
            except httpx.HTTPError:
                continue
            soup = BeautifulSoup(r.text, "html.parser")
            episodes = _bsto_episodes(soup, series_root, num, language)

            # Empty result means either bs.to fell back (so the page
            # had episodes but for a different language) or the
            # season genuinely has nothing in the requested language.
            # Either way, walk the other available languages until
            # one returns the season's episode list — those titles
            # + numbers populate the picker (stamped unavailable,
            # since the user can't actually play them in the
            # requested language). Keeps the show's full season /
            # episode structure visible in the dropdown regardless
            # of which language the user picked.
            #
            # Iterating ALL other languages (rather than just the
            # show's anchor / default) is necessary because the
            # requested language can BE the anchor — e.g., for a
            # Subbed+English-Sub-only show, the default-language
            # preference resolves to "des", so a request for /des
            # missing a season would never get a fallback if we
            # only tried the anchor.
            if not episodes:
                for fallback_lang in available_languages:
                    if fallback_lang == language:
                        continue
                    fallback_url = f"{series_root}/{num}/{fallback_lang}"
                    try:
                        fr = client.get(fallback_url)
                        fr.raise_for_status()
                    except httpx.HTTPError:
                        continue
                    fsoup = BeautifulSoup(fr.text, "html.parser")
                    fallback_episodes = _bsto_episodes(
                        fsoup, series_root, num, fallback_lang)
                    if fallback_episodes:
                        for ep in fallback_episodes:
                            ep["available"] = False
                            ep["language"] = language
                        episodes = fallback_episodes
                        break
            if episodes:
                seasons.append({"number": num, "episodes": episodes})

        return {
            "title": title,
            "thumbnailUrl": thumb,
            "seasons": seasons,
            # The actual language we ended up loading (URL hint, page
            # default, or "de" fallback). C# uses this to label the
            # language dropdown — needed for shows whose URL has no
            # language suffix but whose only available variant is
            # "de/sub" (the default-language picker promotes it when
            # "de" isn't offered).
            "language": language,
            # Raw <select.series-language> options from bs.to. The
            # C# layer mirrors these directly into the language
            # dropdown and applies its own persistent phantom-flag
            # filter for variants the user has previously discovered
            # to be empty.
            "availableLanguages": available_languages,
        }

    except httpx.HTTPError as e:
        raise HTTPException(status_code=502, detail=f"bs.to fetch failed: {e}")


def _bsto_title(soup: BeautifulSoup) -> str | None:
    h2 = soup.select_one("section.serie h2")
    if h2 and h2.text:
        # The h2 sometimes contains an inner <small> with the season
        # subtitle; strip it.
        for small in h2.find_all("small"):
            small.decompose()
        text = h2.get_text(strip=True)
        if text:
            return text
    og = soup.find("meta", property="og:title")
    if og and og.get("content"):
        return og["content"].strip()
    return None


def _bsto_thumb(soup: BeautifulSoup, page_url: str) -> str | None:
    img = soup.select_one("section.serie #sp_right img")
    if img and img.get("src"):
        return _absolutize(img["src"], page_url)
    og = soup.find("meta", property="og:image")
    if og and og.get("content"):
        return _absolutize(og["content"], page_url)
    return None


def _bsto_season_numbers(soup: BeautifulSoup) -> list[int]:
    """Reads the season-selector list. Each <li> carries class s<N>."""
    nums: set[int] = set()
    for li in soup.select("#seasons ul li"):
        for cls in li.get("class", []):
            m = re.match(r"s(\d+)$", cls)
            if m:
                try:
                    nums.add(int(m.group(1)))
                except ValueError:
                    pass
                break
    if not nums:
        # Fallback: scan season-tab anchors.
        for a in soup.select("#seasons ul li a[href]"):
            m = re.search(r"/serie/[^/]+/(\d+)(?:/|$)", a["href"])
            if m:
                try:
                    nums.add(int(m.group(1)))
                except ValueError:
                    pass
    return sorted(nums)


def _bsto_default_language(soup: BeautifulSoup) -> str | None:
    """Picks a language for fetching season pages.

    Prefers German ("de"), falls back to other dubbed variants, then
    Subbed forms, then whatever the page's series-language <select>
    offers first.
    """
    options = _bsto_available_languages(soup)
    if not options:
        return None
    for pref in ("de", "en", "de/sub", "des", "en/sub", "ens", "jps"):
        if pref in options:
            return pref
    return options[0]


def _bsto_available_languages(soup: BeautifulSoup) -> list[str]:
    """All language codes offered by the series-language <select>.

    Empty list when the page didn't render a language picker (e.g. a
    show with only one language available may omit the <select>) — the
    caller treats that as "no hint" and falls back to the URL or to
    "de" as a last resort.
    """
    options = [opt.get("value", "").strip()
               for opt in soup.select("select.series-language option")]
    return [o for o in options if o]


def _bsto_lang_class(code: str | None) -> str | None:
    """Coarse language category for a bs.to language token: 'ensub',
    'sub', 'en', 'de', or None for unknown. bs.to is inconsistent
    about URL form vs <select> value (e.g., 'de/sub' as a <select>
    value but '/des' in episode URLs); classifying both into a
    category lets equivalent forms compare equal.
    """
    if not code:
        return None
    lo = code.lower()
    if lo in ("jps", "ens", "en/sub"):
        return "ensub"
    if lo == "des" or "sub" in lo:
        return "sub"
    if lo == "en" or lo.startswith("en"):
        return "en"
    if lo == "de" or lo.startswith("de"):
        return "de"
    return None


def _bsto_href_lang_class(href: str) -> str | None:
    """Classifies a bs.to episode URL by its trailing language token.
    Order matters — longer suffixes ("/de/sub", "/en/sub", "/jps",
    "/ens", "/des") must be tested before shorter ones ("/de", "/en")
    so a Subbed URL doesn't classify as plain German.
    """
    if not href:
        return None
    h = href.rstrip("/").lower()
    if h.endswith("/jps") or h.endswith("/ens") or h.endswith("/en/sub"):
        return "ensub"
    if h.endswith("/des") or h.endswith("/de/sub"):
        return "sub"
    if h.endswith("/en"):
        return "en"
    if h.endswith("/de"):
        return "de"
    return None


def _bsto_episodes(
    soup: BeautifulSoup, series_root: str, season_num: int, language: str
) -> list[dict]:
    """Extracts episode rows for one season's HTML page.

    Each row's first cell holds an <a> whose text is the episode number;
    the second cell holds <a><strong>Episode title</strong></a>; the
    third cell holds the per-row hoster icons (`<i class="hoster ...">`).
    Episodes with no hosters in this language are rendered with
    `<tr class="disabled">` and an empty third cell — these are
    surfaced via `available=False` so the C# picker can grey them
    out (per-language: an episode unavailable in /de can still be
    available in /des or /jps).

    Fallback detection: bs.to silently serves a default-language page
    when the requested season+language combo has NO content at all
    (not just disabled, but missing entirely — e.g., requesting /jps
    season 22 for a show whose /jps content only covers season 1).
    The fallback page's episode hrefs use a different language's
    suffix than what was requested; compare the first valid row's
    href language category to the requested one and bail out with
    an empty list if they don't match — the caller then skips this
    season+language combo, so the C# picker never sees ghost
    "available" episodes that are really fallback content.
    """
    expected_class = _bsto_lang_class(language)
    rows = soup.select("table.episodes tr")
    for tr in rows:
        cells = tr.find_all("td")
        if len(cells) < 2:
            continue
        num_link = cells[0].find("a", href=True)
        if not num_link:
            continue
        href = num_link.get("href") or ""
        href_class = _bsto_href_lang_class(href)
        if expected_class and href_class and href_class != expected_class:
            # bs.to fell back to a different language — discard the
            # entire response so /series doesn't include this season.
            return []
        # Only need to inspect the first row that classifies; bs.to
        # serves consistent hrefs for the whole season.
        if href_class is not None:
            break

    episodes: list[dict] = []
    for tr in rows:
        cells = tr.find_all("td")
        if len(cells) < 2:
            continue
        num_link = cells[0].find("a", href=True)
        if not num_link:
            continue
        try:
            ep_num = int(num_link.get_text(strip=True))
        except ValueError:
            continue
        title_strong = cells[1].select_one("a strong")
        title = title_strong.get_text(strip=True) if title_strong else (
            cells[1].get_text(strip=True))
        href = num_link["href"]
        ep_url = _absolutize(href, series_root)

        # Availability — either signal alone is enough, but checking
        # both is robust against bs.to template variations:
        #   1. <tr class="disabled"> on the row.
        #   2. The third cell has no `<i class="hoster">` icons.
        tr_classes = tr.get("class") or []
        has_disabled_class = "disabled" in tr_classes
        hoster_cell = cells[2] if len(cells) > 2 else None
        hoster_count = (
            len(hoster_cell.select("i.hoster"))
            if hoster_cell is not None else 0
        )
        available = not has_disabled_class and hoster_count > 0

        episodes.append({
            "number": ep_num,
            "title": title,
            "language": language,
            "url": ep_url,
            "available": available,
        })

    # Some templates list every episode twice (one per hoster). Dedup on
    # episode number, keep first.
    seen: set[int] = set()
    deduped: list[dict] = []
    for ep in episodes:
        if ep["number"] in seen:
            continue
        seen.add(ep["number"])
        deduped.append(ep)
    deduped.sort(key=lambda e: e["number"])
    return deduped


def _absolutize(href: str, base: str) -> str:
    if href.startswith("http://") or href.startswith("https://"):
        return href
    if href.startswith("//"):
        return "https:" + href
    if href.startswith("/"):
        m = re.match(r"^(https?://[^/]+)", base)
        return (m.group(1) if m else "") + href
    # relative path — join under the bs.to root
    return "https://bs.to/" + href.lstrip("/")


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
