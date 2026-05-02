"""
yt-dlp extractor for Vidmoly and its mirror domains.

Vidmoly's embed page (`/embed-<id>.html`) is straightforward: a
JWPlayer setup with a `sources` array whose first entry's `file`
points at an m3u8 master playlist. No obfuscation in the standard
layout, so extraction reduces to a single regex against the embed
HTML.

Some mirrors instead serve a P.A.C.K.E.R. obfuscated block on the
embed page; the extractor falls back to unpacking that when the
direct regex doesn't match.

The bare `/<id>` route returns a thin landing page that <iframe>'s
the embed; we follow that iframe (or rewrite to the embed URL
ourselves) before pulling the m3u8.
"""

import re
from urllib.parse import urlparse

from yt_dlp.extractor.common import InfoExtractor
from yt_dlp.utils import ExtractorError


_BASE_CHARS = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ"


def _encode(i: int, base: int) -> str:
    if i < base:
        return _BASE_CHARS[i]
    return _encode(i // base, base) + _BASE_CHARS[i % base]


def _unpack_packer(packed: str) -> str | None:
    """Decode Dean Edwards P.A.C.K.E.R. obfuscated JS — same algo
    used by mixdrop.py / filemoon.py. Returns the unpacked source or
    None when the input doesn't match."""
    m = re.search(
        r"}\('(?P<p>.+?)',(?P<a>\d+),(?P<c>\d+),'(?P<k>.+?)'\.split\('\|'\)",
        packed, re.DOTALL,
    )
    if not m:
        return None
    p = m.group("p")
    a = int(m.group("a"))
    c = int(m.group("c"))
    k = m.group("k").split("|")
    p = p.replace("\\\\", "\\").replace("\\'", "'").replace('\\"', '"')
    for i in range(c - 1, -1, -1):
        if i < len(k) and k[i]:
            token = _encode(i, a)
            replacement = k[i]
            p = re.sub(r"\b" + re.escape(token) + r"\b",
                       lambda _m, s=replacement: s, p)
    return p


_BROWSER_UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 (KHTML, like Gecko) "
    "Chrome/131.0.0.0 Safari/537.36"
)


def _find_m3u8(text: str) -> str | None:
    """Tries several JWPlayer-shaped patterns to locate an m3u8 URL.
    Covers the variations observed across Vidmoly / Filemoon mirrors:
      - file:"..."  / "file":"..."  / src:"..."
      - single quotes
      - any double-quoted m3u8 URL as a last resort
    Returns the first match, or None.
    """
    if not text:
        return None
    patterns = (
        # JWPlayer sources array, file/src/src1 keys, single or double quotes
        r'sources\s*:\s*\[\s*\{\s*(?:"file"|file|"src"|src)\s*:\s*["\'](https?[^"\']+\.m3u8[^"\']*)["\']',
        # Bare file/src declaration (covers JWPlayer setup outside an array
        # plus some HLS-only embed templates)
        r'(?:"file"|\bfile|"src"|\bsrc)\s*:\s*["\'](https?[^"\']+\.m3u8[^"\']*)["\']',
        # data-* attributes some mirrors use
        r'data-(?:file|src|hls)\s*=\s*["\'](https?[^"\']+\.m3u8[^"\']*)["\']',
        # Any double-quoted m3u8 URL (last-resort fallback — picks the
        # first .m3u8 anywhere in the text)
        r'["\'](https?://[^"\']+\.m3u8[^"\']*)["\']',
    )
    for pat in patterns:
        m = re.search(pat, text)
        if m:
            return m.group(1)
    return None


class VidmolyIE(InfoExtractor):
    IE_NAME = "vidmoly"

    # Vidmoly URL forms observed in bs.to hoster lists:
    #   /embed-<id>.html — direct JWPlayer (the page we want)
    #   /w/<id>          — Nuxt SPA wrapper that <iframe>s the embed
    #   /v/<id>          — alt watch route, also iframes the embed
    #   /<id>            — bare ID, redirects/iframes to embed
    # Mirror TLDs are many (.to .me .net .biz .cc .sx + more); the
    # iframe inside a /w/ page often lands on a DIFFERENT TLD than
    # the request (e.g. /w/ on .me iframes /embed- on .biz).
    _VALID_URL = (
        r"https?://(?:www\.)?"
        r"(?:vidmoly\.(?:to|me|net|com|info|club|biz|cc|sx|tv|fun|cloud)"
        r"|vmoly\.[a-z]+)"
        r"/(?:embed-|w/|v/)?(?P<id>[a-z0-9]+)(?:\.html)?"
    )

    _TESTS = [{
        "url": "https://vidmoly.to/abc123def",
        "only_matching": True,
    }]

    def _real_extract(self, url):
        video_id = self._match_id(url)
        host = urlparse(url).netloc

        headers = {
            "User-Agent": _BROWSER_UA,
            "Referer": f"https://{host}/",
        }
        webpage = self._download_webpage(url, video_id, headers=headers)

        # "Video not found" — surface a clean ExtractorError so
        # aggregators can fall through to the next mirror.
        if re.search(r"(?i)<title>\s*(?:video not found|file removed|deleted|404)", webpage):
            raise ExtractorError("Vidmoly: video was removed", expected=True)

        # First pass: maybe the input URL was already the /embed-
        # page with the JWPlayer setup directly visible.
        m3u8_url = _find_m3u8(webpage)

        # Second pass: /w/ and /v/ wrapper pages (and sometimes the
        # bare /<id> route) are Nuxt SPAs that link to the actual
        # embed page. The embed's host can be a DIFFERENT TLD than
        # the request (e.g. /w/ on .me points at /embed- on .biz),
        # so we can NOT synthesize the URL from the request host —
        # we have to read it out of the SSR payload.
        if not m3u8_url:
            iframe_url = None
            # Preferred: explicit <iframe src> (older mirrors / direct embeds).
            iframe_match = re.search(
                r'<iframe[^>]+src=["\'](https?://[^"\']*(?:vidmoly|vmoly)[^"\']*?/embed-[^"\']+)["\']',
                webpage, re.IGNORECASE,
            )
            if iframe_match:
                iframe_url = iframe_match.group(1)
            # Newer Nuxt /v/ pages don't render an iframe in SSR — the
            # embed URL is serialized inside __NUXT_DATA__ as
            # "https://<mirror>/embed-<id>.html". Match any such URL
            # anywhere in the page; the path shape is specific enough
            # that nothing else looks like it.
            if not iframe_url:
                ssr_match = re.search(
                    r'https?://[^\s"\'<>\\]+/embed-[A-Za-z0-9]+(?:\.html)?',
                    webpage,
                )
                if ssr_match:
                    iframe_url = ssr_match.group(0)
            # Last-resort iframe rewrite: if we still don't have one
            # and the URL was clearly a wrapper form, synthesize the
            # embed URL from the same host. Only useful when the
            # mirror serves the embed itself; cross-mirror /w/ pages
            # need the SSR match above.
            if not iframe_url and ("/w/" in url or "/v/" in url
                                   or url.rstrip("/").endswith(f"/{video_id}")):
                iframe_url = f"https://{host}/embed-{video_id}.html"
            if iframe_url:
                iframe_host = urlparse(iframe_url).netloc
                webpage = self._download_webpage(
                    iframe_url, video_id,
                    headers={"User-Agent": _BROWSER_UA, "Referer": f"https://{host}/"},
                )
                m3u8_url = _find_m3u8(webpage)
                # Segments need the iframe's origin as Referer to play.
                host = iframe_host
                headers = {
                    "User-Agent": _BROWSER_UA,
                    "Referer": f"https://{iframe_host}/",
                }

        # Third pass: P.A.C.K.E.R. obfuscation seen on some mirrors.
        if not m3u8_url:
            packed_match = re.search(
                r"(eval\(function\(p,a,c,k,e,d?\)[^\n]*?}\)\))",
                webpage, re.DOTALL,
            )
            if packed_match:
                unpacked = _unpack_packer(packed_match.group(1))
                if unpacked:
                    m3u8_url = _find_m3u8(unpacked)

        if not m3u8_url:
            raise ExtractorError(
                "Vidmoly: m3u8 URL not found in page (layout changed?)",
                expected=True,
            )

        title = (
            self._og_search_title(webpage, default=None)
            or self._html_extract_title(webpage, default=video_id)
            or video_id
        )
        if title:
            title = re.sub(r"\s*[-|]\s*Vidmoly.*$", "", title, flags=re.I).strip()

        formats = self._extract_m3u8_formats(
            m3u8_url, video_id, "mp4",
            entry_protocol="m3u8_native", m3u8_id="hls",
            headers=headers,
        )

        return {
            "id": video_id,
            "title": title or video_id,
            "formats": formats,
            "http_headers": headers,
        }
