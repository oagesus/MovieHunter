"""
Custom yt-dlp extractor for MixDrop and its mirrors.

MixDrop embeds a Dean Edwards "P.A.C.K.E.R." obfuscated JS block that,
once unpacked, assigns the direct video URL to `MDCore.wurl`. This
extractor unpacks the block and returns that URL.

Handles the rotating mirror list (mixdrop.ag, m1xdrop.click, dr0pstream,
plus many other TLD variants). dr0pstream.com is Cloudflare-blocked from
many IPs — if that happens we surface a clear error so meinecloud can
fall through.
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
    """Decode Dean Edwards P.A.C.K.E.R. obfuscated JS.

    Input: the raw string containing an `eval(function(p,a,c,k,e,d)...)`
    invocation. Returns the unpacked code, or None if the format doesn't
    match.
    """
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

    # Unescape common escapes in the packed string
    p = p.replace("\\\\", "\\").replace("\\'", "'").replace('\\"', '"')

    for i in range(c - 1, -1, -1):
        if i < len(k) and k[i]:
            token = _encode(i, a)
            replacement = k[i]
            p = re.sub(r"\b" + re.escape(token) + r"\b",
                       lambda _m, s=replacement: s, p)

    return p


class MixDropIE(InfoExtractor):
    IE_NAME = "mixdrop"

    _VALID_URL = (
        r"https?://(?:www\.)?"
        r"(?:mixdrop\.(?:ag|club|co|bz|to|ch|gl|sx|pw|ir|media|vc|fi|si|nu|show|net)"
        r"|m1xdrop\.(?:click|com|to|sx|cc|pw|tv|ps|bz|net)"
        r"|dr0pstream\.(?:com|net|to|cc)"
        r"|mxcontent\.net)"
        r"/[ef]/(?P<id>[a-z0-9]+)"
    )

    _TESTS = [{
        "url": "https://mixdrop.ag/e/o70rjlvofe6nwj",
        "only_matching": True,
    }]

    _UA = (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/131.0.0.0 Safari/537.36"
    )

    def _real_extract(self, url):
        video_id = self._match_id(url)
        host = urlparse(url).netloc

        try:
            webpage = self._download_webpage(
                url, video_id,
                headers={
                    "User-Agent": self._UA,
                    "Referer": "https://meinecloud.click/",
                },
            )
        except ExtractorError as e:
            # dr0pstream/m1xdrop mirrors are frequently Cloudflare-blocked
            msg = str(e).lower()
            if "403" in msg or "forbidden" in msg or "cloudflare" in msg:
                raise ExtractorError(
                    f"MixDrop mirror {host} is Cloudflare-blocked from this network",
                    expected=True,
                )
            raise

        # Detect "Video not found" / removed videos
        if re.search(r"(?i)<title>\s*(?:video not found|removed|deleted)", webpage):
            raise ExtractorError(
                "MixDrop: video was removed", expected=True,
            )

        packed_match = re.search(
            r"(eval\(function\(p,a,c,k,e,d?\)[^\n]*?}\)\))",
            webpage, re.DOTALL,
        )
        if not packed_match:
            raise ExtractorError(
                "MixDrop: packed JS block not found (layout changed?)",
                expected=True,
            )

        unpacked = _unpack_packer(packed_match.group(1))
        if not unpacked:
            raise ExtractorError(
                "MixDrop: failed to unpack JS", expected=True,
            )

        wurl_match = re.search(r'MDCore\.wurl\s*=\s*"([^"]+)"', unpacked)
        if not wurl_match:
            raise ExtractorError(
                "MixDrop: MDCore.wurl not found after unpacking",
                expected=True,
            )

        stream_url = wurl_match.group(1)
        if stream_url.startswith("//"):
            stream_url = "https:" + stream_url

        # Title — strip "- MixDrop" suffix if present
        title = (
            self._og_search_title(webpage, default=None)
            or self._html_extract_title(webpage, default=None)
            or video_id
        )
        if title:
            title = re.sub(r"\s*[-|]\s*MixDrop.*$", "", title, flags=re.I).strip()

        headers = {
            "Referer": f"https://{host}/",
            "User-Agent": self._UA,
        }

        # Pre-flight check: MixDrop's signed URL often has a session-bound
        # signature that rejects requests from outside the browser context.
        # If the CDN returns 4xx on HEAD, raise ExtractorError so meinecloud
        # can fall through to the next server (DoodStream, typically).
        import urllib.error
        import urllib.request
        try:
            req = urllib.request.Request(stream_url, headers=headers, method="HEAD")
            with urllib.request.urlopen(req, timeout=6) as resp:
                if resp.status >= 400:
                    raise ExtractorError(
                        f"MixDrop CDN returned HTTP {resp.status} (likely "
                        "session-bound signature; trying next server)",
                        expected=True,
                    )
        except urllib.error.HTTPError as e:
            if e.code == 403:
                raise ExtractorError(
                    "MixDrop CDN returned HTTP 403 (session-bound signature; "
                    "trying next server)",
                    expected=True,
                )
            # Other HTTP errors are also probably unrecoverable
            raise ExtractorError(
                f"MixDrop CDN returned HTTP {e.code}", expected=True,
            )
        except (urllib.error.URLError, OSError, TimeoutError) as e:
            raise ExtractorError(
                f"MixDrop CDN unreachable: {type(e).__name__}",
                expected=True,
            )

        return {
            "id": video_id,
            "title": title,
            "formats": [{
                "url": stream_url,
                "ext": "mp4",
                "format_id": "http",
                "http_headers": headers,
            }],
            "http_headers": headers,
        }
