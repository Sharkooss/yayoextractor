# Onglet Téléphone v2 : photos animées, doublons, mode « Ne pas toucher »

Date : 2026-10-24 · Statut : décidé par Claude sur délégation de Louis (absent, « fais comme tu le sens »)
Suite de `2026-10-23-transfert-telephone-design.md`.

## Retours de Yayo après le premier vrai transfert (Galaxy S20 FE)

1. **« Des vidéos coupées à 1 ou 2 secondes »** : ce sont les **photos animées** (Motion Photo
   Samsung, Live Photo iPhone). Selon l'appareil, le clip est soit **dans** le JPEG (une vidéo
   MP4 collée après l'image), soit un **fichier vidéo à part** portant le même nom que la photo
   (`IMG_1234.HEIC` + `IMG_1234.MOV`). Louis ne veut **pas** de live : seulement la photo.
2. **« Des photos et des vidéos en double sur la clé »** : causes retenues
   - WhatsApp garde une copie (recompressée, donc d'octets différents) de tout ce qu'on envoie,
     dans `WhatsApp Images/Sent`, `WhatsApp Video/Sent`… Ces dossiers contiennent un fichier
     `.nomedia` : **la Galerie du téléphone ne les montre pas**, mais le pont 1.0 les copiait.
     Même chose pour `Private` et les autres dossiers `.nomedia`.
   - Fichiers identiques à plusieurs endroits du téléphone (média transféré deux fois dans
     WhatsApp, ancien et nouveau dossier WhatsApp après la migration Android 11…).
   - Clips des photos animées, vus comme des « vidéos en double » de la photo.

**Règle directrice : sur la clé, exactement ce que Yayo voit dans la Galerie de son téléphone,
une seule fois, en photo fixe.**

## Partie 1 — Correctifs (mode normal, celui de Yayo)

### Ce que l'analyse marque (et que le mode normal ignore)

Chaque élément de galerie gagne des drapeaux :

| Champ | Sens | Mode normal |
|---|---|---|
| `hidden` | dans un dossier (ou sous-dossier) contenant `.nomedia`, ou dans un dossier `Sent` d'un chemin WhatsApp | ignoré |
| `live` | vidéo compagnon d'une photo animée : même dossier, même nom sans extension qu'une photo, et (durée ≤ 4 s si lisible, sinon taille ≤ 12 Mo) | ignoré |
| `duplicateOf` | id d'un autre élément du téléphone au contenu identique (même taille + mêmes 64 premiers Ko) ; on garde l'élément de l'appareil photo, sinon celui sans « (n) » dans le nom, sinon le premier | ignoré |
| `motion` | JPEG qui contient une vidéo de photo animée (connu seulement après lecture : analyse approfondie ou copie) | copié **sans** la vidéo |

Le mode normal n'affiche pas ces éléments dans la galerie, ne les compte pas et ne les
sélectionne pas. Une ligne discrète dit combien ont été écartés.

### Photo animée → photo seule

À la copie (et à la réparation de la clé), un JPEG dont la fin contient une vidéo est coupé :
- trouver la fin de l'image principale (marqueurs JPEG jusqu'à SOS, puis données entropiques
  jusqu'au `FFD9` qui n'est pas un octet de bourrage) ;
- vidéo détectée après cette fin : marqueur Samsung `MotionPhoto_Data`, ou boîte MP4 `ftyp`,
  ou XMP Google (`MicroVideo`/`MotionPhoto` + `MicroVideoOffset`/`Item:Length`) ;
- on tronque **au début de la vidéo** (une éventuelle carte de gain HDR placée avant est
  gardée) et on neutralise dans le XMP `MotionPhoto="1"`/`MicroVideo="1"` → `"0"` (même
  longueur, octets remplacés sur place) ;
- le résultat doit rester un JPEG valide (commence par `FFD8`, image principale complète),
  sinon on copie l'original tel quel.

Les photos font quelques Mo : elles sont lues entièrement en mémoire avant écriture.

### Index des copies sur la destination

`<racine>\.yayo-pont.tsv` (attributs caché + système), une ligne par fichier copié ou réparé :
`nom sur le téléphone \t taille sur le téléphone \t chemin relatif sur la destination \t taille écrite`.
À l'analyse, une entrée dont le fichier existe encore avec la taille écrite rend
« déjà sur la clé » tout fichier du téléphone de même nom et même taille d'origine. C'est ce qui
permet de reconnaître une photo animée déjà copiée (et donc raccourcie). Les règles 1.0
(même taille + même nom, ou même taille + mêmes 64 premiers Ko) restent en plus.

### Vidéos incomplètes

Un MP4/MOV/M4V/3GP est **complet** si ses boîtes de premier niveau couvrent exactement le fichier
(tailles 32 et 64 bits, taille 0 = jusqu'à la fin) et s'il contient `moov`. Après chaque copie
de vidéo : contrôle, et échec du fichier s'il est incomplet. Un fichier incomplet sur la
destination n'entre pas dans l'index des doublons : l'élément du téléphone redevient « à copier ».

### « Ranger la clé » au début de chaque copie

Avant de copier (mode normal : toujours ; mode avancé : option), dans le **dossier racine
géré** uniquement (`Photos du téléphone` ou la racine choisie), jamais ailleurs :
1. supprimer les `*.part` orphelins ;
2. **doublons exacts** : même taille puis même SHA-256 complet → on garde un seul fichier (nom
   sans « (n) », puis chemin le plus court, puis le plus ancien), les autres sont supprimés ;
3. **photos animées** : JPEG contenant une vidéo → raccourci sur place (`.part` puis
   remplacement), entrée ajoutée à l'index des copies avec le nom et la taille d'origine ;
4. **clips de photos animées** : vidéo de même nom (sans extension) qu'une photo du même dossier
   et de durée ≤ 4 s (lue dans `mvhd`) → supprimée ;
5. **vidéos incomplètes** dont le téléphone a un fichier de même nom dans la copie en cours →
   supprimées (la bonne version est recopiée juste après) ;
6. dossiers devenus vides → supprimés.

Les copies envoyées par WhatsApp (dossiers `Sent`) déjà présentes sur la clé **ne sont pas**
supprimées : rien ne prouve que l'original est sur la clé (risque de perte). Elles ne sont
simplement plus recopiées.

Bilan renvoyé par la copie : `repair: {duplicates, motionStripped, liveRemoved, brokenRemoved,
partsRemoved, bytesFreed}`. Le site l'affiche en une phrase dans le bilan final.

## Partie 2 — Mode « Ne pas toucher » (mode avancé, pour Louis)

Interrupteur fixe en bas à gauche de l'onglet Téléphone, libellé **« Ne pas toucher »**
(discret, pour que Yayo n'y touche pas). Activé, l'onglet passe en **mode avancé** ; l'état est
gardé dans `localStorage`. Rien de ce mode n'apparaît quand l'interrupteur est éteint.

### Fonctions

- **Choix du téléphone** parmi tous les appareils WPD branchés (et pas seulement le premier).
- **Destination libre** : une clé USB, un disque, ou **n'importe quel dossier du PC** (sélecteur
  de dossier Windows ouvert par le pont, ou chemin tapé à la main ; destinations récentes
  mémorisées). Nom du dossier racine réglable (`Photos du téléphone` par défaut, vide = écrire
  directement dans la destination).
- **Rangement par modèle** de chemin, avec préréglages et modèle libre. Jetons :
  `{annee}` `{mois}` (01–12) `{mois_nom}` (Octobre) `{jour}` `{date}` (2026-10-24) `{heure}` (14h30)
  `{source}` (Appareil photo, WhatsApp, Captures d'écran, Autres) `{type}` (Photos, Vidéos)
  `{lieu}` `{ville}` `{departement}` `{region}` `{pays}` `{nom}` (nom d'origine sans extension).
  Préréglages : Année/Mois (défaut, identique au mode normal), Année, Année/Mois/Jour, Tout dans
  un dossier, Lieu/Année, Année/Lieu, Pays/Ville, Source/Année, Type/Année/Mois.
  `/` sépare les dossiers. Valeur manquante : « Date inconnue », « Lieu inconnu »… Chaque segment
  est nettoyé (caractères interdits, points/espaces finaux, noms réservés, 80 caractères max).
- **Renommage** optionnel par modèle de nom (mêmes jetons, l'extension est toujours gardée).
  Par défaut `{nom}` (nom d'origine).
- **Tri par lieu** : analyse approfondie qui lit l'EXIF (date de prise de vue + GPS) des photos ;
  géocodage inverse **hors ligne** par le serveur du site (`POST /api/geo/reverse`, GeoNames).
  Niveau au choix : ville, département, région, pays. **Lieux fréquents** : un lieu avec moins de
  N fichiers (réglable, défaut 10) remonte au niveau supérieur (ville → département → région →
  pays). Les éléments sans GPS (vidéos, captures) héritent du lieu de la photo la plus proche
  dans le temps à moins de X heures (réglable, défaut 3 h), sinon « Lieu inconnu ».
- **Filtres** : sources, photos/vidéos, période (du… au…), inclure les dossiers masqués
  (`.nomedia`/`Sent`), inclure les clips de photos animées, inclure les doublons du téléphone.
- **Options de copie** : retirer la vidéo des photos animées (défaut oui), ranger la destination
  avant de copier (défaut oui), ignorer ce qui est déjà sur la destination (défaut oui), conflit
  de nom (renommer « (2) » / ignorer / remplacer), vérifier après copie (relecture et SHA-256),
  garder les dates d'origine sur les fichiers (défaut oui).
- **Aperçu du rangement** avant copie : arborescence avec nombre de fichiers et taille par dossier.
- **Statistiques** de l'analyse (par année, source, type, lieu) et **export** du bilan de copie
  en CSV.
- Réglages mémorisés dans `localStorage`.

## API du pont v2 (pont 1.1.0, `api: 2`)

Compatibilité : les routes 1.0 gardent leur forme ; tout est additif. Le site exige `api ≥ 2`
et `MIN_BRIDGE = "1.1.0"` (le 1.0.1 fabrique des doublons).

| Route | Changement |
|---|---|
| `GET /status` | `+ features: ["phones","folders","layout","deep","repair","update","pick-folder"]`, `+ updating: bool` |
| `GET /devices[?all=1]` | `+ phones: [{id, name, manufacturer, state}]`, `phone` = téléphone choisi (ou le premier). `?all=1` : `drives` contient aussi les disques fixes non système, chaque disque a `kind: "usb"|"fixed"` |
| `POST /scan` | corps : `{driveId}` **ou** `{folder}` ; `+ phoneId?`, `+ options: {deep?, rootName?}`. Phase `details` pendant l'analyse approfondie avec `detailsDone`/`detailsTotal`. Éléments : `+ path, hidden, live, duplicateOf, motion?, exifDate?, lat?, lon?`. Compteurs `+ hidden, live, duplicates` |
| `POST /plan` | même corps que `/transfer` → `{folders: [{path, count, bytes}], toCopy, skipped, bytes}` (aperçu, rien n'est écrit) |
| `POST /transfer` | corps : `{driveId}` ou `{folder}`, `ids[]`, `+ options` (ci-dessous). État : `+ repair{…}`, `+ phase: "repair"|"copy"`, `+ copiedFiles: [{id, name, path}]` (chemin relatif à la destination) |
| `POST /open` | `{driveId}` ou `{folder}` (+ `rootName?`) |
| `POST /pick-folder` | ouvre le sélecteur de dossier Windows au premier plan → `{path}` ou `{cancelled: true}` (le site attend jusqu'à 10 min) |
| `POST /update` | télécharge la dernière version publiée et se remplace en silence → `{state: "started"|"uptodate"|"busy"|"error", version?}` |

Options de copie (toutes facultatives ; absentes = comportement du mode normal) :

```json
{
  "rootName": "Photos du téléphone",
  "layout": "{annee}/{mois} - {mois_nom}",
  "fileName": "{nom}",
  "places": { "<id>": { "lieu": "Lyon", "ville": "Lyon", "departement": "Rhône", "region": "Auvergne-Rhône-Alpes", "pays": "France" } },
  "stripMotion": true,
  "repair": true,
  "dedupe": true,
  "conflict": "rename",
  "verify": false,
  "keepDates": true
}
```

Date utilisée pour le rangement : EXIF de prise de vue (JPEG, et HEIC si lisible), sinon date
du téléphone. Les chemins produits par les modèles restent toujours **sous** la racine de la
destination (aucun `..`, aucun chemin absolu).

Sécurité inchangée (127.0.0.1, `Host`, origines, jeton). L'écriture n'est plus limitée à
`Photos du téléphone` d'une clé : elle est limitée à la destination choisie dans la requête,
qui doit être un chemin local absolu sur une lettre de lecteur (pas de chemin réseau), hors
du dossier Windows et des dossiers Program Files.

### Mise à jour automatique du pont (1.1.0+)

Au démarrage puis toutes les 12 h, et sur `POST /update` : le pont lit
`https://api.github.com/repos/Sharkooss/yayoextractor/releases/latest` (tag `pont-vX.Y.Z`),
et si la version est plus récente et qu'aucune analyse ni copie ne tourne, il télécharge
`YayoPont.exe` dans `%LOCALAPPDATA%\YayoPont\maj\`, vérifie (en-tête `MZ`, taille > 50 Ko,
`FileVersionInfo` = version annoncée) puis lance `YayoPont.exe --maj` : l'installation
habituelle (arrêt de l'ancienne instance, réécriture, redémarrage) **sans ouvrir le site**.
Téléchargé par le pont lui-même, le fichier n'a pas la marque « venant d'internet » : pas de
SmartScreen. Le 1.0.1 de Yayo devra être mis à jour une dernière fois à la main (écran
« Mise à jour nécessaire » existant) ; ensuite tout est automatique.

## API du serveur : géocodage inverse

`POST /api/geo/reverse` `{points: [[lat, lon], …]}` (5 000 points max) →
`{places: [{ville, departement, region, pays, codePays} | null, …]}` (même ordre).

- Données GeoNames (`cities500.zip`, `admin1CodesASCII.txt`, `admin2Codes.txt`) téléchargées
  au premier besoin dans `/data/geonames/` (le volume persiste entre déploiements), chargées
  une fois en mémoire avec un index en grille. Noms de pays en français (table intégrée).
- Ville = lieu habité le plus proche à moins de 30 km ; pays/région/département tirés de ce lieu
  (pays jusqu'à 100 km). Départements nettoyés du préfixe « Département de/du/de la/des/d' ».
- Données indisponibles → `503` avec un message clair. Les points ne sont ni conservés ni loggés.

## Livraison

1. Pont 1.1.0 (`<Version>` bumpée) poussé en premier : la release `pont-v1.1.0` doit exister
   avant que le site ne l'exige.
2. Site + serveur ensuite (déploiement automatique sur `main`).

## Révision 1.1.1 — « 100 % automatique », toutes marques (24/10/2026, demande de Louis)

Louis : « il n'a plus qu'à prendre le nouveau pont, brancher son téléphone et la clé, et ça
réimporte tout correctement », photos animées de **toutes** les marques (iPhone compris).

1. **Ranger la clé dès l'analyse.** Chez Yayo tout est déjà sur la clé (copié par le 1.0.1) :
   le bouton de copie serait grisé et le rangement, lancé seulement à la copie, ne tournerait
   jamais. Nouvel ordre de l'analyse : téléphone (`phone`) → rangement (`repair`, avec la liste du
   téléphone) → index de la clé (`drive`) → comparaison (`compare`) → détails (`details`).
   L'état de l'analyse porte `repair{…}` (même forme que pour la copie). Option `options.repair`
   de `/scan` (défaut vrai). Le rangement reste aussi au début de la copie (quasi gratuit).
2. **Ordre du rangement** : photos animées raccourcies **avant** les doublons exacts (deux
   copies d'une même photo animée, l'une déjà raccourcie, deviennent identiques).
3. **Photos animées de toutes les marques**
   - JPEG : Samsung (remorque SEF, tous les enregistrements `MotionPhoto*`), Google (`MVIMG`,
     `.MP.jpg`, XMP `MicroVideo`/`MotionPhoto`/`Container`), Xiaomi, Oppo/OnePlus/Realme, Vivo,
     Honor/Huawei (MP4 collé après l'image, avec ou sans en-tête maison). Détection rapide sur la
     clé : un JPEG qui ne finit pas par `FFD9` (fin d'image) et n'est pas une simple remorque
     Samsung sans photo animée est lu en entier et raccourci s'il contient une vidéo.
   - Coupe : au début de la vidéo s'il reste une image secondaire (carte de gain HDR, MPF)
     entre la fin de l'image principale et la vidéo, sinon **à la fin de l'image principale**.
   - **HEIC/HEIF** : vidéo dans une boîte `mpvd` (Google) ou remorque SEF Samsung après les
     boîtes HEIF → coupe après la dernière boîte HEIF, seulement si toutes les données
     référencées par `iloc` restent avant la coupe ; XMP neutralisé.
   - **Clips à part** (iPhone `IMG_1234.HEIC` + `IMG_1234.MOV`, `IMG_E…` retouchées) : même nom
     qu'une photo du même dossier (téléphone) ; sur la clé, même nom qu'une photo du même dossier
     **ou** n'importe où dans la racine gérée avec moins de 2 min d'écart de date, durée ≤ 4 s.
     Un clip iPhone se reconnaît aussi à ses métadonnées QuickTime
     (`com.apple.quicktime.content.identifier` / clés `live-photo`) ; il est retiré de la clé si
     sa photo est sur la clé ou sur le téléphone.
4. **Copies WhatsApp « Sent » déjà sur la clé** : retirées **seulement** si leur original est
   sur la clé. Fichier de la clé de même nom et taille qu'un élément `hidden` d'un dossier `Sent`
   du téléphone (et d'aucun élément visible). Photo : empreinte visuelle (dHash + confirmation
   fine, orientation EXIF appliquée, vignette EXIF quand son cadrage correspond) d'une photo de la
   clé datée avant l'envoi. Vidéo : même durée à 0,15 s près, même format d'image, original daté
   avant l'envoi (30 jours max) et plus lourd. Sans original trouvé : on garde.
5. Pont **1.1.1**, le site exige `MIN_BRIDGE = "1.1.1"` (le 1.1.0 se met à jour tout seul).
