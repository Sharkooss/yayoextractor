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
  - `JOB_TTL_MINUTES` — durée de conservation des fichiers convertis (défaut 60).
- **Volume** : `downloads` monté sur `/data` (fichiers temporaires de conversion, nettoyés automatiquement).
- **Healthcheck** : `GET /api/health` (intégré au Dockerfile).
- **Sidecar `potprovider`** : génère les PO tokens exigés par YouTube pour les IP
  de serveur ([bgutil-ytdlp-pot-provider](https://github.com/Brainicism/bgutil-ytdlp-pot-provider)).
  Sans lui, YouTube répond « Sign in to confirm you're not a bot ». Réseau interne uniquement.

## CI/CD

Chaque push sur `main` déclenche `.github/workflows/deploy.yml` : connexion SSH au VPS,
`git reset --hard origin/main` puis `docker compose up -d --build`.

Secrets GitHub requis : `VPS_HOST`, `VPS_USER`, `VPS_SSH_KEY` (clé privée dédiée au déploiement).

## Maintenance

YouTube change régulièrement : si les téléchargements se mettent à échouer,
reconstruire l'image pour récupérer le dernier yt-dlp :

```bash
docker compose build --no-cache app && docker compose up -d
```

Si YouTube bloque l'IP du serveur (« Sign in to confirm you're not a bot »),
exporter un `cookies.txt` depuis un navigateur connecté et le placer dans le volume :

```bash
docker cp cookies.txt yayoextractor:/data/cookies.txt
```
