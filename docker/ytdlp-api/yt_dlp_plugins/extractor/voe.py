"""
yt-dlp extractor for voe.sx and its mirror domains.

yt-dlp dropped its built-in Voe extractor a while back — Voe rotates
obfuscation patterns and mirror domains aggressively, and the upstream
maintainers couldn't keep pace. This is a port of the algorithm
maintained by pelim/yt-dlp-plugin-voe and MPZ-00/voe-dl (last active
2025-09), which solves Voe's current "Method 8" obfuscated-JSON
payload.

Pipeline:
  1. Fetch the page with browser-like headers (Voe 403s on default UA).
  2. If the page contains `window.location.href = "<mirror>"`, follow
     that — Voe often redirects /<id> to /e/<id> on a mirror domain.
  3. Locate the obfuscated payload at
        <script type="application/json">["<obf>"]</script>
  4. Decode through 6 transforms: ROT13 → strip noise markers → base64
     → shift each char code-point −3 → reverse → base64 → JSON.parse.
  5. The decoded JSON has a `source` key with the master HLS URL.

Returns m3u8 + the headers downstream (yt-dlp's HLS player + our VLC
host) need to fetch the segments.
"""

import base64
import codecs
import json
import re

from yt_dlp.extractor.common import InfoExtractor
from yt_dlp.utils import ExtractorError


_BROWSER_UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 (KHTML, like Gecko) "
    "Chrome/131.0.0.0 Safari/537.36"
)

_NOISE_MARKERS = ("@$", "^^", "~@", "%?", "*~", "!!", "#&")


class VoeIE(InfoExtractor):
    IE_NAME = "voe"

    # Permissive URL match: voe.sx itself plus the rotating mirror
    # domains. bs.to most often delivers voe.sx, but the redirect lands
    # on whichever mirror is currently active. We also accept any
    # /<hash>(/e/<hash>)? path on a domain that contains 'voe'.
    _VALID_URL = (
        r"https?://"
        r"(?:[\w-]+\.)?(?:voe[\w-]*\.(?:sx|com|net|org|click|red|live|cloud|sh|fun))"
        r"(?:/(?:e/)?)"
        r"(?P<id>[\w-]+)"
    )

    _TESTS = [{
        "url": "https://voe.sx/povhcc8qt2a1",
        "only_matching": True,
    }]

    def _real_extract(self, url):
        video_id = self._match_id(url)
        headers = {"User-Agent": _BROWSER_UA, "Referer": "https://voe.sx/"}

        # Step 1: fetch the (initial) page.
        webpage = self._download_webpage(url, video_id, headers=headers)

        # Step 2: follow the JS-level redirect if present. Voe's bare
        # /<id> route returns a tiny stub that does
        # `window.location.href = "https://<mirror>.voe.sx/e/<id>"` —
        # the deobfuscable payload only exists on the final page.
        redirect = self._search_regex(
            r'window\.location\.href\s*=\s*["\']([^"\']+)["\']',
            webpage, "voe js redirect", default=None,
        )
        if redirect:
            self.write_debug(f"voe: following JS redirect to {redirect}")
            webpage = self._download_webpage(
                redirect, video_id,
                headers={"User-Agent": _BROWSER_UA, "Referer": url},
            )

        # Steps 3-5: try the modern obfuscated-JSON pipeline.
        m3u8_url = _extract_voe_obfuscated(webpage)

        # Fallbacks for older / alternative page formats.
        if not m3u8_url:
            m3u8_url = self._search_regex(
                r'"hls"\s*:\s*"([^"]+\.m3u8[^"]*)"',
                webpage, "voe hls direct", default=None,
            )
        if not m3u8_url:
            m3u8_url = self._search_regex(
                r'sources\s*=\s*\[\s*\{\s*file\s*:\s*"([^"]+\.m3u8[^"]*)"',
                webpage, "voe sources fallback", default=None,
            )

        if not m3u8_url:
            raise ExtractorError(
                "voe: no m3u8 URL found in page. The obfuscation pattern "
                "may have changed again — check pelim/yt-dlp-plugin-voe "
                "for an updated deobfuscation routine.",
                expected=True,
            )

        title = (
            self._og_search_title(webpage, default=None)
            or self._html_extract_title(webpage, default=video_id)
            or video_id
        )

        formats = self._extract_m3u8_formats(
            m3u8_url, video_id, "mp4",
            entry_protocol="m3u8_native", m3u8_id="hls",
            headers=headers,
        )

        return {
            "id": video_id,
            "title": title.strip() if title else video_id,
            "formats": formats,
            "http_headers": headers,
        }


def _extract_voe_obfuscated(html: str) -> str | None:
    """The 6-step deobfuscation pipeline. Returns the m3u8 URL or None
    when the page didn't contain a recognised payload (caller falls
    back to simpler regexes)."""

    # Find the obfuscated payload — wrapped in <script type="application/json">
    # as a single-element JSON array of one string.
    m = re.search(
        r'<script[^>]+type=["\']application/json["\'][^>]*>\s*(\[[^<]+\])\s*</script>',
        html, re.DOTALL,
    )
    if not m:
        return None
    try:
        arr = json.loads(m.group(1))
    except ValueError:
        return None
    if not isinstance(arr, list) or not arr or not isinstance(arr[0], str):
        return None
    s = arr[0]

    # 1. ROT13 (letters only — codecs.encode handles it correctly).
    s = codecs.encode(s, "rot_13")

    # 2. Strip the obfuscator's noise markers.
    for pat in _NOISE_MARKERS:
        s = s.replace(pat, "")

    # 3. Base64 decode (tolerant of missing padding).
    s = _b64decode_padded(s)
    if s is None:
        return None

    # 4. Shift each char code-point by −3.
    try:
        s = "".join(chr(ord(c) - 3) for c in s)
    except ValueError:
        return None

    # 5. Reverse, then base64-decode again.
    s = _b64decode_padded(s[::-1])
    if s is None:
        return None

    # 6. Parse JSON, return the master HLS URL.
    try:
        data = json.loads(s)
    except ValueError:
        return None

    for key in ("source", "hls", "file", "direct_access_url"):
        v = data.get(key) if isinstance(data, dict) else None
        if isinstance(v, str) and ("http" in v):
            return v
    return None


def _b64decode_padded(t: str) -> str | None:
    """Base64 decode tolerant of missing padding. Returns None on
    decode failure rather than raising."""
    if not t:
        return None
    t = t + "=" * (-len(t) % 4)
    try:
        return base64.b64decode(t).decode("utf-8", errors="replace")
    except Exception:
        return None
