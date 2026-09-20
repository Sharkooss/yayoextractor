#!/usr/bin/env bash
# Canari yayoextractor : vérifie que YouTube répond encore, et répare.
#
# Panne observée le 20/09/2026 : l'IP de sortie du tunnel WARP finit par être
# grillée par YouTube, qui répond alors « LOGIN_REQUIRED » sur TOUS les clients
# (web, mweb, tv, android, ios…). Symptôme côté utilisateur : « Sign in to
# confirm you're not a bot » sur chaque vidéo. Le conteneur reste healthy et
# les logs n'ont rien d'anormal, donc rien ne le signale.
#
# Deux pièges qui justifient la forme de ce script :
#   - Mettre à jour yt-dlp ne répare PAS cette panne (la nightly 2026.09.16
#     échouait autant que la stable). Le remède est la rotation de l'IP.
#   - Une IP grillée ne bloque pas 100 % des vidéos : dQw4w9WgXcQ répondait
#     encore quand tout le reste était bloqué. Un canari mono-vidéo peut donc
#     annoncer « tout va bien » sur une app morte — d'où les deux vidéos.
#
# Usage :
#   canary.sh             vérifie, et répare si besoin
#   canary.sh --rebuild   rafraîchit d'abord yt-dlp (passage quotidien de 6h)
#   canary.sh --probe     vérifie seulement, sans rien réparer (diagnostic)

set -uo pipefail

APP_DIR="/srv/docker/apps/yayoextractor"
APP_CONTAINER="yayoextractor"
WARP_CONTAINER="yayoextractor-warp"
WARP_VOLUME="yayoextractor_warp_data"
# Une reconnexion tire une IP de sortie au hasard, qui peut elle aussi être
# déjà grillée : mesuré le 20/09/2026, la 1re rotation a débloqué l'app, la
# 2e est retombée sur une IP bloquée. Il faut donc insister avant d'escalader.
ROTATION_ATTEMPTS=6
REBUILD=0
[ "${1:-}" = "--rebuild" ] && REBUILD=1

log() { echo "$(date '+%Y-%m-%d %H:%M:%S') $*"; }

# Le webhook sert déjà aux notifications de conversion, on le réutilise.
WEBHOOK="$(grep -s '^DISCORD_WEBHOOK_URL=' "$APP_DIR/.env" | cut -d= -f2- | tr -d '"'"'"'')"

alert() {
  log "ALERTE : $1"
  [ -z "$WEBHOOK" ] && return 0
  local payload
  payload=$(printf '{"username":"Yayo Extractor","embeds":[{"title":"Yayo Extractor en panne","description":%s,"color":15158332}]}' \
    "$(printf '%s' "$1" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))')")
  curl -s -m 10 -X POST -H 'Content-Type: application/json' -d "$payload" "$WEBHOOK" >/dev/null
}

# Sonde : rejoue exactement le chemin de l'app (base_ydl_opts), pas une
# variante simplifiée qui pourrait réussir là où l'app échoue.
# Sortie : 0 = sain, 2 = bloqué par YouTube, 3 = autre erreur.
probe() {
  docker exec -i -w /srv/app "$APP_CONTAINER" python - <<'PY'
import sys
import yt_dlp
from app.main import base_ydl_opts

# Deux vidéos stables et très vues. On exige que les DEUX répondent : une IP
# grillée laisse parfois passer une vidéo isolée (cf. en-tête du script).
VIDEOS = ["aircAruvnKk", "8aGhZQkoFbQ"]
blocked = False
for vid in VIDEOS:
    try:
        with yt_dlp.YoutubeDL(base_ydl_opts()) as ydl:
            info = ydl.extract_info(f"https://www.youtube.com/watch?v={vid}", download=False)
    except Exception as exc:
        message = str(exc)
        # On ne distingue que ce qui change le remède : un blocage se répare
        # par une rotation d'IP, une vidéo supprimée ne se répare pas du tout
        # (et ne doit surtout pas déclencher de rotation en boucle).
        if "not a bot" in message or "LOGIN_REQUIRED" in message:
            print(f"{vid} : bloqué par YouTube")
            blocked = True
        else:
            print(f"{vid} : {message[:200]}")
            sys.exit(3)
        continue
    if not (info or {}).get("formats"):
        print(f"{vid} : aucun format renvoyé")
        blocked = True
sys.exit(2 if blocked else 0)
PY
}

# Rotation du tunnel : WARP se réattribue une IP de sortie à la reconnexion.
# C'est le geste qui a réparé la panne du 20/09/2026.
rotate_warp() {
  docker exec "$WARP_CONTAINER" warp-cli --accept-tos disconnect >/dev/null 2>&1
  sleep 3
  docker exec "$WARP_CONTAINER" warp-cli --accept-tos connect >/dev/null 2>&1
  sleep 10
}

# Si la reconnexion ne suffit pas, on repart d'une inscription WARP neuve :
# le volume ne contient que l'identité du client, recréée au démarrage.
renew_warp_identity() {
  cd "$APP_DIR" || return 1
  docker compose stop warp >/dev/null 2>&1
  docker compose rm -f warp >/dev/null 2>&1
  docker volume rm "$WARP_VOLUME" >/dev/null 2>&1
  docker compose up -d warp >/dev/null 2>&1
  for _ in $(seq 1 30); do
    [ "$(docker inspect -f '{{.State.Health.Status}}' "$WARP_CONTAINER" 2>/dev/null)" = "healthy" ] && break
    sleep 5
  done
  sleep 5
}

# Dernier recours : yt-dlp périmé (panne du 05/09/2026, cause opposée).
rebuild_app() {
  cd "$APP_DIR" || return 1
  YTDLP_REFRESH="$(date +%Y%m%d%H%M%S)" docker compose up -d --build >/dev/null 2>&1
  sleep 15
}

# Le détail de l'échec a déjà été journalisé une fois ; ici on ne garde que
# le verdict, sinon chaque tentative recrache la trace yt-dlp complète.
step() {
  local name="$1"; shift
  log "→ $name"
  "$@"
  if probe >/dev/null 2>&1; then
    log "rétabli par : $name"
    return 0
  fi
  return 1
}

main() {
  if [ "${1:-}" = "--probe" ]; then
    output="$(probe)"; rc=$?
    log "sonde : code $rc ${output:+- $output}"
    return $rc
  fi

  if [ "$REBUILD" = 1 ]; then
    log "passage quotidien : rafraîchissement de yt-dlp"
    rebuild_app
    docker builder prune -f --max-used-space 5GB >/dev/null 2>&1
  fi

  output="$(probe)"; rc=$?
  if [ $rc -eq 0 ]; then
    log "OK"
    return 0
  fi
  log "sonde en échec (code $rc) : $output"

  if [ $rc -eq 3 ]; then
    # Erreur inattendue : on retente une fois avant d'ameuter.
    sleep 20
    probe && { log "OK au second essai"; return 0; }
  fi

  # Du moins cher au plus cher, on s'arrête dès que la sonde repasse.
  for attempt in $(seq 1 "$ROTATION_ATTEMPTS"); do
    step "rotation du tunnel WARP ($attempt/$ROTATION_ATTEMPTS)" rotate_warp && return 0
  done
  step "réinscription WARP (nouvelle identité)" renew_warp_identity && return 0
  step "reconstruction de l'app (yt-dlp à jour)" rebuild_app && return 0

  alert "La sonde YouTube échoue encore après rotation WARP, réinscription et reconstruction. Dernier retour : $(probe 2>&1 | head -5)"
  return 1
}

main "$@"
