# Onglet « Téléphone » : photos du téléphone vers une clé USB

Date : 2026-10-23 · Statut : validé par Louis

## Besoin

Yayo (grand-père de Louis, peu à l'aise avec un PC) veut copier les photos et vidéos de son
Samsung Galaxy S20 FE sur une clé USB. Il branche le téléphone et la clé sur son PC Windows,
voit ses photos, en choisit ou prend tout, et la copie ne recrée pas ce qui est déjà sur la clé.

## Contrainte qui fixe l'architecture

Un site web ne peut pas lire un téléphone branché en USB sous Windows : le téléphone y est
un appareil MTP tenu par le pilote WPD de Windows, et Chrome refuse cette classe d'interfaces
à WebUSB. Il ne peut pas non plus détecter le branchement d'une clé. Il faut donc un
programme local. Décision : **le site fait tout (interface, logique d'affichage), un « pont »
minuscule installé une fois sur le PC fait l'accès matériel.**

## Architecture

```
yayoextractor.louis-nectoux.fr  (onglet Téléphone, JS)
        │  fetch http://127.0.0.1:47863  (CORS + jeton de session)
        ▼
Pont Yayo  (YayoPont.exe, C# .NET Framework 4.8, invisible, démarre avec Windows)
        ├─ téléphone : API Windows Portable Devices (WPD, COM) — aucun réglage sur le téléphone
        └─ clé USB : lecteurs amovibles / disques USB (API fichiers Windows)
```

- **.NET Framework 4.8** est intégré à Windows 10 (1903+) et 11 : rien à installer, un seul
  exe d'environ 150 Ko, ~20 Mo de RAM, CPU quasi nul au repos.
- Pas de console (WinExe), pas de packer : réduit les faux positifs Defender.

## Installation (par Yayo lui-même)

1. L'onglet ne trouve pas le pont → gros bouton « Installer le pont » + guide illustré :
   télécharger → SmartScreen « Informations complémentaires » → « Exécuter quand même ».
2. L'exe s'installe sans droits admin :
   - se recopie dans `%LOCALAPPDATA%\YayoPont\YayoPont.exe` (fichier réécrit, donc sans
     marque « téléchargé d'internet » : pas de nouvelle alerte au démarrage de Windows) ;
   - démarrage automatique : `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` ;
   - apparaît dans « Applications installées » (`HKCU\...\Uninstall`, `--uninstall`) ;
   - lance la copie installée en arrière-plan puis ouvre `…/#telephone`.
3. L'onglet détecte le pont en 2 s. Chrome demande une fois l'accès aux appareils locaux.
4. Relancer l'exe (nouvelle version ou double-clic) : remplace l'instance en cours
   (événement nommé `Local\YayoPont.Quit`), une seule instance (mutex `Local\YayoPont`).

Limites assumées : l'alerte SmartScreen à l'installation (exe non signé, choix du
téléchargement direct plutôt que du Microsoft Store) et l'invite de Chrome, chacune une fois.

## Parcours dans l'onglet

Style chaleureux du site, animation à chaque étape, tutoiement, gros boutons.

1. **Pont** (seulement s'il manque) : installation guidée, détection automatique.
2. **Téléphone** : illustration animée du câble. États :
   - `ready` : téléphone vu avec son stockage → étape suivante ;
   - `locked` : téléphone vu mais stockage vide → « Déverrouille ton téléphone et touche
     *Autoriser* » ;
   - `charging-only` : appareil USB Android/Apple vu sans WPD → « Touche la notification
     *Recharge via USB* et choisis *Transfert de fichiers* ».
3. **Clé USB** : détection au branchement, nom + espace libre, choix s'il y en a plusieurs.
4. **Galerie** : animation d'analyse avec compteurs, puis grille par mois avec vraies
   vignettes. Filtres Appareil photo / WhatsApp / Captures / Autres et Photos / Vidéos.
   Badge « déjà sur la clé ». Sélection par photo, par mois, ou « Tout sélectionner (nouvelles) ».
   Barre d'action collante « Copier N photos (X Go) » + jauge d'espace de la clé.
   Clic sur une photo : aperçu en grand.
5. **Transfert** : scène plein écran dans l'esprit YAYO//LAB, photos qui volent du téléphone
   vers la clé, vraie progression (nombre, octets, temps restant), « Annuler ».
6. **Fin** : bilan (copiées / déjà présentes / échecs) + « Ouvrir la clé » +
   « Retirer la clé en toute sécurité » (éjection par le pont).

## Ce qui est lu sur le téléphone

Parcours récursif de chaque stockage, extensions photo (`jpg jpeg png heic heif webp gif dng`)
et vidéo (`mp4 mov 3gp mkv webm avi m4v`). Dossiers ignorés : noms commençant par `.`,
`Android/data`, `Android/obb`, et tout dossier contenant `thumbnail`, `cache` ou `sticker`
(insensible à la casse). Profondeur max 10.

Provenance (filtres), d'après le chemin :
`Screenshots`/`Captures` → captures ; `WhatsApp` → whatsapp ; `DCIM` → appareil photo ;
sinon → autres.

## Doublons et rangement

- **Index de la clé** : tous les fichiers média de la clé entière (hors `System Volume
  Information` et `$RECYCLE.BIN`), par taille.
- **Déjà sur la clé** si un fichier de la clé a la même taille **et** soit le même nom
  (insensible à la casse), soit les mêmes 64 premiers Ko (copie renommée). La comparaison de
  contenu ne lit que les candidats de même taille.
- **Destination** : `<clé>:\Photos du téléphone\AAAA\MM - Mois\nom`. Date : EXIF
  `DateTimeOriginal` des JPEG (lue dans le début du flux pendant la copie), sinon date du
  téléphone (création, puis modification).
- **Copie seule** : rien n'est jamais supprimé du téléphone. Écriture en `.part`, taille
  vérifiée, renommage, date d'origine posée sur le fichier. Même nom mais contenu différent
  dans le dossier cible → `nom (2).ext`.
- Avant de démarrer : espace libre vérifié ; sur une clé FAT32, les fichiers > 4 Go sont
  signalés et exclus.
- Interruption (câble débranché, annulation) : relancer reprend naturellement grâce aux
  doublons.

## API du pont

Base `http://127.0.0.1:47863`. Réponses JSON (sauf vignettes et médias).

| Route | Jeton | Rôle |
|---|---|---|
| `GET /status` | non | `{app:"yayo-pont", version, api:1}` |
| `POST /session` | non (origine vérifiée) | `{token}` |
| `GET /devices` | oui | `{phone, usbPhoneSeen, drives[]}` |
| `POST /scan` `{driveId}` | oui | lance l'analyse → `{jobId}` |
| `GET /scan/{job}` | oui | `{state, phase, photos, videos, items?}` |
| `GET /thumb/{id}` | oui | vignette JPEG (WPD, sinon réduction GDI+, sinon 404) |
| `GET /media/{id}` | oui | fichier complet (aperçu) |
| `POST /transfer` `{driveId, ids[]}` | oui | → `{jobId}` |
| `GET /transfer/{job}` | oui | `{state, done, total, bytesDone, bytesTotal, current, copied, skipped, failed[]}` |
| `POST /transfer/{job}/cancel` | oui | annule |
| `POST /open` `{driveId}` | oui | ouvre l'Explorateur sur le dossier de destination |
| `POST /eject` `{driveId}` | oui | éjecte la clé |

`phone` : `null` ou `{name, manufacturer, state: "ready"|"locked"}` ; `usbPhoneSeen` : un
appareil USB d'un fabricant de téléphones est branché (sert à l'état `charging-only`).
Élément de galerie : `{id, name, kind:"photo"|"video", size, date, source, onDrive}`.

## Sécurité

- Écoute sur `127.0.0.1` uniquement ; en-tête `Host` vérifié (anti DNS-rebinding).
- Origines acceptées : `https://yayoextractor.louis-nectoux.fr`, `http://localhost:8124`,
  `http://127.0.0.1:8124`. Préflight CORS avec `Access-Control-Allow-Private-Network: true`.
- Jeton aléatoire obtenu par `POST /session` (réponse lisible seulement par une origine
  acceptée), exigé partout ailleurs (en-tête `X-Yayo-Token` ou `?t=` pour les `<img>`).
- Lecture seule sur le téléphone ; écriture uniquement sous `Photos du téléphone` de la clé
  choisie.

## Livraison

- Projet `bridge/` (SDK .NET, cible `net48`, C# récent, aucune dépendance NuGet à l'exécution).
- Workflow GitHub Actions `bridge.yml` (windows-latest) : tests + build Release, publication
  en Release GitHub `pont-v<version>` avec l'asset `YayoPont.exe`. Le site télécharge
  `releases/latest/download/YayoPont.exe`.
- Le site compare `api` du pont à celle qu'il attend et propose la mise à jour si besoin.

## Tests

- **Unitaires (pont)** : doublons, date EXIF, chemins de rangement, classification de
  provenance, filtres de dossiers, contrôle d'origine/hôte/jeton.
- **Mode simulation** : `YayoPont.exe --faux-telephone <dossier> --fausse-cle <dossier>`
  remplace WPD et la clé par des dossiers → parcours complet du site testable sans matériel.
- **WPD réel** : avec l'iPhone de Louis (détection, vignettes, copie).
- **Samsung** (état `charging-only`, chemins WhatsApp Android 11+) : validé chez Yayo.
