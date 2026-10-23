# 🎵 Yayo Extractor

Petit site pour chercher une vidéo YouTube et la récupérer en **MP3** (ou MP4), avec un
onglet **Photos** qui répare les photos que les logiciels de montage refusent d'importer, et un
onglet **Téléphone** qui copie les photos d'un téléphone vers une clé USB, sans doublons.
Backend Python (FastAPI + yt-dlp + ffmpeg + Pillow), interface statique servie par le même conteneur ;
l'onglet Téléphone s'appuie en plus sur le **Pont Yayo** (`bridge/`), un petit programme Windows.

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
- **Volume** : `downloads` monté sur `/data` (fichiers temporaires de conversion et photos
  réparées, dans `/data/jobs` et `/data/photos`, nettoyés automatiquement).
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

## Onglet Photos

Lien direct : `https://yayoextractor.louis-nectoux.fr/#photos`.

**Le problème d'origine (21/10/2026).** Les photos reçues par WhatsApp sont des JPEG
*progressifs* (marqueur `SOF2` au lieu de `SOF0`) : navigateurs et visionneuses les
affichent, mais beaucoup de logiciels de montage grand public ne décodent que le JPEG
« baseline » et refusent le fichier. Diagnostic rapide : `ffprobe -show_streams photo.jpg`
affiche `profile=Progressive`.

`app/photos.py` ré-encode chaque photo en JPEG baseline 8 bits, RVB, sRGB, sans
métadonnées, en reprenant les tables de quantification d'origine (qualité inchangée,
écart invisible). Au passage : HEIC/HEIF d'iPhone, PNG/WEBP/GIF/TIFF, rotation EXIF
appliquée pour de vrai, CMJN, transparence, 16 bits, profils couleur exotiques,
résolutions au-delà de 4096 px, fichiers tronqués. Noms de sortie ASCII, suffixés `-corrigee`.

- **API** : `POST /api/photos` crée un lot, puis `POST /api/photos/{lot}` (multipart, champ
  `file`) une requête **par photo** — un lot entier dans une seule requête dépasserait le
  délai de lecture de Traefik (60 s) sur une connexion lente. Téléchargement :
  `GET /api/photos/{lot}/{index}/file`, aperçu `…/view`, archive `GET /api/photos/{lot}/zip`.
- **Limites** : 60 photos par lot, 60 Mo par photo.
- **Confidentialité** : l'original n'est jamais conservé (lu depuis le tampon d'envoi, que
  Starlette supprime à la fin de la requête) ; les photos réparées vivent dans `/data/photos`
  et suivent `JOB_TTL_MINUTES` comme les jobs.
  La page garde aussi une copie en mémoire, donc les boutons de téléchargement marchent
  même après l'effacement côté serveur (seul le ZIP dépend du serveur).
- **Interface** : `app/static/photos.js` + `lab.css`. Pendant la réparation, « YAYO//LAB »
  joue un spectacle plein écran (bouton « Passer l'animation » ou Échap pour l'écourter).
  Il affiche de vraies données : octets et marqueurs JPEG du fichier, nombre de passes
  progressives, blocs 8×8 reconstruits.

## Onglet Téléphone et Pont Yayo

Lien direct : `https://yayoextractor.louis-nectoux.fr/#telephone`. Conception complète :
`docs/superpowers/specs/2026-10-23-transfert-telephone-design.md`.

**Pourquoi un programme local.** Un site ne peut pas lire un téléphone branché en USB sous Windows
(le téléphone y est un appareil MTP tenu par Windows, et Chrome refuse cette classe à WebUSB), ni
voir qu'une clé vient d'être branchée. Le site fait donc toute l'interface et parle à
`YayoPont.exe` sur `http://127.0.0.1:47863`, qui lit le téléphone via l'API Windows Portable
Devices (aucun réglage sur le téléphone) et écrit sur la clé.

- **Le pont** : C# sur .NET Framework 4.8 (fourni avec Windows 10/11), un seul exe d'environ 120 Ko,
  sans dépendance. Double-clic sur le fichier téléchargé = installation sans droits admin dans
  `%LOCALAPPDATA%\YayoPont`, démarrage avec la session (clé `Run`), entrée dans « Applications
  installées ». Journal : `%LOCALAPPDATA%\YayoPont\pont.log`.
- **SmartScreen** : l'exe n'est pas signé, Windows affiche « Windows a protégé votre ordinateur »
  à l'installation (une fois) ; l'onglet guide Yayo en images. La copie installée est réécrite
  sans la marque « téléchargé d'internet », donc pas d'alerte au démarrage de Windows.
- **Sécurité** : écoute sur `127.0.0.1` seulement, en-tête `Host` vérifié, origines limitées au
  site (et `localhost:8124` en dev), jeton de session pour tout le reste. Lecture seule sur le
  téléphone ; écriture seulement sous `Photos du téléphone` sur la clé choisie.
- **Rangement** : `<clé>\Photos du téléphone\AAAA\MM - Mois\`, date EXIF sinon date du téléphone.
  Déjà sur la clé = même taille et même nom, ou même taille et mêmes 64 premiers Ko (copies
  renommées). Copie en `.part`, vérifiée, puis renommée.

### Publier une nouvelle version du pont

Bumper `<Version>` dans `bridge/YayoPont/YayoPont.csproj` et pousser sur `main` :
`.github/workflows/bridge.yml` teste, construit et publie la release `pont-v<version>` avec
`YayoPont.exe`. Le site télécharge toujours `releases/latest/download/YayoPont.exe`. Si l'API
change de façon incompatible, augmenter `Api.Version` (pont) et `API_REQUIRED` (`phone.js`) :
le site proposera alors la mise à jour. Pour écarter une version défectueuse déjà installée,
monter `MIN_BRIDGE` dans `phone.js` (1.0.1 depuis le plantage WPD de la 1.0.0).

**Piège WPD** : en interop COM, un tableau .NET part en SAFEARRAY par défaut ; WPD attend des
tableaux C. Tout paramètre tableau des interfaces de `WpdInterop.cs` doit porter
`[MarshalAs(UnmanagedType.LPArray)]` (vérifié par `WpdInteropTests`). Sans téléphone branché, le
bug ne se voit pas. Diagnostic sur place : lancer le pont avec `YAYO_TRACE=1` pour tracer chaque
appel WPD dans `pont.log`.

### Développer sans téléphone ni clé

```bash
dotnet test bridge/YayoPont.sln
bridge/YayoPont/bin/Debug/net48/YayoPont.exe --faux-telephone <dossier> --fausse-cle <dossier> --lent
```

Le premier dossier joue le téléphone (un fichier `VERROUILLE` ou `RECHARGE` à sa racine simule
les états correspondants), le second la clé ; `--lent` bride la lecture à ~3 Mo/s pour voir
l'animation de copie. Le site local (`localhost:8124`) est une origine acceptée.

## CI/CD

Chaque push sur `main` déclenche `.github/workflows/deploy.yml` : connexion SSH au VPS,
`git reset --hard origin/main` puis `docker compose up -d --build`. Le workflow réinstalle
aussi les tâches cron du canari (voir plus bas), pour qu'elles suivent le dépôt.

Il tourne également chaque lundi à 4h. Ce n'est plus pour rafraîchir yt-dlp — le canari
du VPS s'en charge tous les jours à 6h — mais pour la veille des versions sidecar, qui
interroge GitHub et Docker Hub depuis le runner.

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

## Canari (surveillance et réparation automatique)

`scripts/canary.sh`, installé par la CI dans la crontab du VPS :

| Tâche | Fréquence | Rôle |
|---|---|---|
| `canary.sh` | toutes les heures (minute 17) | vérifie que YouTube répond, répare si besoin |
| `canary.sh --rebuild` | tous les jours à 6h | rafraîchit yt-dlp, puis vérifie |

Journal : `journalctl -t yayo-canary`. Diagnostic à la main, sans rien réparer :
`/srv/docker/apps/yayoextractor/scripts/canary.sh --probe`.

En cas d'échec, le canari escalade du remède le moins cher au plus cher et s'arrête
dès que la sonde repasse : rotation du tunnel WARP (jusqu'à 6 fois) → réinscription
WARP → reconstruction de l'image → alerte Discord.

**Pourquoi cette forme.** Panne du 20/09/2026 : l'IP de sortie WARP avait été grillée
par YouTube, qui répondait `LOGIN_REQUIRED` sur *tous* les clients. Deux pièges vérifiés
ce jour-là, qui expliquent chaque détail du script :

- **Mettre à jour yt-dlp ne répare pas cette panne** — la nightly `2026.09.16` échouait
  autant que la stable. Le remède est la rotation d'IP, pas la mise à jour. Ne pas
  confondre avec le 403 du 05/09/2026, dont la cause était l'inverse.
- **Une rotation est une loterie** : la nouvelle IP peut être grillée elle aussi
  (mesuré : 1re rotation OK, 2e retombée sur une IP bloquée). D'où les 6 essais.
- **Une IP grillée ne bloque pas tout** : `dQw4w9WgXcQ` répondait encore quand 7 autres
  vidéos étaient bloquées. Un canari mono-vidéo annoncerait « tout va bien » sur une
  app morte — la sonde exige donc que **deux** vidéos répondent.
- Le conteneur reste `healthy` pendant toute la panne : `/api/health` ne teste que
  FastAPI, jamais YouTube. Rien ne signalait l'incident avant ce canari.

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
