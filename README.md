# 🎵 Yayo Extractor

Petit site pour chercher une vidéo YouTube et la récupérer en **MP3** (ou MP4).
Backend Python (FastAPI + yt-dlp + ffmpeg), interface statique servie par le même conteneur.

**Production :** https://yayoextractor.louis-nectoux.fr

## Déploiement sur le VPS

```bash
cd /srv/docker/apps
git clone https://github.com/Sharkooss/yayoextractor.git
cd yayoextractor
cp .env.example .env
nano .env          # ajuster APP_DOMAIN si besoin
docker compose up -d --build
docker compose logs -f
```

Aucune migration ni commande d'initialisation : le conteneur est autonome.

## Infos utiles

- **Port interne** : `8000` (routé par Traefik via les labels, aucun port publié).
- **Domaine** : `APP_DOMAIN` dans `.env` — le sous-domaine doit pointer vers le VPS (A → 92.222.247.229).
- **Variables** (`.env`) :
  - `APP_DOMAIN` — obligatoire, domaine public.
  - `MAX_DURATION_MINUTES` — durée max des vidéos acceptées (défaut 180).
  - `JOB_TTL_MINUTES` — durée de conservation des fichiers convertis (défaut 15).
    Un fil de nettoyage supprime les fichiers expirés, et tout `/data/jobs` est
    vidé au démarrage : rien ne s'accumule sur le disque du serveur.
  - `DISCORD_WEBHOOK_URL` — facultatif : notification Discord à chaque conversion
    réussie (titre, chaîne, durée, taille, temps de traitement). Vide = désactivé.
    À renseigner uniquement dans le `.env` du serveur, jamais dans le dépôt.
- **Volume** : `downloads` monté sur `/data` (fichiers temporaires de conversion, nettoyés automatiquement).
- **Healthcheck** : `GET /api/health` (intégré au Dockerfile).
- **Sidecar `warp`** : proxy Cloudflare WARP ([caomingjun/warp](https://hub.docker.com/r/caomingjun/warp)).
  YouTube bloque les IP de datacenter (« Sign in to confirm you're not a bot ») ;
  tout le trafic yt-dlp sort par ce tunnel (`YTDLP_PROXY`). Réseau interne uniquement.
- **Sidecar `potprovider`** : génère les PO tokens exigés par YouTube
  ([bgutil-ytdlp-pot-provider](https://github.com/Brainicism/bgutil-ytdlp-pot-provider)).
- **Deno + yt-dlp-ejs** (dans l'image) : résolvent les défis JavaScript de YouTube.
- **Clients YouTube de secours** (`YOUTUBE_PLAYER_CLIENTS` dans `app/main.py`) :
  le client par défaut ne renvoie aucun format sur certaines vidéos
  (« This video is not available ») ; `mweb` et `tv` prennent le relais.
  Un nom inconnu de yt-dlp est ignoré silencieusement : vérifier qu'il existe
  encore (`INNERTUBE_CLIENTS`) avant de compter dessus.

## CI/CD

Chaque push sur `main` déclenche `.github/workflows/deploy.yml` : connexion SSH au VPS,
`git reset --hard origin/main` puis `docker compose up -d --build`. Le workflow tourne
aussi chaque lundi, pour récupérer le dernier yt-dlp même sans nouveau commit.

Le build reçoit `YTDLP_REFRESH` (horodatage) : sans lui, Docker réutilise la couche
`pip install` et fige yt-dlp — c'est ce qui a cassé la prod le 05/09/2026, l'image
d'août tournant avec un yt-dlp de juillet (« HTTP Error 403: Forbidden »).

Secrets GitHub requis : `VPS_HOST`, `VPS_USER`, `VPS_SSH_KEY` (clé privée dédiée au déploiement).

## Maintenance

La version de yt-dlp réellement embarquée est loggée au démarrage
(`docker compose logs app | grep "démarrage avec"`) — à vérifier en premier
si les téléchargements échouent. Pour forcer une mise à jour à la main :

```bash
YTDLP_REFRESH=$(date +%s) docker compose up -d --build app
```

Si YouTube bloque l'IP du serveur (« Sign in to confirm you're not a bot »),
exporter un `cookies.txt` depuis un navigateur connecté et le placer dans le volume :

```bash
docker cp cookies.txt yayoextractor:/data/cookies.txt
```
