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

`docker compose up -d --build` ne reconstruit **que** le service `app` : les sidecars
gardent l'image tirée la première fois. Ils sont donc épinglés sur des tags de version
explicites et bumpés à la main, plutôt que suivis en `latest` — un auto-pull de WARP
lors du déploiement automatique du lundi 4h pourrait casser le tunnel (donc tous les
téléchargements) sans personne pour le voir. Le workflow signale les nouvelles versions
disponibles en warning, ce qui rend l'épinglage visible au lieu de l'oublier.

Après déploiement, le workflow vérifie que le plugin pip bgutil et le serveur
`potprovider` partagent le même `major.minor`, et **échoue** sinon — voir Maintenance.

Secrets GitHub requis : `VPS_HOST`, `VPS_USER`, `VPS_SSH_KEY` (clé privée dédiée au déploiement).

## Maintenance

La version de yt-dlp réellement embarquée est loggée au démarrage
(`docker compose logs app | grep "démarrage avec"`) — à vérifier en premier
si les téléchargements échouent. Pour forcer une mise à jour à la main :

```bash
YTDLP_REFRESH=$(date +%s) docker compose up -d --build app
```

### Bumper les sidecars

Les deux images sont épinglées dans `compose.yaml`. Le workflow affiche en warning
quand une version plus récente existe (visible chaque lundi via le cron).

**bgutil (`potprovider`)** — contrainte dure : le plugin pip et le serveur doivent
partager le même `major.minor`, sinon plus aucun PO token n'est généré et tous les
téléchargements YouTube échouent. Le plugin est donc figé lui aussi (`==` dans
`requirements.txt`) : non figé, `YTDLP_REFRESH` le ferait dériver tout seul à chaque
déploiement pendant que l'image du sidecar reste sur son tag. **Les deux lignes se
bumpent dans le même commit :**

```bash
# requirements.txt : bgutil-ytdlp-pot-provider==<version>
# compose.yaml     : BGUTIL_VERSION:-<version>
```

Un bump d'un seul côté fait échouer le déploiement (contrôle post-déploiement
comparant `pip show` et le `/ping` du sidecar) plutôt que de casser la prod en silence.
Pour tester une version sans commit : `BGUTIL_VERSION=1.4.0 docker compose up -d potprovider`.

**warp** — aucun couplage, mais c'est le tunnel par lequel sort tout le trafic yt-dlp.
Bumper le tag, puis vérifier que le tunnel remonte avant de considérer le déploiement bon :

```bash
docker compose up -d warp
docker compose exec -T app python - <<'PY'
from yt_dlp import YoutubeDL
from yt_dlp.networking import Request
with YoutubeDL({"proxy": "socks5h://warp:1080", "quiet": True}) as y:
    body = y.urlopen(Request("https://www.cloudflare.com/cdn-cgi/trace")).read().decode()
print([l for l in body.splitlines() if l.startswith(("warp=", "ip="))])
PY
```

`warp=on` attendu, avec une IP Cloudflare. Le test passe par le proxy SOCKS et la pile
réseau de yt-dlp, donc exactement le chemin des téléchargements (`curl` n'est pas
installé dans l'image `app`). Si `warp=off` ou si l'appel échoue, revenir au tag
précédent : le trafic sortirait en direct depuis l'IP du VPS, que YouTube bloque.

Si YouTube bloque l'IP du serveur (« Sign in to confirm you're not a bot »),
exporter un `cookies.txt` depuis un navigateur connecté et le placer dans le volume :

```bash
docker cp cookies.txt yayoextractor:/data/cookies.txt
```
