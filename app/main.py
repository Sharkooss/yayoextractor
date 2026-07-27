"""Yayo Extractor — YouTube vers MP3/MP4, pensé pour Yayo."""

import json
import logging
import os
import re
import shutil
import threading
import time
import urllib.error
import urllib.request
import uuid
from contextlib import asynccontextmanager
from pathlib import Path

import yt_dlp
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import FileResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("yayoextractor")

DATA_DIR = Path(os.environ.get("DATA_DIR", "/data"))
JOBS_DIR = DATA_DIR / "jobs"
# Optionnel : un cookies.txt exporté du navigateur, monté dans /data,
# si YouTube se met à bloquer l'IP du serveur.
COOKIES_FILE = DATA_DIR / "cookies.txt"
POT_PROVIDER_URL = os.environ.get("POT_PROVIDER_URL", "").strip()
# Proxy sortant pour yt-dlp (ex. WARP) : YouTube bloque les IP de datacenter.
YTDLP_PROXY = os.environ.get("YTDLP_PROXY", "").strip()
MAX_DURATION_S = int(os.environ.get("MAX_DURATION_MINUTES", "180")) * 60
JOB_TTL_S = int(os.environ.get("JOB_TTL_MINUTES", "15")) * 60
# On vérifie plusieurs fois par TTL, sinon un TTL court n'est pas tenu.
CLEANUP_INTERVAL_S = max(60, min(300, JOB_TTL_S // 5))
# Notification Discord à chaque conversion réussie (vide = désactivé).
DISCORD_WEBHOOK_URL = os.environ.get("DISCORD_WEBHOOK_URL", "").strip()
SEARCH_RESULTS = 12
DOWNLOAD_ATTEMPTS = 3

YOUTUBE_URL_RE = re.compile(
    r"^(https?://)?(www\.|m\.|music\.)?(youtube\.com|youtu\.be)/", re.IGNORECASE
)

jobs: dict[str, dict] = {}
jobs_lock = threading.Lock()


class JobError(Exception):
    """Erreur métier, message affichable tel quel à l'utilisateur."""


def base_ydl_opts() -> dict:
    opts = {"quiet": True, "no_warnings": True, "noplaylist": True}
    if YTDLP_PROXY:
        opts["proxy"] = YTDLP_PROXY
    if POT_PROVIDER_URL:
        opts["extractor_args"] = {"youtubepot-bgutilhttp": {"base_url": [POT_PROVIDER_URL]}}
    if COOKIES_FILE.is_file():
        opts["cookiefile"] = str(COOKIES_FILE)
    return opts


def set_job(job_id: str, **updates) -> None:
    with jobs_lock:
        job = jobs.get(job_id)
        if job is not None:
            job.update(updates)


def public_job(job: dict) -> dict:
    return {k: job[k] for k in ("id", "status", "progress", "title", "thumbnail", "filename", "error", "format")}


def human_duration(seconds: float | None) -> str:
    if not seconds:
        return "—"
    seconds = int(seconds)
    h, m, s = seconds // 3600, (seconds % 3600) // 60, seconds % 60
    return f"{h}:{m:02d}:{s:02d}" if h else f"{m}:{s:02d}"


def human_size(num_bytes: int | None) -> str:
    if not num_bytes:
        return "—"
    mb = num_bytes / (1024 * 1024)
    return f"{mb:.1f} Mo" if mb >= 1 else f"{num_bytes / 1024:.0f} Ko"


def notify_discord(job: dict) -> None:
    """Prévient Discord qu'une conversion est terminée. Jamais bloquant."""
    if not DISCORD_WEBHOOK_URL:
        return
    elapsed = time.time() - job["created_at"]
    fields = [
        {"name": "Format", "value": job["format"].upper(), "inline": True},
        {"name": "Durée", "value": human_duration(job.get("duration")), "inline": True},
        {"name": "Taille", "value": human_size(job.get("filesize")), "inline": True},
        {"name": "Chaîne", "value": job.get("channel") or "—", "inline": True},
        {"name": "Traitement", "value": f"{elapsed:.0f} s", "inline": True},
        {"name": "Tentatives", "value": str(job.get("attempts", 1)), "inline": True},
        {"name": "Fichier", "value": f"`{job['filename']}`", "inline": False},
    ]
    if job.get("client_ip"):
        fields.append({"name": "Depuis", "value": job["client_ip"], "inline": True})
    payload = {
        "username": "Yayo Extractor",
        "embeds": [{
            "title": (job.get("title") or "Sans titre")[:250],
            "url": job.get("source_url"),
            "description": "Nouvelle musique téléchargée",
            "color": 0xFF6B35,
            "thumbnail": {"url": job["thumbnail"]} if job.get("thumbnail") else None,
            "fields": fields,
            "footer": {"text": f"Yayo Extractor • job {job['id'][:8]}"},
            "timestamp": time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime()) + "Z",
        }],
    }
    request = urllib.request.Request(
        DISCORD_WEBHOOK_URL,
        data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json", "User-Agent": "YayoExtractor/1.0"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=10) as resp:
            log.info("job %s : Discord notifié (HTTP %s)", job["id"], resp.status)
    except urllib.error.HTTPError as exc:
        log.warning("job %s : Discord a refusé la notification (HTTP %s)", job["id"], exc.code)
    except Exception as exc:
        log.warning("job %s : notification Discord impossible (%s)", job["id"], exc)


def _download(job_id: str, url: str, fmt: str, job_dir: Path) -> None:
    shutil.rmtree(job_dir, ignore_errors=True)
    job_dir.mkdir(parents=True, exist_ok=True)
    set_job(job_id, status="fetching")

    with yt_dlp.YoutubeDL(base_ydl_opts()) as ydl:
        meta = ydl.extract_info(url, download=False)
    duration = meta.get("duration") or 0
    if MAX_DURATION_S and duration > MAX_DURATION_S:
        raise JobError(f"Cette vidéo est trop longue (maximum {MAX_DURATION_S // 60} minutes).")
    set_job(
        job_id,
        title=meta.get("title"),
        thumbnail=meta.get("thumbnail"),
        duration=duration,
        channel=meta.get("channel") or meta.get("uploader"),
        source_url=meta.get("webpage_url") or url,
        status="downloading",
    )

    def hook(d: dict) -> None:
        if d["status"] == "downloading":
            total = d.get("total_bytes") or d.get("total_bytes_estimate")
            updates = {"status": "downloading"}
            if total:
                updates["progress"] = min(round(d.get("downloaded_bytes", 0) / total * 100, 1), 100.0)
            set_job(job_id, **updates)
        elif d["status"] == "finished":
            set_job(job_id, status="converting", progress=100.0)

    opts = base_ydl_opts() | {
        "outtmpl": str(job_dir / "%(title).180B.%(ext)s"),
        "progress_hooks": [hook],
    }
    if fmt == "mp3":
        opts |= {
            "format": "bestaudio/best",
            "writethumbnail": True,
            "postprocessors": [
                {"key": "FFmpegThumbnailsConvertor", "format": "jpg"},
                {"key": "FFmpegExtractAudio", "preferredcodec": "mp3", "preferredquality": "192"},
                {"key": "FFmpegMetadata"},
                {"key": "EmbedThumbnail"},
            ],
        }
    else:
        opts |= {
            "format": "bestvideo[ext=mp4][height<=1080]+bestaudio[ext=m4a]/best[ext=mp4]/best",
            "merge_output_format": "mp4",
        }

    with yt_dlp.YoutubeDL(opts) as ydl:
        ydl.download([url])

    wanted = ".mp3" if fmt == "mp3" else ".mp4"
    files = sorted(p for p in job_dir.iterdir() if p.suffix.lower() == wanted)
    if not files:
        raise JobError("Le fichier converti est introuvable, réessaie.")
    set_job(
        job_id,
        status="done",
        progress=100.0,
        file=str(files[0]),
        filename=files[0].name,
        filesize=files[0].stat().st_size,
    )
    log.info("job %s terminé : %s", job_id, files[0].name)
    with jobs_lock:
        snapshot = dict(jobs[job_id])
    notify_discord(snapshot)


def run_job(job_id: str, url: str, fmt: str) -> None:
    job_dir = JOBS_DIR / job_id
    try:
        # Même via WARP, YouTube refuse parfois une requête au hasard (~20 %) :
        # on retente la totalité du téléchargement avant de déclarer l'échec.
        for attempt in range(1, DOWNLOAD_ATTEMPTS + 1):
            try:
                set_job(job_id, attempts=attempt)
                _download(job_id, url, fmt, job_dir)
                return
            except yt_dlp.utils.DownloadError as exc:
                if attempt == DOWNLOAD_ATTEMPTS:
                    raise
                log.warning("job %s : tentative %d/%d échouée, on réessaie (%s)",
                            job_id, attempt, DOWNLOAD_ATTEMPTS, exc)
                set_job(job_id, status="retrying", progress=0.0)
                time.sleep(2 * attempt)
    except JobError as exc:
        set_job(job_id, status="error", error=str(exc))
        shutil.rmtree(job_dir, ignore_errors=True)
    except yt_dlp.utils.DownloadError as exc:
        log.warning("job %s en échec : %s", job_id, exc)
        set_job(job_id, status="error", error="Le téléchargement a échoué. Vérifie le lien, ou réessaie un peu plus tard.")
        shutil.rmtree(job_dir, ignore_errors=True)
    except Exception:
        log.exception("job %s : erreur inattendue", job_id)
        set_job(job_id, status="error", error="Une erreur inattendue s'est produite, réessaie.")
        shutil.rmtree(job_dir, ignore_errors=True)


def cleanup_loop() -> None:
    while True:
        time.sleep(CLEANUP_INTERVAL_S)
        cutoff = time.time() - JOB_TTL_S
        with jobs_lock:
            stale = [job_id for job_id, job in jobs.items() if job["created_at"] < cutoff]
            for job_id in stale:
                del jobs[job_id]
        for job_id in stale:
            shutil.rmtree(JOBS_DIR / job_id, ignore_errors=True)
        if stale:
            log.info("nettoyage : %d job(s) supprimé(s)", len(stale))


@asynccontextmanager
async def lifespan(_: FastAPI):
    JOBS_DIR.mkdir(parents=True, exist_ok=True)
    for child in JOBS_DIR.iterdir():
        shutil.rmtree(child, ignore_errors=True)
    threading.Thread(target=cleanup_loop, daemon=True).start()
    yield


app = FastAPI(title="Yayo Extractor", lifespan=lifespan)


@app.get("/api/health")
def health():
    return {"status": "ok"}


@app.get("/api/search")
def search(q: str):
    q = q.strip()
    if not q:
        raise HTTPException(status_code=400, detail="Écris quelque chose à rechercher.")
    opts = base_ydl_opts() | {"extract_flat": True, "skip_download": True}
    try:
        with yt_dlp.YoutubeDL(opts) as ydl:
            info = ydl.extract_info(f"ytsearch{SEARCH_RESULTS}:{q}", download=False)
    except yt_dlp.utils.DownloadError as exc:
        log.warning("recherche en échec : %s", exc)
        raise HTTPException(status_code=502, detail="La recherche YouTube a échoué, réessaie dans un instant.")
    results = []
    for entry in (info or {}).get("entries") or []:
        video_id = entry.get("id")
        if not video_id:
            continue
        results.append({
            "id": video_id,
            "title": entry.get("title") or "Sans titre",
            "channel": entry.get("channel") or entry.get("uploader") or "",
            "duration": entry.get("duration"),
            "url": f"https://www.youtube.com/watch?v={video_id}",
            "thumbnail": f"https://i.ytimg.com/vi/{video_id}/mqdefault.jpg",
        })
    return {"results": results}


class JobRequest(BaseModel):
    url: str
    format: str = "mp3"


@app.post("/api/jobs")
def create_job(req: JobRequest, request: Request):
    url = req.url.strip()
    fmt = req.format.lower().strip()
    if not YOUTUBE_URL_RE.match(url):
        raise HTTPException(status_code=400, detail="Ce lien ne ressemble pas à un lien YouTube.")
    if fmt not in ("mp3", "mp4"):
        raise HTTPException(status_code=400, detail="Format inconnu (mp3 ou mp4 uniquement).")
    job_id = uuid.uuid4().hex
    # Traefik est en frontal : l'IP réelle est dans X-Forwarded-For.
    forwarded = request.headers.get("x-forwarded-for", "")
    client_ip = forwarded.split(",")[0].strip() or (request.client.host if request.client else "")
    with jobs_lock:
        jobs[job_id] = {
            "id": job_id,
            "status": "queued",
            "progress": 0.0,
            "title": None,
            "thumbnail": None,
            "filename": None,
            "error": None,
            "format": fmt,
            "file": None,
            "duration": None,
            "channel": None,
            "filesize": None,
            "source_url": url,
            "client_ip": client_ip,
            "attempts": 0,
            "created_at": time.time(),
        }
    threading.Thread(target=run_job, args=(job_id, url, fmt), daemon=True).start()
    return {"id": job_id}


@app.get("/api/jobs/{job_id}")
def get_job(job_id: str):
    with jobs_lock:
        job = jobs.get(job_id)
        if job is None:
            raise HTTPException(status_code=404, detail="Job introuvable.")
        return public_job(job)


def ready_file(job_id: str) -> tuple[str, str, str]:
    with jobs_lock:
        job = jobs.get(job_id)
        if job is None or job["status"] != "done" or not job["file"]:
            raise HTTPException(status_code=404, detail="Fichier indisponible.")
        return job["file"], job["filename"], job["format"]


@app.get("/api/jobs/{job_id}/file")
def get_file(job_id: str):
    path, filename, fmt = ready_file(job_id)
    media_type = "audio/mpeg" if fmt == "mp3" else "video/mp4"
    return FileResponse(path, filename=filename, media_type=media_type)


@app.get("/api/jobs/{job_id}/stream")
def stream_file(job_id: str):
    """Même fichier, servi en lecture (inline) pour le lecteur de la page.
    Starlette gère les requêtes Range, donc on peut avancer dans le morceau."""
    path, filename, fmt = ready_file(job_id)
    media_type = "audio/mpeg" if fmt == "mp3" else "video/mp4"
    return FileResponse(path, filename=filename, media_type=media_type,
                        content_disposition_type="inline")


app.mount("/", StaticFiles(directory=Path(__file__).parent / "static", html=True), name="static")
