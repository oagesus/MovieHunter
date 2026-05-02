FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PIP_NO_CACHE_DIR=1 \
    PYTHONPATH=/app

# yt-dlp + the FastAPI host that wraps it. bgutil-ytdlp-pot-provider
# is a sidecar plugin used by the YouTube extractor.
RUN pip install "yt-dlp>=2026.3.1" "fastapi>=0.115" "uvicorn[standard]>=0.32" "bgutil-ytdlp-pot-provider"

# bs.to scraping deps — used by /series (episode listing) and
# /bsto/search (filter the cached series directory). Captcha-solving
# itself happens in the C# app's embedded WebView (see
# MainWindow.BstoCaptcha.cs); the Python service only handles the
# captcha-free metadata calls.
RUN pip install "httpx>=0.27" "beautifulsoup4>=4.12"

WORKDIR /app
COPY main.py .
# Custom yt-dlp extractors. yt-dlp auto-discovers any module under a
# `yt_dlp_plugins` namespace package on sys.path.
COPY yt_dlp_plugins ./yt_dlp_plugins

EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
  CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8080/healthz', timeout=3).status==200 else 1)"

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8080"]
