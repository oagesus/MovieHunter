"""
yt-dlp extractor for Filemoon and its mirror domains.

yt-dlp doesn't ship a built-in Filemoon extractor (the upstream
youtube-dl XFileShare extractor stopped covering Filemoon's modern
layout in 2024). Filemoon mirrors rotate through many TLDs but all
share the same page structure:

  1. Embed page at /e/<id> (or /embed-<id>.html) returns HTML whose
     <body> contains a Dean Edwards P.A.C.K.E.R. obfuscated JS block
     (`eval(function(p,a,c,k,e,d){...}('packed',a,c,'k1|k2|...|kn'.split('|'),0,{}))`).
  2. After P.A.C.K.E.R. unpacks, the unpacked source contains a
     JWPlayer setup with `sources:[{file:"<m3u8 URL>"}]` (sometimes
     `file:"<URL>"` without the array wrapper for legacy mirrors).
  3. The m3u8 master playlist + the page's Referer / User-Agent
     headers are what the downstream player needs.

Same unpack algorithm as `mixdrop.py` — Filemoon and MixDrop both
use vanilla P.A.C.K.E.R. so the unpacker is duplicated here rather
than refactored into a shared helper (keeping each extractor
self-contained matches the existing plugin layout).
"""

import re
from urllib.parse import urlparse

from yt_dlp.extractor.common import InfoExtractor
from yt_dlp.utils import ExtractorError


_BASE_CHARS = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ"


def _encode(i: int, base: int) -> str:
    """Encode integer `i` as a string in `base` using 0-9a-zA-Z."""
    if i < base:
        return _BASE_CHARS[i]
    return _encode(i // base, base) + _BASE_CHARS[i % base]


def _unpack_packer(packed: str) -> str | None:
    """Decode Dean Edwards P.A.C.K.E.R. obfuscated JS. Returns the
    unpacked source code, or None if the input doesn't match the
    P.A.C.K.E.R. signature."""
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
    Covers the variations observed across Filemoon mirrors:
      - file:"..."  / "file":"..."  / src:"..."
      - single quotes
      - any double-quoted m3u8 URL as a last resort
    Returns the first match, or None.
    """
    if not text:
        return None
    patterns = (
        r'sources\s*:\s*\[\s*\{\s*(?:"file"|file|"src"|src)\s*:\s*["\'](https?[^"\']+\.m3u8[^"\']*)["\']',
        r'(?:"file"|\bfile|"src"|\bsrc)\s*:\s*["\'](https?[^"\']+\.m3u8[^"\']*)["\']',
        r'data-(?:file|src|hls)\s*=\s*["\'](https?[^"\']+\.m3u8[^"\']*)["\']',
        r'["\'](https?://[^"\']+\.m3u8[^"\']*)["\']',
    )
    for pat in patterns:
        m = re.search(pat, text)
        if m:
            return m.group(1)
    return None


class FilemoonIE(InfoExtractor):
    IE_NAME = "filemoon"

    # Permissive URL match: filemoon.* plus the rotating mirror
    # domains we've observed in bs.to / hoster lists. Both /e/<id>
    # (modern) and /embed-<id>.html (legacy) routes are accepted.
    _VALID_URL = (
        r"https?://(?:www\.)?"
        r"(?:filemoon\.(?:sx|com|to|in|nl|art|ngo|wf|ki|sb|now|of|live|pw|red|cool|fun|me)"
        r"|filemoonen\.[a-z]+"
        r"|kerapoxy\.cc"
        r"|f1xc\.[a-z]+)"
        r"/(?:e/|d/|embed-)?(?P<id>[a-z0-9]+)"
    )

    _TESTS = [{
        "url": "https://filemoon.sx/e/abc123def",
        "only_matching": True,
    }]

    def _real_extract(self, url):
        video_id = self._match_id(url)
        host = urlparse(url).netloc

        webpage = self._download_webpage(
            url, video_id,
            headers={"User-Agent": _BROWSER_UA, "Referer": url},
        )

        # Some Filemoon pages (especially the bare /<id> route on
        # older mirrors) return a stub HTML that <iframe>'s the
        # actual embed. Follow the iframe to the real page.
        iframe_src = self._search_regex(
            r'<iframe[^>]+src=["\']([^"\']*(?:filemoon|/embed[-/])[^"\']*)["\']',
            webpage, "filemoon iframe", default=None,
        )
        if iframe_src:
            if iframe_src.startswith("//"):
                iframe_src = "https:" + iframe_src
            elif iframe_src.startswith("/"):
                iframe_src = f"https://{host}{iframe_src}"
            webpage = self._download_webpage(
                iframe_src, video_id,
                headers={"User-Agent": _BROWSER_UA, "Referer": url},
            )

        # "Video not found" / removed video shortcut.
        if re.search(r"(?i)<title>\s*(?:video not found|file removed|deleted)", webpage):
            raise ExtractorError("Filemoon: video was removed", expected=True)

        # Try the direct page first — some Filemoon embeds (especially
        # newer rollouts) put the JWPlayer setup inline without P.A.C.K.E.R.
        m3u8_url = _find_m3u8(webpage)

        # Fall back to unpacking P.A.C.K.E.R. — Filemoon's classic layout.
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
                "Filemoon: m3u8 URL not found (layout changed?)",
                expected=True,
            )

        title = (
            self._og_search_title(webpage, default=None)
            or self._html_extract_title(webpage, default=video_id)
            or video_id
        )

        # Strip the boilerplate "- Filemoon" suffix some mirrors
        # append to <title>.
        if title:
            title = re.sub(r"\s*[-|]\s*Filemoon.*$", "", title, flags=re.I).strip()

        # The CDN that serves the m3u8 segments expects the embed
        # page's host as Referer; without it the segment GETs 403.
        headers = {
            "User-Agent": _BROWSER_UA,
            "Referer": f"https://{host}/",
        }

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
