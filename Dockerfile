FROM python:3.12-slim

# Runtime JS pour les défis JavaScript de YouTube (yt-dlp-ejs, deno >= 2.3)
COPY --from=denoland/deno:bin-2.9.4 /deno /usr/local/bin/deno

ENV PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /srv/app

COPY requirements.txt .
# yt-dlp est non fige pour suivre les changements de YouTube, mais Docker
# reutilise cette couche tant que requirements.txt ne bouge pas : l'image
# du 17/08/2026 embarquait encore un yt-dlp du 04/07, d'ou un « HTTP Error
# 403 » sur tous les telechargements. YTDLP_REFRESH change a chaque
# deploiement et force la reinstallation.
ARG YTDLP_REFRESH=0
RUN echo "refresh ${YTDLP_REFRESH}" \
    && pip install --no-cache-dir -r requirements.txt

COPY app/ ./app/

RUN useradd --create-home --uid 1000 yayo \
    && mkdir -p /data \
    && chown -R yayo:yayo /data

USER yayo

EXPOSE 8000

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/api/health', timeout=4)"

CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
