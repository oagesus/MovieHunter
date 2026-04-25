FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PIP_NO_CACHE_DIR=1 \
    PYTHONPATH=/app

RUN pip install 'yt-dlp>=2026.3.1' 'fastapi>=0.115' 'uvicorn[standard]>=0.32' \
    'bgutil-ytdlp-pot-provider'

WORKDIR /app
COPY main.py .
# Custom extractors. yt-dlp auto-discovers any module under a
# `yt_dlp_plugins` namespace package on sys.path.
COPY yt_dlp_plugins ./yt_dlp_plugins

EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
  CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8080/healthz', timeout=3).status==200 else 1)"

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8080"]
