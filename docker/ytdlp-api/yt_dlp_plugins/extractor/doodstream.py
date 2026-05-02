"""
Custom extractor for DoodStream and its mirrors.

DoodStream serves videos via a two-step exchange:
  1. Embed page contains `/pass_md5/<hash>/<id>` + `?token=...` references
  2. GET /pass_md5/... (with Referer) returns a URL prefix
  3. Append 10 random chars + ?token=<token>&expiry=<ms> to get the final URL

Covers the rotating list of DoodStream mirror domains.
"""

import random
import re
import string
import time
from urllib.parse import urlparse

from yt_dlp.extractor.common import InfoExtractor
from yt_dlp.utils import ExtractorError


class DoodStreamIE(InfoExtractor):
    IE_NAME = "doodstream"

    # DoodStream rotates through many mirror domains. The classic
    # `dood.*` family + observed-in-the-wild rebrand domains. New
    # mirrors keep appearing (recent additions: do7go, playmogo,
    # vidply, doply, vide0); add them here when you hit "Unsupported
    # URL" in docker logs for one.
    _VALID_URL = (
        r"https?://(?:www\.)?"
        r"(?:"
        r"(?:dood(?:stream)?|ds2play|d000?d|d0000d)"
        r"\.(?:to|so|cc|la|pm|yt|wf|ch|li|re|net|ws|club|watch|stream|com|sh|one|info)"
        r"|do7go\.com"
        r"|playmogo\.(?:com|net|to)"
        r"|vidply\.(?:com|net)"
        r"|doply\.(?:com|net)"
        r"|vide0\.(?:com|net)"
        r")"
        r"/[ed]/(?P<id>[a-z0-9]+)"
    )

    _TESTS = [{
        "url": "https://dood.to/e/vsjdj53azs6y",
        "only_matching": True,
    }]

    _UA = (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/131.0.0.0 Safari/537.36"
    )

    def _real_extract(self, url):
        video_id = self._match_id(url)
        host_prefix = "{0.scheme}://{0.netloc}".format(urlparse(url))

        # Ensure we're hitting the /e/ variant — /d/ is the download page,
        # /e/ is the embed player with the pass_md5 reference.
        if "/d/" in url:
            url = url.replace("/d/", "/e/", 1)

        webpage = self._download_webpage(
            url, video_id,
            headers={
                "User-Agent": self._UA,
                "Referer": host_prefix + "/",
            },
        )

        # DoodStream shows a 200 OK page titled "Video not found" when the
        # file has been removed. Detect this up-front so we can raise a
        # useful error (and meinecloud can fall through to the next server).
        if re.search(r"<title>\s*Video not found", webpage, re.I):
            raise ExtractorError("DoodStream: video was removed", expected=True)

        pass_md5_path = self._search_regex(
            r"(/pass_md5/[\w/-]+)", webpage, "pass_md5 path", default=None)
        token = self._search_regex(
            r"[?&]token=([a-zA-Z0-9_-]+)", webpage, "token", default=None)
        if not pass_md5_path or not token:
            raise ExtractorError(
                "DoodStream: pass_md5 / token not found in page (layout changed?)",
                expected=True,
            )

        title = (
            self._og_search_title(webpage, default=None)
            or self._html_extract_title(webpage, default=None)
            or video_id
        )
        # DoodStream appends " - DoodStream" to titles — strip if present.
        if title and " - DoodStream" in title:
            title = title.split(" - DoodStream")[0].strip()

        base_url = self._download_webpage(
            host_prefix + pass_md5_path, video_id,
            headers={
                "User-Agent": self._UA,
                "Referer": url,
                "X-Requested-With": "XMLHttpRequest",
            },
            note="Fetching stream base URL",
            errnote="Failed to get stream base URL",
        )

        if not base_url.startswith("http"):
            raise ExtractorError(
                "pass_md5 endpoint did not return a URL; host may have "
                "changed its extraction scheme.",
                expected=True,
            )

        rand_chars = "".join(
            random.choices(string.ascii_letters + string.digits, k=10))
        expiry = int(time.time() * 1000)
        stream_url = f"{base_url.strip()}{rand_chars}?token={token}&expiry={expiry}"

        headers = {
            "Referer": host_prefix + "/",
            "User-Agent": self._UA,
        }

        # CDN serves a valid video only when Referer is sent. Downstream
        # players (VLC) often can't be trusted to send Referer, so we
        # pre-resolve any redirects here with Referer, then hand the final
        # URL to the player. If the first request already returns 200,
        # the URL is unchanged.
        #
        # Also critical: DoodStream routes different videos to different CDN
        # edges, and some edges are unreachable from certain networks. Raise
        # ExtractorError on unreachable CDN so meinecloud.py can fall through
        # to the next server.
        import urllib.error
        import urllib.request
        try:
            req = urllib.request.Request(stream_url, headers=headers, method="HEAD")
            with urllib.request.urlopen(req, timeout=8) as resp:
                resolved = resp.geturl()
                if resolved and resolved != stream_url:
                    stream_url = resolved
                if resp.status >= 400:
                    raise ExtractorError(
                        f"DoodStream CDN returned HTTP {resp.status}",
                        expected=True,
                    )
        except urllib.error.HTTPError as e:
            raise ExtractorError(
                f"DoodStream CDN returned HTTP {e.code}", expected=True,
            )
        except (urllib.error.URLError, OSError, TimeoutError) as e:
            # Connection refused, DNS failure, timeout — all signal that this
            # CDN edge is unreachable from our network. Bail early so the
            # next server gets a chance.
            raise ExtractorError(
                f"DoodStream CDN unreachable ({type(e).__name__}: {e}). "
                "Try a different server.",
                expected=True,
            )

        return {
            "id": video_id,
            "title": title,
            # Provide a `formats` list so main.py's _pick_streams can read
            # http_headers from the chosen format.
            "formats": [{
                "url": stream_url,
                "ext": "mp4",
                "format_id": "http",
                "vcodec": "h264",
                "acodec": "aac",
                "http_headers": headers,
            }],
            "http_headers": headers,
        }
