# Onglet Téléphone + Pont Yayo — plan d'implémentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** un 3e onglet « Téléphone » qui copie les photos/vidéos d'un téléphone branché au PC vers une clé USB, sans doublons, grâce à un petit pont local Windows.

**Architecture:** le site (JS statique) parle en HTTP au pont `YayoPont.exe` sur `127.0.0.1:47863`. Le pont (C# .NET Framework 4.8, sans dépendance) lit le téléphone via WPD (COM écrit à la main d'après le SDK Windows 10.0.22621) et la clé via les API fichiers ; la logique pure (règles média, doublons, dates, rangement, sécurité HTTP) est isolée et testée unitairement ; un mode simulation remplace téléphone et clé par des dossiers.

**Tech Stack:** C# (LangVersion latest) sur `net48`, SDK .NET 8 pour builder, xUnit pour les tests ; JS/CSS vanilla côté site ; GitHub Actions `windows-latest` pour publier l'exe.

**Spec:** `docs/superpowers/specs/2026-10-23-transfert-telephone-design.md`

## Global Constraints

- Port fixe `47863`, écoute `127.0.0.1` uniquement ; `Host` ∈ {`127.0.0.1:47863`, `localhost:47863`}.
- Origines : `https://yayoextractor.louis-nectoux.fr`, `http://localhost:8124`, `http://127.0.0.1:8124`.
- Jeton exigé partout sauf `GET /status`, `POST /session` et préflights `OPTIONS`.
- `api: 1` ; le site exige `api >= 1`.
- Aucune suppression sur le téléphone ; écriture seulement sous `<clé>\Photos du téléphone\`.
- Rangement `AAAA\MM - Mois` (mois en français, majuscule initiale).
- Exe WinExe, aucun packer, aucune dépendance NuGet à l'exécution.
- Textes de l'interface en français, tutoiement.

## Fichiers

```
bridge/
  YayoPont.sln
  YayoPont/YayoPont.csproj          WinExe net48, AssemblyVersion = version du pont
  YayoPont/Program.cs               arguments, instance unique, démarrage serveur
  YayoPont/Core/MediaRules.cs       extensions, provenance, dossiers ignorés
  YayoPont/Core/ExifDate.cs         DateTimeOriginal d'un début de JPEG
  YayoPont/Core/Destination.cs      chemin AAAA\MM - Mois, nom libre « (2) »
  YayoPont/Core/DuplicateIndex.cs   index de la clé, verdict « déjà sur la clé »
  YayoPont/Core/Models.cs           MediaItem, PhoneInfo, DriveDto, etc.
  YayoPont/Http/HttpServer.cs       mini serveur HTTP/1.1 sur TcpListener
  YayoPont/Http/Security.cs         contrôle Host/Origin/jeton, en-têtes CORS
  YayoPont/Http/Json.cs             sérialiseur JSON minimal
  YayoPont/Api.cs                   routes
  YayoPont/Jobs/ScanJob.cs          analyse téléphone + index clé
  YayoPont/Jobs/TransferJob.cs      copie avec progression/annulation
  YayoPont/Phone/IPhoneSource.cs    abstraction
  YayoPont/Phone/FolderPhoneSource.cs  simulation
  YayoPont/Phone/WpdInterop.cs      interfaces COM WPD (ordre vtable du SDK)
  YayoPont/Phone/WpdPhoneSource.cs  vraie source WPD (thread dédié)
  YayoPont/Phone/UsbPresence.cs     appareil USB de fabricant de téléphones présent ?
  YayoPont/Drives/IDriveProvider.cs, RealDriveProvider.cs, FakeDriveProvider.cs, Eject.cs
  YayoPont/Setup/Installer.cs       installation, démarrage auto, désinstallation
  YayoPont.Tests/…                  tests xUnit (net48)
.github/workflows/bridge.yml        build + tests + release pont-v<version>
app/static/phone.js, phone.css      onglet Téléphone
app/static/index.html, app.js, style.css   3e onglet
```

## Tâches

### Task 1 : squelette du pont + règles média
- [ ] csproj `net48` WinExe + projet de tests xUnit, `dotnet test` vert sur un test trivial.
- [ ] `MediaRules.Kind(name)` → `photo|video|null` ; `MediaRules.Source(path)` → `screenshots|whatsapp|camera|other` (ordre : captures, WhatsApp, DCIM, autres) ; `MediaRules.SkipFolder(name, parentPath)` (`.` initial, `Android/data`, `Android/obb`, contient `thumbnail`/`cache`/`sticker`).
- [ ] Tests : `IMG_1.JPG`→photo, `a.mp4`→video, `a.txt`→null ; `Phone/DCIM/Camera/x.jpg`→camera ; `…/WhatsApp/Media/WhatsApp Images/x.jpg`→whatsapp ; `DCIM/Screenshots/x.png`→screenshots ; `Pictures/x.jpg`→other ; skip `.thumbnails`, `Android/data`, `WhatsApp Stickers`, garde `Android/media`.

### Task 2 : date EXIF + destination
- [ ] `ExifDate.TryRead(byte[] head, out DateTime)` : JPEG `FFD8`, segment APP1 `Exif\0\0`, TIFF II/MM, IFD0 → pointeur ExifIFD (0x8769) → `DateTimeOriginal` (0x9003), repli `DateTime` (0x0132) d'IFD0.
- [ ] `Destination.Folder(root, date)` → `root\Photos du téléphone\2026\10 - Octobre` ; `Destination.FreeName(folder, name, exists)` → `nom (2).ext`, `(3)`…
- [ ] Tests : JPEG synthétique II et MM avec DateTimeOriginal ; sans EXIF → false ; mois 1..12 ; collisions successives.

### Task 3 : index des doublons
- [ ] `DuplicateIndex.Build(IEnumerable<(string path,long size)>)` ; `IsOnDrive(name, size, Func<byte[]> phoneHead, Func<string,byte[]> driveHead)` : même taille ET (même nom insensible à la casse OU mêmes 64 premiers Ko) ; `Add(path,size)` après chaque copie.
- [ ] Tests : même nom/taille → vrai ; même taille, nom différent, même début → vrai ; même taille, début différent → faux ; taille différente → faux ; la fonction de lecture du téléphone n'est appelée que pour les tailles en collision.

### Task 4 : HTTP + sécurité
- [ ] `HttpServer` (TcpListener, keep-alive, corps par Content-Length, 30 s de délai) ; `Security.Check(req)` → 403 si Host invalide, 403 si Origin présente hors liste, 401 si jeton absent/faux sur route protégée ; `OPTIONS` → 204 avec `Access-Control-Allow-Origin`, `-Methods`, `-Headers: Content-Type, X-Yayo-Token`, `-Private-Network: true`, `Vary: Origin`.
- [ ] `Json` : sérialisation objets/listes/chaînes/nombres/booléens/null + parse d'objet simple (`driveId`, `ids[]`).
- [ ] Tests : Host `evil.com:47863` → 403 ; Origin `https://evil.com` → 403 ; route protégée sans jeton → 401 ; jeton en `?t=` accepté ; préflight correct ; JSON aller-retour avec accents et guillemets.

### Task 5 : lecteurs (clé)
- [ ] `RealDriveProvider.List()` : lecteurs prêts, `Removable`, ou `Fixed` dont le bus est USB (`IOCTL_STORAGE_QUERY_PROPERTY`, BusType 7), hors lecteur système ; `DriveDto{id,letter,label,fs,totalBytes,freeBytes}`.
- [ ] `FakeDriveProvider(dir)` : un lecteur « Clé de test » racine = dossier.
- [ ] `Eject.Request(letter)` : numéro de disque (`IOCTL_STORAGE_GET_DEVICE_NUMBER`) → devinst disque (SetupAPI) → parent → `CM_Request_Device_EjectW` ; repli verbe Shell « eject ».
- [ ] `Open(letter)` : `explorer.exe` sur le dossier de destination (créé si absent).

### Task 6 : sources téléphone
- [ ] `IPhoneSource` : `PhoneInfo Detect()`, `IEnumerable<PhoneFile> Enumerate(Action<int,int> progress, CancellationToken)`, `Stream OpenRead(string id)`, `byte[] Thumbnail(string id)` (null si aucune).
- [ ] `FolderPhoneSource(dir)` + tests (énumération avec règles, ids stables).
- [ ] `WpdInterop` : `IPortableDeviceManager`, `IPortableDevice`, `IPortableDeviceContent`, `IEnumPortableDeviceObjectIDs`, `IPortableDeviceProperties`, `IPortableDeviceResources`, `IPortableDeviceValues` (méthodes 1..26 dans l'ordre du SDK), `IPortableDeviceKeyCollection`, CLSID `PortableDeviceManager 0af10cec…`, `PortableDeviceFTM f7c0039a…`, `PortableDeviceValues 0c15d503…`, `PortableDeviceKeyCollection de2d022d…`, clés WPD (voir spec), `PROPVARIANT` minimal (VT_DATE).
- [ ] `WpdPhoneSource` : thread dédié, ouverture en lecture seule (`GENERIC_READ`), exclut les protocoles `MSC`, stockages = objets fonctionnels catégorie stockage, parcours récursif avec chemins, vignette `WPD_RESOURCE_THUMBNAIL` puis repli GDI+ (JPEG/PNG ≤ 15 Mo), état `locked` si aucun stockage.
- [ ] `UsbPresence.PhoneSeen()` : SetupAPI, VID Samsung 04E8, Google 18D1, Xiaomi 2717, Huawei 12D1, OnePlus 2A70, Oppo/Realme 22D9, Vivo 2D95, Motorola 22B8, LG 1004, Sony 0FCE, HMD 2E04, Apple 05AC (PID 0x1290–0x12AF).
- [ ] Vérif manuelle : iPhone de Louis listé, vignettes, copie d'une photo.

### Task 7 : jobs + routes
- [ ] `ScanJob(phone, drive)` : phase `drive` (index de la clé), phase `phone` (énumération + verdict doublon) → `items`.
- [ ] `TransferJob(phone, drive, items, index)` : contrôle place libre et FAT32 4 Go, puis par fichier : lecture début → date EXIF/téléphone → dossier → doublon/collision → `.part` → taille → renommage → dates ; annulation entre blocs ; erreurs par fichier ; arrêt « téléphone débranché » si la source lève une erreur COM.
- [ ] `Api` : toutes les routes de la spec.
- [ ] Test d'intégration : pont en mode simulation sur port libre, scénario session → devices → scan → transfer → deuxième scan (tout `onDrive`) → fichiers rangés au bon endroit avec la date EXIF.

### Task 8 : installation
- [ ] `Installer` : lancé hors de `%LOCALAPPDATA%\YayoPont` → arrêter l'instance (événement `Local\YayoPont.Quit`), réécrire l'exe (sans flux Zone.Identifier), clés `Run` et `Uninstall` (HKCU), lancer `--background`, ouvrir `https://yayoextractor.louis-nectoux.fr/#telephone`.
- [ ] `--uninstall` : arrêt, suppression des clés, effacement différé du dossier, message de confirmation.
- [ ] Instance unique (`Local\YayoPont`) : un double-clic sur l'exe installé ouvre juste le site.
- [ ] Vérif manuelle sur ce PC : installation, entrée dans « Applications installées », redémarrage de session simulé (lancement depuis la clé Run), désinstallation propre.

### Task 9 : CI
- [ ] `.github/workflows/bridge.yml` : sur push touchant `bridge/**` (et manuel) → `dotnet test` puis `dotnet build -c Release` → si la version n'a pas de release `pont-v<version>`, la crée avec `YayoPont.exe`, marquée latest.

### Task 10 : onglet Téléphone (site)
- [ ] 3e onglet `#telephone` (tabs à 3, pastille généralisée), `phone.js` (client du pont, machine à états : pont → téléphone → clé → galerie → transfert → fin), `phone.css`.
- [ ] Illustrations SVG animées (téléphone + câble, clé USB), détection en direct (sondage 1,5 s), guide d'installation SmartScreen + invite Chrome, états `locked`/`charging-only`.
- [ ] Galerie : sections par mois (`content-visibility:auto`), vignettes `crossorigin` avec jeton, filtres, sélection, barre d'action, aperçu.
- [ ] Transfert : scène plein écran (vignettes qui volent du téléphone à la clé), progression réelle, annulation ; bilan, ouvrir, éjecter.
- [ ] Vérif navigateur avec le pont en mode simulation : parcours complet, mobile/desktop, clair/sombre.

### Task 11 : livraison
- [ ] README (section Téléphone), commit, push (déploie le site), release du pont via CI, test du lien de téléchargement en prod.
