"""
Extractor for meinecloud.click — a server-list embed host used by DLE-based
German streaming sites. Parses the `data-link` server list, picks the most
reliably extractable host, and delegates.
"""

import re

from yt_dlp.extractor.common import InfoExtractor
from yt_dlp.utils import ExtractorError


# Preference order: extractors with the most stable / most often-updated logic
# in yt-dlp go first. Tweak as needed.
# Note: mixdrop.ag and its mirrors (m1xdrop.click, dr0pstream.com) are NOT
# supported by yt-dlp's current stable build, so they're deprioritized.
_HOST_PREFERENCE = [
    # MixDrop mirror that works from most EU IPs (extractor: mixdrop.py)
    "mixdrop",
    "m1xdrop",       # the current 302 target — same extractor matches it
    # DoodStream — works for ~50% of videos depending on CDN edge routing
    "doodstream",
    "dood.",
    # These are tried as last-resort; they often fail due to Cloudflare
    # blocks or missing extractors, but meinecloud.py falls through cleanly.
    "supervideo",
    "streamtape",
    "voe",
    "vidoza",
    "dr0pstream",    # Cloudflare-blocked from many networks
]


class MeineCloudIE(InfoExtractor):
    IE_NAME = "meinecloud"

    _VALID_URL = (
        r"https?://(?:www\.)?meinecloud\.click/"
        r"(?:movie|episode|series|watch)/(?P<id>tt?\d+|[\w-]+)"
    )

    _TESTS = [{
        "url": "https://meinecloud.click/movie/tt0295297",
        "only_matching": True,
    }]

    def _real_extract(self, url):
        video_id = self._match_id(url)
        webpage = self._download_webpage(
            url, video_id,
            headers={"Referer": "https://hdfilme.win/"},
        )

        # Collect all server links — they look like:
        #   <li class="" data-link="//mixdrop.ag/e/o70rjlvofe6nwj">
        servers = re.findall(
            r'<li[^>]+data-link="(?P<link>//[^"]+)"[^>]*>\s*(?P<name>[^<\s]+)',
            webpage,
        )

        if not servers:
            raise ExtractorError(
                "No server links found on meinecloud page. "
                "Either the page layout changed or the page is blocked.",
                expected=True,
            )

        # Normalize protocol-relative URLs
        servers = [
            {"name": name.strip().lower(),
             "url": ("https:" + link) if link.startswith("//") else link}
            for link, name in servers
        ]

        # Sort by our preference (lower index = higher priority). Unknown
        # hosts keep their original order at the end.
        def score(s):
            combined = (s["name"] + " " + s["url"]).lower()
            for i, key in enumerate(_HOST_PREFERENCE):
                if key in combined:
                    return i
            return len(_HOST_PREFERENCE)

        servers.sort(key=score)

        self.to_screen(
            f"[meinecloud] {len(servers)} servers: "
            + ", ".join(s["name"] for s in servers)
        )

        # Try each server in preference order. First one that yt-dlp can
        # actually extract wins. This handles the common case where one
        # host has removed the video but others still have it.
        last_error = None
        ydl = self._downloader
        for s in servers:
            try:
                self.to_screen(f"[meinecloud] trying {s['name']} → {s['url']}")
                info = ydl.extract_info(s["url"], download=False, ie_key=None)
                if info:
                    # Success — return the extracted info, adding a Referer
                    # in case the downstream extractor didn't.
                    info.setdefault("http_headers", {})
                    info["http_headers"].setdefault(
                        "Referer", "https://meinecloud.click/"
                    )
                    return info
            except ExtractorError as e:
                last_error = e
                self.to_screen(f"[meinecloud]   {s['name']} failed: {e}")
                continue

        raise ExtractorError(
            f"All {len(servers)} servers failed. Last error: {last_error}",
            expected=True,
        )
