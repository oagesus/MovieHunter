r"""
Extractor for a DLE-based German streaming catalog.

Finds the main embed iframe (typically to meinecloud.click) and delegates
to whichever extractor matches that URL.

The `_VALID_URL` below must match your real domain — replace testurl\.com
with your real domain (keep the backslash before the dot).
"""

import re

from yt_dlp.extractor.common import InfoExtractor


class TestUrlIE(InfoExtractor):
    IE_NAME = "hdfilme"

    _VALID_URL = (
        r"https?://(?:www\.)?hdfilme\.win/"
        r"filme1/(?P<id>\d+)-(?P<slug>[\w-]+)-stream\.html"
    )

    _TESTS = [{
        "url": "https://hdfilme.win/filme1/4758-harry-potter-und-die-kammer-des-schreckens-stream.html",
        "only_matching": True,
    }]

    def _real_extract(self, url):
        video_id = self._match_id(url)
        webpage = self._download_webpage(url, video_id)

        # The main player iframe lives inside a div with class "guardahd-player".
        # Match it rather than any random iframe (like the YouTube trailer).
        iframe_url = self._search_regex(
            r'class="[^"]*guardahd-player[^"]*"[^>]*>\s*<iframe[^>]+src="(?P<u>https?://[^"]+)"',
            webpage, "embed URL", group="u",
        )

        title = (
            self._og_search_title(webpage, default=None)
            or self._html_search_regex(
                r'<h1[^>]*>([^<]+)</h1>', webpage, "title", default=None)
            or video_id
        )

        return {
            "_type": "url_transparent",
            "url": iframe_url,
            "id": video_id,
            "title": title.strip() if title else video_id,
        }
