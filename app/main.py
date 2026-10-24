"""Yayo Extractor — YouTube vers MP3/MP4, et réparation de photos, pensé pour Yayo."""

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
import zipfile
from contextlib import asynccontextmanager
from pathlib import Path

import yt_dlp
from fastapi import FastAPI, File, HTTPException, Request, UploadFile
from fastapi.responses import FileResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel

from app import geo, photos

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("yayoextractor")

DATA_DIR = Path(os.environ.get("DATA_DIR", "/data"))
JOBS_DIR = DATA_DIR / "jobs"
PHOTOS_DIR = DATA_DIR / "photos"
# Géocodage inverse (tri par lieu de l'onglet Téléphone) : données GeoNames persistantes.
geocoder = geo.Geocoder(DATA_DIR / "geonames")
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
# Sur certaines vidéos, les clients YouTube par défaut ne renvoient aucun format
# et yt-dlp conclut « This video is not available ». On ajoute deux clients de
# secours qui, eux, répondent : il suffit qu'un seul aboutisse.
# Les noms doivent exister dans yt-dlp, sinon ils sont ignorés en silence
# (`no_warnings`) : « tv_embedded » a disparu et ne servait donc plus de filet.
YOUTUBE_PLAYER_CLIENTS = ["default", "mweb", "tv"]
# Réparation de photos : une requête par photo, pour qu'un envoi lent ne bute
# pas sur le délai de lecture de Traefik (60 s) comme le ferait un lot entier.
MAX_PHOTOS_PER_BATCH = 60
MAX_PHOTO_BYTES = 60 * 1024 * 1024

YOUTUBE_URL_RE = re.compile(
    r"^(https?://)?(www\.|m\.|music\.)?(youtube\.com|youtu\.be)/", re.IGNORECASE
)

jobs: dict[str, dict] = {}
jobs_lock = threading.Lock()
# Lots de photos réparées, mêmes règles d'expiration que les jobs (JOB_TTL_MINUTES).
photo_batches: dict[str, dict] = {}
photos_lock = threading.Lock()


class JobError(Exception):
    """Erreur métier, message affichable tel quel à l'utilisateur."""


def base_ydl_opts() -> dict:
    opts = {"quiet": True, "no_warnings": True, "noplaylist": True}
    extractor_args = {"youtube": {"player_client": YOUTUBE_PLAYER_CLIENTS}}
    if YTDLP_PROXY:
        opts["proxy"] = YTDLP_PROXY
    if POT_PROVIDER_URL:
        extractor_args["youtubepot-bgutilhttp"] = {"base_url": [POT_PROVIDER_URL]}
    opts["extractor_args"] = extractor_args
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


def purge_expired(registry: dict[str, dict], lock: threading.Lock, root: Path, cutoff: float) -> int:
    with lock:
        stale = [key for key, entry in registry.items() if entry["created_at"] < cutoff]
        for key in stale:
            del registry[key]
    for key in stale:
        shutil.rmtree(root / key, ignore_errors=True)
    return len(stale)


def cleanup_loop() -> None:
    while True:
        time.sleep(CLEANUP_INTERVAL_S)
        cutoff = time.time() - JOB_TTL_S
        stale_jobs = purge_expired(jobs, jobs_lock, JOBS_DIR, cutoff)
        stale_photos = purge_expired(photo_batches, photos_lock, PHOTOS_DIR, cutoff)
        if stale_jobs or stale_photos:
            log.info("nettoyage : %d job(s) et %d lot(s) de photos supprimés", stale_jobs, stale_photos)


@asynccontextmanager
async def lifespan(_: FastAPI):
    # Trace la version : une image construite sur un cache Docker perime
    # embarque un yt-dlp trop vieux, et YouTube renvoie alors des « 403
    # Forbidden » impossibles a rattacher a leur cause sans cette ligne.
    log.info("démarrage avec yt-dlp %s", yt_dlp.version.__version__)
    for root in (JOBS_DIR, PHOTOS_DIR):
        root.mkdir(parents=True, exist_ok=True)
        for child in root.iterdir():
            shutil.rmtree(child, ignore_errors=True)
    threading.Thread(target=cleanup_loop, daemon=True).start()
    # En tâche de fond : ni le démarrage ni le healthcheck n'attendent GeoNames.
    threading.Thread(target=geocoder.prefetch, daemon=True).start()
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


# ---------- Réparation de photos ----------


@app.post("/api/photos")
def create_photo_batch():
    batch_id = uuid.uuid4().hex
    (PHOTOS_DIR / batch_id).mkdir(parents=True, exist_ok=True)
    with photos_lock:
        photo_batches[batch_id] = {"created_at": time.time(), "items": []}
    return {"id": batch_id}


@app.post("/api/photos/{batch_id}")
def add_photo(batch_id: str, file: UploadFile = File(...)):
    """Répare une photo du lot. L'original n'est pas conservé : seul le tampon
    d'envoi de Starlette l'a contenu, et il disparaît avec la requête."""
    if file.size is not None and file.size > MAX_PHOTO_BYTES:
        raise HTTPException(status_code=413, detail="Cette photo est trop lourde (60 Mo maximum).")
    with photos_lock:
        batch = photo_batches.get(batch_id)
        if batch is None:
            raise HTTPException(status_code=404, detail="Session expirée, recharge la page.")
        index = len(batch["items"])
        if index >= MAX_PHOTOS_PER_BATCH:
            raise HTTPException(status_code=400,
                                detail=f"{MAX_PHOTOS_PER_BATCH} photos maximum à la fois.")
        # Réserve l'index tout de suite : les envois arrivent en parallèle.
        batch["items"].append(None)

    name = file.filename or "photo"
    item = {"index": index, "name": name, "ok": False, "error": None, "filename": None,
            "size_in": file.size, "size_out": None}
    try:
        data, diagnosis = photos.repair(file.file)
        (PHOTOS_DIR / batch_id / f"{index}.jpg").write_bytes(data)
        item |= diagnosis | {
            "ok": True,
            "size_out": len(data),
            "issue_labels": [photos.ISSUE_LABELS[code] for code in diagnosis["issues"]],
        }
        log.info("photos %s #%d : %s %dx%d -> baseline (%s)", batch_id[:8], index,
                 diagnosis["format"], diagnosis["orig_width"], diagnosis["orig_height"],
                 ", ".join(diagnosis["issues"]) or "rien à signaler")
    except photos.PhotoError as exc:
        item["error"] = str(exc)
    except Exception:
        log.exception("photos %s #%d : erreur inattendue", batch_id[:8], index)
        item["error"] = "Une erreur inattendue s'est produite sur cette photo."

    with photos_lock:
        batch = photo_batches.get(batch_id)
        if batch is not None:
            if item["ok"]:
                # Nom choisi sous le verrou : deux photos du lot peuvent porter le même.
                taken = {other["filename"] for other in batch["items"] if other and other["filename"]}
                filename = base = photos.output_name(name)
                n = 2
                while filename in taken:
                    filename, n = f"{base[:-4]}-{n}.jpg", n + 1
                item["filename"] = filename
            batch["items"][index] = item
    return item


def photo_file(batch_id: str, index: int) -> tuple[Path, str]:
    with photos_lock:
        batch = photo_batches.get(batch_id)
        items = batch["items"] if batch else []
        item = items[index] if 0 <= index < len(items) else None
        if not item or not item["ok"]:
            raise HTTPException(status_code=404, detail="Photo expirée ou introuvable.")
        return PHOTOS_DIR / batch_id / f"{index}.jpg", item["filename"]


@app.get("/api/photos/{batch_id}/zip")
def get_photos_zip(batch_id: str):
    with photos_lock:
        batch = photo_batches.get(batch_id)
        ready = [item for item in (batch["items"] if batch else []) if item and item["ok"]]
    if not ready:
        raise HTTPException(status_code=404, detail="Photos expirées ou introuvables.")
    batch_dir = PHOTOS_DIR / batch_id
    archive = batch_dir / "photos-corrigees.zip"
    partial = batch_dir / f"{uuid.uuid4().hex}.zip.part"
    # Les JPEG sont déjà compressés : ZIP_STORED, aussi rapide qu'une copie.
    with zipfile.ZipFile(partial, "w", zipfile.ZIP_STORED) as zf:
        for item in ready:
            zf.write(batch_dir / f"{item['index']}.jpg", item["filename"])
    os.replace(partial, archive)
    return FileResponse(archive, filename="photos-corrigees.zip", media_type="application/zip")


@app.get("/api/photos/{batch_id}/{index}/file")
def get_photo(batch_id: str, index: int):
    path, filename = photo_file(batch_id, index)
    return FileResponse(path, filename=filename, media_type="image/jpeg")


@app.get("/api/photos/{batch_id}/{index}/view")
def view_photo(batch_id: str, index: int):
    path, filename = photo_file(batch_id, index)
    return FileResponse(path, filename=filename, media_type="image/jpeg",
                        content_disposition_type="inline")


# ---------- Géocodage inverse ----------


class GeoRequest(BaseModel):
    # Non typé point par point : un point invalide donne `null` à sa place
    # au lieu de faire rejeter toute la requête.
    points: list


@app.post("/api/geo/reverse")
def geo_reverse(req: GeoRequest):
    """Ville, département, région et pays de chaque point. Les coordonnées ne sont
    ni conservées ni journalisées."""
    if len(req.points) > geo.MAX_POINTS:
        raise HTTPException(status_code=400,
                            detail=f"{geo.MAX_POINTS} points maximum par requête.")
    try:
        return {"places": geocoder.reverse(req.points)}
    except geo.GeoUnavailable as exc:
        raise HTTPException(status_code=503, detail=str(exc))


app.mount("/", StaticFiles(directory=Path(__file__).parent / "static", html=True), name="static")
