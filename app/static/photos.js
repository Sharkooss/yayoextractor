"use strict";

/* Onglet Photos : choix des fichiers, envoi au serveur, et YAYO//LAB, le
   spectacle qui accompagne la réparation. Le spectacle ne ment pas : octets,
   marqueurs JPEG, passes et compteurs affichés viennent du vrai fichier. */

(() => {
  const $ = (id) => document.getElementById(id);

  const MAX_PHOTOS = 60; // mêmes limites que le serveur
  const MAX_BYTES = 60 * 1024 * 1024;
  const UPLOAD_CONCURRENCY = 3;
  const IMAGE_NAME = /\.(jpe?g|jfif|jpe|png|heic|heif|webp|gif|bmp|tiff?|avif|mpo)$/i;
  const REDUCED = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const SPEED = REDUCED ? 0.3 : 1; // facteur appliqué à toutes les durées du spectacle
  const RING_LENGTH = 1589.6; // 2 * PI * 253, en unités du viewBox
  const GLYPHS = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789#%&@/<>=+";

  // Libellés courts du diagnostic, par code d'anomalie renvoyé par le serveur.
  const ISSUE_SHORT = {
    progressive: ["Encodage", "Progressif"],
    format: ["Format", "Non standard"],
    mpo: ["Structure", "Multi-images"],
    truncated: ["Intégrité", "Fichier tronqué"],
    orientation: ["Orientation", "Rotation cachée"],
    cmyk: ["Couleurs", "CMJN"],
    alpha: ["Transparence", "Présente"],
    depth: ["Profondeur", "16 bits"],
    grayscale: ["Canaux", "Niveaux de gris"],
    color_profile: ["Profil couleur", "Exotique"],
    huge: ["Résolution", "Géante"],
  };

  // Marqueurs JPEG commentés dans le flux binaire.
  const MARKERS = {
    0xd8: ["SOI", "début de l'image"],
    0xe0: ["APP0", "en-tête JFIF"],
    0xe1: ["APP1", "métadonnées EXIF"],
    0xe2: ["APP2", "profil couleur ICC"],
    0xed: ["APP13", "métadonnées Photoshop"],
    0xee: ["APP14", "en-tête Adobe"],
    0xdb: ["DQT", "tables de quantification"],
    0xc4: ["DHT", "tables de Huffman"],
    0xc0: ["SOF0", "encodage standard"],
    0xc1: ["SOF1", "encodage étendu"],
    0xc2: ["SOF2", "encodage progressif"],
    0xdd: ["DRI", "intervalle de reprise"],
    0xda: ["SOS", "début des données"],
    0xfe: ["COM", "commentaire"],
  };
  const PROGRESSIVE_SOF = [0xc2, 0xc6, 0xca, 0xce];

  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const easeOut = (p) => 1 - Math.pow(1 - p, 3);
  const easeInOut = (p) => -(Math.cos(Math.PI * p) - 1) / 2;

  function formatBytes(n) {
    if (n < 1024 * 1024) return `${Math.max(1, Math.round(n / 1024))} Ko`;
    return `${(n / 1048576).toFixed(1).replace(".", ",")} Mo`;
  }

  function plural(n, word) {
    return `${n} ${word}${n > 1 ? "s" : ""}`;
  }

  /* ======================================================================
     Choix des photos
     ====================================================================== */

  const input = $("photo-input");
  const dropzone = $("dropzone");
  let selection = []; // { id, file, url, tile }
  let nextId = 1;

  function isImage(file) {
    return file.type.startsWith("image/") || IMAGE_NAME.test(file.name);
  }

  function showError(id, message) {
    $(id).textContent = message || "";
    $(id).hidden = !message;
  }

  function extensionOf(name) {
    const match = /\.([a-z0-9]+)$/i.exec(name);
    return match ? match[1].toUpperCase() : "?";
  }

  // Un fichier glissé depuis la liste des téléchargements du navigateur après
  // avoir été déplacé n'existe plus là où le navigateur le cherche : sans ce
  // test, il passait toute l'animation pour finir en « connexion coupée ».
  async function isReadable(file) {
    try {
      await file.slice(0, 16).arrayBuffer();
      return true;
    } catch {
      return false;
    }
  }

  const MISSING_FILE = (name) =>
    `« ${name} » est introuvable sur ton ordinateur : elle a sans doute été déplacée. ` +
    "Ajoute-la depuis le dossier où elle se trouve maintenant.";

  async function addFiles(fileList) {
    const files = Array.from(fileList); // la liste du glisser-déposer expire après l'événement
    const readable = await Promise.all(files.map(isReadable));
    const problems = [];
    for (const [k, file] of files.entries()) {
      if (!readable[k]) {
        problems.push(MISSING_FILE(file.name));
      } else if (!isImage(file)) {
        problems.push(`« ${file.name} » n'est pas une photo.`);
      } else if (file.size > MAX_BYTES) {
        problems.push(`« ${file.name} » est trop lourde (60 Mo maximum).`);
      } else if (selection.length >= MAX_PHOTOS) {
        problems.push(`${MAX_PHOTOS} photos maximum à la fois : les suivantes n'ont pas été ajoutées.`);
        break;
      } else {
        const entry = { id: nextId++, file, url: URL.createObjectURL(file) };
        entry.tile = makeTile(entry);
        $("photo-grid").appendChild(entry.tile);
        selection.push(entry);
      }
    }
    const extra = problems.length > 1 ? ` (et ${plural(problems.length - 1, "autre")})` : "";
    showError("drop-error", problems.length ? problems[0] + extra : "");
    if (selection.length) showError("repair-error", "");
    updateTray();
  }

  function makeTile(entry) {
    const tile = document.createElement("div");
    tile.className = "photo-tile";
    tile.innerHTML = `
      <img alt="">
      <span class="tile-format"><b></b><span></span></span>
      <button type="button" class="tile-remove">
        <svg class="icon" aria-hidden="true"><use href="#i-x"/></svg>
      </button>`;
    const img = tile.querySelector("img");
    // Chrome et Firefox n'affichent pas les HEIC : on montre le format à la place.
    img.addEventListener("error", () => tile.classList.add("no-preview"));
    img.alt = entry.file.name;
    img.src = entry.url;
    tile.querySelector(".tile-format b").textContent = extensionOf(entry.file.name);
    tile.querySelector(".tile-format span").textContent = entry.file.name;
    const remove = tile.querySelector(".tile-remove");
    remove.setAttribute("aria-label", `Retirer ${entry.file.name}`);
    remove.addEventListener("click", () => removePhoto(entry.id));
    return tile;
  }

  function removePhoto(id) {
    const index = selection.findIndex((entry) => entry.id === id);
    if (index < 0) return;
    const [entry] = selection.splice(index, 1);
    URL.revokeObjectURL(entry.url);
    entry.tile.remove();
    updateTray();
  }

  function clearSelection() {
    for (const entry of selection) URL.revokeObjectURL(entry.url);
    selection = [];
    $("photo-grid").innerHTML = "";
    showError("drop-error", "");
    updateTray();
  }

  function updateTray() {
    const n = selection.length;
    $("tray").hidden = n === 0;
    dropzone.classList.toggle("compact", n > 0);
    $("tray-count").textContent = n === 1 ? "1 photo prête" : `${n} photos prêtes`;
    $("drop-title").textContent = n ? "Ajouter d'autres photos" : "Glisse tes photos ici";
    $("drop-sub").textContent = n
      ? "Glisse-les ici, ou appuie sur le bouton"
      : "ou appuie sur le bouton pour les choisir dans ton ordinateur";
    $("drop-btn-label").textContent = n ? "Ajouter" : "Choisir mes photos";
  }

  dropzone.addEventListener("click", () => input.click());
  dropzone.addEventListener("keydown", (event) => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      input.click();
    }
  });
  input.addEventListener("change", () => {
    const files = Array.from(input.files);
    input.value = "";
    addFiles(files);
  });
  $("tray-clear").addEventListener("click", clearSelection);

  // Glisser-déposer n'importe où sur la page : des photos lâchées sur l'onglet
  // Musique ouvrent l'onglet Photos au lieu d'afficher l'image dans le navigateur.
  const carriesFiles = (event) => Array.from(event.dataTransfer?.types || []).includes("Files");
  let dragDepth = 0;

  window.addEventListener("dragenter", (event) => {
    if (!carriesFiles(event)) return;
    event.preventDefault();
    if (!$("lab").hidden) return;
    dragDepth++;
    dropzone.classList.add("dragover");
  });
  window.addEventListener("dragover", (event) => {
    if (!carriesFiles(event)) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = $("lab").hidden ? "copy" : "none";
  });
  window.addEventListener("dragleave", () => {
    dragDepth = Math.max(0, dragDepth - 1);
    if (!dragDepth) dropzone.classList.remove("dragover");
  });
  window.addEventListener("drop", (event) => {
    if (!carriesFiles(event)) return;
    event.preventDefault();
    dragDepth = 0;
    dropzone.classList.remove("dragover");
    if (!$("lab").hidden) return;
    if (typeof showTab === "function") showTab("photos");
    addFiles(event.dataTransfer.files);
  });

  $("repair-btn").addEventListener("click", () => {
    if (!selection.length) {
      showError("repair-error", "Choisis d'abord une ou plusieurs photos, juste à côté.");
      dropzone.focus();
      return;
    }
    showError("repair-error", "");
    startRun(selection.slice());
  });

  /* ======================================================================
     Lecture du fichier : ce que le flux binaire et le diagnostic affichent
     ====================================================================== */

  async function readBytes(file) {
    // Tout le fichier pour compter les passes, sauf s'il est énorme.
    const limit = file.size <= 40 * 1024 * 1024 ? file.size : 4 * 1024 * 1024;
    return new Uint8Array(await file.slice(0, limit).arrayBuffer());
  }

  function sniff(bytes) {
    const ascii = (a, b) => String.fromCharCode(...bytes.subarray(a, b));
    const info = {
      kind: "?", label: "Inconnu", known: false, markers: [],
      progressive: false, scans: 0, width: 0, height: 0, sosOff: null, signature: [0, 4],
    };
    const set = (kind, signature) => Object.assign(info, { kind, label: kind, known: true, signature });
    if (bytes[0] === 0xff && bytes[1] === 0xd8) {
      set("JPEG", [0, 2]);
      parseJpeg(bytes, info);
    } else if (bytes[0] === 0x89 && ascii(1, 4) === "PNG") {
      set("PNG", [0, 8]);
    } else if (ascii(4, 8) === "ftyp") {
      set(/avi[fs]/.test(ascii(8, 12)) ? "AVIF" : "HEIC", [4, 12]);
    } else if (ascii(0, 4) === "RIFF" && ascii(8, 12) === "WEBP") {
      set("WEBP", [8, 12]);
    } else if (ascii(0, 3) === "GIF") {
      set("GIF", [0, 6]);
    } else if (ascii(0, 2) === "BM") {
      set("BMP", [0, 2]);
    } else if (ascii(0, 4) === "II*\0" || ascii(0, 4) === "MM\0*") {
      set("TIFF", [0, 4]);
    }
    return info;
  }

  function parseJpeg(b, info) {
    info.markers.push({ off: 0, code: 0xd8 });
    let i = 2;
    while (i + 4 <= b.length) {
      if (b[i] !== 0xff) { i++; continue; }
      const code = b[i + 1];
      if (code === 0xff || code === 0x00) { i++; continue; }
      if (code === 0x01 || (code >= 0xd0 && code <= 0xd7)) { i += 2; continue; }
      info.markers.push({ off: i, code });
      if (code >= 0xc0 && code <= 0xcf && code !== 0xc4 && code !== 0xc8 && code !== 0xcc) {
        info.progressive = PROGRESSIVE_SOF.includes(code);
        info.height = (b[i + 5] << 8) | b[i + 6];
        info.width = (b[i + 7] << 8) | b[i + 8];
      }
      if (code === 0xda) { info.sosOff = i; break; }
      i += 2 + ((b[i + 2] << 8) | b[i + 3]);
    }
    // Dans les données compressées, 0xFF n'est suivi de 0xDA que pour un vrai
    // marqueur : chaque occurrence est une passe de balayage de plus.
    if (info.sosOff === null) return;
    info.scans = 1;
    for (let k = info.sosOff + 2; k < b.length - 1; k++) {
      if (b[k] === 0xff && b[k + 1] === 0xda) info.scans++;
    }
  }

  function jpegBlocks(w, h) {
    // Luminance en 8×8 plus deux plans de chrominance sous-échantillonnés (4:2:0).
    return Math.ceil(w / 8) * Math.ceil(h / 8) + 2 * Math.ceil(w / 16) * Math.ceil(h / 16);
  }

  function loadImage(src) {
    return new Promise((resolve, reject) => {
      const img = new Image();
      img.decoding = "async";
      img.onload = () => resolve(img);
      img.onerror = reject;
      img.src = src;
    });
  }

  /* ======================================================================
     Réseau : un lot, puis une requête par photo (en parallèle limité)
     ====================================================================== */

  function deferred() {
    let resolve;
    const promise = new Promise((r) => { resolve = r; });
    return { promise, resolve };
  }

  function uploadOne(r, i) {
    return new Promise((resolve) => {
      const xhr = new XMLHttpRequest();
      xhr.open("POST", `/api/photos/${r.batchId}`);
      xhr.responseType = "json";
      xhr.upload.addEventListener("progress", (event) => {
        if (event.lengthComputable) r.progress[i] = event.loaded / event.total;
      });
      xhr.addEventListener("load", () => {
        const data = xhr.response || {};
        if (xhr.status === 200) resolve(data);
        else resolve({ ok: false, error: data.detail || "Le serveur a refusé cette photo.", retry: xhr.status >= 500 });
      });
      xhr.addEventListener("error", () => {
        resolve({ ok: false, error: "La connexion a été coupée pendant l'envoi.", retry: true });
      });
      const form = new FormData();
      form.append("file", r.entries[i].file, r.entries[i].file.name);
      xhr.send(form);
    });
  }

  async function network(r) {
    try {
      const resp = await fetch("/api/photos", { method: "POST" });
      if (!resp.ok) throw new Error(String(resp.status));
      r.batchId = (await resp.json()).id;
      r.linked.resolve(true);
    } catch {
      r.fatal = "Impossible de joindre le serveur. Vérifie ta connexion à internet, puis réessaie.";
      r.linked.resolve(false);
      for (const d of r.ready) d.resolve({ ok: false, error: r.fatal });
      return;
    }

    let cursor = 0;
    async function worker() {
      while (cursor < r.n && !r.closed) {
        const i = cursor++;
        let result = await uploadOne(r, i);
        if (!result.ok && result.retry && !r.closed) {
          if (!(await isReadable(r.entries[i].file))) {
            // Déplacée entre l'ajout et le clic : réessayer ne servirait à rien.
            result = { ok: false, error: MISSING_FILE(r.entries[i].file.name) };
          } else {
            await sleep(1200);
            r.progress[i] = 0;
            result = await uploadOne(r, i);
          }
        }
        if (result.ok) {
          // Gardée en mémoire : le téléchargement marchera même après
          // l'effacement des fichiers sur le serveur.
          try {
            const resp = await fetch(`/api/photos/${r.batchId}/${result.index}/file`);
            if (resp.ok) result.blobUrl = URL.createObjectURL(await resp.blob());
          } catch { /* on se rabattra sur le lien du serveur */ }
        }
        r.results[i] = result;
        r.ready[i].resolve(result);
      }
    }
    await Promise.all(Array.from({ length: Math.min(UPLOAD_CONCURRENCY, r.n) }, worker));
  }

  const fileUrl = (r, res) => `/api/photos/${r.batchId}/${res.index}/file`;
  const photoSrc = (r, res) => res.blobUrl || `/api/photos/${r.batchId}/${res.index}/view`;

  /* ======================================================================
     YAYO//LAB : chronologie du spectacle
     ====================================================================== */

  let run = null;

  function createRun(entries) {
    return {
      entries,
      n: entries.length,
      results: new Array(entries.length).fill(null),
      ready: entries.map(() => deferred()),
      progress: new Array(entries.length).fill(0),
      linked: deferred(),
      batchId: null,
      fatal: null,
      skip: false,
      closed: false,
      finalising: false,
      timers: new Set(),
      started: performance.now(),
      perPhoto: 3000,
    };
  }

  const alive = (r) => r === run && !r.closed;

  // Attente du spectacle : « Passer l'animation » les résout toutes d'un coup.
  function wait(ms) {
    const r = run;
    if (!r || r.skip) return Promise.resolve();
    return new Promise((resolve) => {
      const timer = { resolve };
      timer.id = setTimeout(() => { r.timers.delete(timer); resolve(); }, ms * SPEED);
      r.timers.add(timer);
    });
  }

  function skipShow() {
    const r = run;
    if (!r || r.skip || r.closed) return;
    r.skip = true;
    for (const timer of r.timers) { clearTimeout(timer.id); timer.resolve(); }
    r.timers.clear();
    stopGlitch();
    if (!r.fatal) enterFinalising(r);
  }

  async function startRun(entries) {
    const r = (run = createRun(entries));
    openLab();
    network(r);
    try {
      await boot(r);
      if (!alive(r)) return;
      if (r.fatal) return showFatal(r);
      await goOnline(r);
      r.perPhoto = Math.max(450, Math.min(3200, 12500 / r.n));
      for (let i = 0; i < r.n && !r.skip; i++) await processPhoto(r, i);
      if (!alive(r)) return;
      if (r.skip) await finishSkipped(r);
      else await finale(r);
    } catch (err) {
      console.error(err);
      await Promise.all(r.ready.map((d) => d.promise));
    }
    if (alive(r)) showResults(r);
  }

  async function boot(r) {
    $("boot-lines").innerHTML = "";
    await decodeText($("boot-logo"), "YAYO//LAB", 1000);
    await bootLine("Initialisation du noyau de restauration", "OK", 260);
    await bootLine("Calibrage des capteurs optiques", "OK", 220);
    await bootLine("Chargement des décodeurs JPEG · HEIC · PNG · WEBP", "OK", 220);
    const link = await bootLine("Liaison sécurisée avec le serveur", null, 0);
    const ok = await r.linked.promise;
    link.textContent = ok ? "OK" : "ÉCHEC";
    link.className = ok ? "st" : "st bad";
    if (!ok) return wait(900);
    const total = r.entries.reduce((sum, entry) => sum + entry.file.size, 0);
    await bootLine(`${plural(r.n, "photo")} en file d'attente · ${formatBytes(total)}`, "PRÊT", 220);
    await wait(600);
  }

  async function bootLine(label, status, ms) {
    const line = document.createElement("div");
    line.className = "boot-line";
    const text = document.createElement("span");
    const dots = document.createElement("span");
    dots.className = "dots";
    const st = document.createElement("span");
    st.className = "st wait";
    st.textContent = "···";
    line.append(text, dots, st);
    $("boot-lines").appendChild(line);
    await typeText(text, `> ${label}`, 5);
    await wait(ms);
    if (status) {
      st.textContent = status;
      st.className = "st";
    }
    return st;
  }

  async function goOnline(r) {
    $("lab-session").textContent = `session ${r.batchId.slice(0, 8).toUpperCase()}`;
    $("lab-boot").classList.add("gone");
    await wait(480);
    $("lab-boot").hidden = true;
    const frame = $("lab-frame");
    frame.classList.remove("booting");
    frame.classList.add("online");
    layoutCore();
    logLine("Noyau de restauration en ligne", "ok");
    await wait(900);
  }

  async function processPhoto(r, i) {
    const entry = r.entries[i];
    const file = entry.file;
    const t = r.perPhoto;
    const fast = t < 1600;

    $("core-count").textContent = `Photo ${i + 1}/${r.n}`;
    $("core-name").textContent = file.name;
    setPhase("Analyse");
    setCoreState("analyzing", "damaged");
    setRing(0, 250);
    $("diag-list").innerHTML = "";
    $("ro-bytes").textContent = "0";
    $("ro-blocks").textContent = "0";
    $("ro-scans").textContent = "—";
    scope.target = 1;
    announce(`Analyse de la photo ${i + 1} sur ${r.n}`);
    logLine(`Acquisition · ${file.name} · ${formatBytes(file.size)}`);

    const [bytes, preview] = await Promise.all([readBytes(file), loadImage(entry.url).catch(() => null)]);
    if (r.skip) return;
    const info = sniff(bytes);
    core.preview = preview;
    core.fixed = null;
    core.aspect = preview
      ? preview.naturalWidth / preview.naturalHeight
      : info.width && info.height ? info.width / info.height : 3 / 4;
    core.block = info.progressive ? 44 : info.kind === "JPEG" ? 9 : 16;
    pixelCache.clear();
    fitPhoto();
    startGlitch();

    // Analyse : flux binaire, diagnostic, compteur d'octets.
    const analysis = t * 0.42;
    hexFile(file.name);
    const hexDone = hexStream(bytes, info, analysis);
    countTo($("ro-bytes"), file.size, analysis);
    setRing(0.45, analysis);
    setGlobal((i + 0.15) / r.n);

    await wait(t * 0.07);
    diagAdd("Signature", info.label, info.known ? "ok" : "warn");
    await wait(t * 0.07);
    const dims = preview ? [preview.naturalWidth, preview.naturalHeight] : info.width ? [info.width, info.height] : null;
    diagAdd("Dimensions", dims ? `${dims[0]} × ${dims[1]}` : "—", "ok");
    await wait(t * 0.07);

    const rows = {};
    if (info.kind === "JPEG") {
      rows.progressive = diagAdd("Encodage", info.progressive ? "Progressif" : "Standard", info.progressive ? "bad" : "ok");
      $("ro-scans").textContent = String(info.scans || "—");
      if (info.progressive) {
        logLine(`Anomalie · marqueur SOF2 : image codée en ${info.scans} passes successives`, "bad");
        if (!fast) progressivePasses(t * 0.24);
      }
    } else {
      rows.format = diagAdd("Format", info.label, "warn");
      logLine(`Format ${info.label} : non standard pour un logiciel de montage`, "warn");
    }
    await hexDone;
    if (r.skip) return;

    const res = await awaitResult(r, i);
    if (r.skip) return;
    if (!res.ok) {
      diagAdd("État", "Illisible", "bad");
      logLine(`Échec · ${res.error}`, "bad");
      setPhase("Irrécupérable");
      filmAdd(null, false);
      setGlobal((i + 1) / r.n);
      await wait(t * 0.35);
      return;
    }
    for (const code of res.issues) {
      if (rows[code]) continue;
      const [key, value] = ISSUE_SHORT[code] || [code, "Anomalie"];
      await wait(t * 0.04);
      rows[code] = diagAdd(key, value, "warn");
      logLine(`Anomalie · ${key.toLowerCase()} : ${value.toLowerCase()}`, "warn");
    }
    const flawed = res.issues.length > 0;
    const compat = diagAdd("Compatibilité", flawed ? "Incertaine" : "Bonne", flawed ? "bad" : "ok");

    // Reconstruction bloc par bloc, dans l'ordre d'un JPEG standard.
    setPhase("Reconstruction");
    setCoreState("rebuilding", "damaged");
    logLine("Reconstruction séquentielle · JPEG standard (baseline SOF0)");
    const fixed = await loadImage(photoSrc(r, res)).catch(() => preview);
    if (r.skip) return;
    if (fixed) {
      const aspect = fixed.naturalWidth / fixed.naturalHeight;
      if (Math.abs(aspect - core.aspect) > 0.01) {
        core.aspect = aspect;
        fitPhoto();
      }
    }
    const rebuildMs = t * 0.34;
    countTo($("ro-blocks"), jpegBlocks(res.width, res.height), rebuildMs);
    if (info.progressive) $("ro-scans").textContent = `${info.scans} → 1`;
    setRing(0.9, rebuildMs);
    setGlobal((i + 0.55) / r.n);
    if (fixed) await rebuild(fixed, rebuildMs);
    else await wait(rebuildMs);
    if (r.skip) return;

    // Vérification.
    scope.target = 0.04;
    setCoreState("fixed");
    setRing(1, 300);
    setPhase("Vérifiée");
    for (const row of Object.values(rows)) row.fix("Corrigé");
    compat.fix("100 %");
    stamp();
    logLine(`${res.filename} · prête pour le montage`, "ok");
    announce(`Photo ${i + 1} réparée`);
    filmAdd(photoSrc(r, res), true);
    setGlobal((i + 1) / r.n);
    await wait(t * 0.2);
  }

  async function awaitResult(r, i) {
    if (r.results[i]) return r.results[i];
    logLine("Transfert vers le noyau de calcul…");
    let settled = false;
    r.ready[i].promise.then(() => { settled = true; });
    while (!settled && !r.skip) {
      const p = Math.round(r.progress[i] * 100);
      setPhaseNow(p < 100 ? `Transfert ${p} %` : "Calcul serveur");
      await Promise.race([r.ready[i].promise, sleep(200)]);
    }
    return r.ready[i].promise;
  }

  async function finale(r) {
    const ok = r.results.filter((res) => res && res.ok).length;
    stars.target = REDUCED ? 0 : 2.4;
    ringRate(8);
    logLine(ok ? "Toutes les vérifications sont passées" : "Aucune photo n'a pu être restaurée", ok ? "ok" : "bad");
    await wait(700);
    if (!alive(r)) return;
    flash();
    $("lab-frame").classList.add("dimmed");
    const fin = $("lab-finale");
    fin.classList.toggle("failed", !ok);
    $("finale-sub").textContent = "";
    fin.hidden = false;
    await decodeText($("finale-title"), ok ? "Restauration terminée" : "Restauration impossible", 1000);
    $("finale-sub").textContent = `${ok} / ${plural(r.n, "photo")} ${ok > 1 ? "compatibles" : "compatible"}`;
    await wait(1800);
    stars.target = 0.07;
    ringRate(1);
  }

  function enterFinalising(r) {
    r.finalising = true;
    $("lab-boot").hidden = true;
    $("lab-frame").classList.add("dimmed");
    const fin = $("lab-finale");
    fin.classList.remove("failed");
    $("finale-title").textContent = "Finalisation";
    fin.hidden = false;
    updateFinalising(r);
  }

  function updateFinalising(r) {
    const done = r.results.filter(Boolean).length;
    $("finale-sub").textContent = `${done} / ${plural(r.n, "photo")} ${done > 1 ? "traitées" : "traitée"}`;
  }

  async function finishSkipped(r) {
    if (!r.finalising) enterFinalising(r);
    await Promise.all(r.ready.map((d) => d.promise.then(() => updateFinalising(r))));
    if (alive(r)) await sleep(400);
  }

  /* ---------- Ouverture / fermeture ---------- */

  let clockTimer = 0;

  function openLab() {
    const lab = $("lab");
    $("boot-logo").textContent = "";
    $("boot-lines").innerHTML = "";
    $("lab-boot").hidden = false;
    $("lab-boot").classList.remove("gone");
    const frame = $("lab-frame");
    frame.hidden = false;
    frame.className = "lab-frame booting";
    $("hex-stream").innerHTML = "";
    $("lab-log").innerHTML = "";
    $("diag-list").innerHTML = "";
    $("filmstrip").innerHTML = "";
    $("lab-session").textContent = "session ——";
    $("lab-finale").hidden = true;
    $("lab-results").hidden = true;
    $("lab-skip").hidden = false;
    $("ro-bytes").textContent = "0";
    $("ro-blocks").textContent = "0";
    $("ro-scans").textContent = "—";
    setGlobal(0);
    setRing(0, 0);
    setCoreState();
    core.preview = core.fixed = null;
    const ctx = coreCanvas.getContext("2d");
    ctx.clearRect(0, 0, coreCanvas.width, coreCanvas.height);
    scope.chaos = scope.target = 0.3;
    stars.speed = stars.target = REDUCED ? 0 : 0.07;

    lab.hidden = false;
    document.body.style.overflow = "hidden";
    lab.tabIndex = -1;
    lab.focus();
    resizeBackground();
    seedStars();
    startLoop();
    startRings();
    tickClock();
    clearInterval(clockTimer);
    clockTimer = setInterval(tickClock, 1000);
  }

  function closeLab(clear) {
    const r = run;
    if (r) {
      r.closed = true;
      r.skip = true;
      for (const timer of r.timers) { clearTimeout(timer.id); timer.resolve(); }
      // Délai : un téléchargement tout juste lancé lit encore ces fichiers.
      const urls = r.results.filter((res) => res && res.blobUrl).map((res) => res.blobUrl);
      setTimeout(() => urls.forEach((url) => URL.revokeObjectURL(url)), 60000);
    }
    stopGlitch();
    stopLoop();
    clearInterval(clockTimer);
    $("lab").hidden = true;
    document.body.style.overflow = "";
    if (clear) clearSelection();
    $("repair-btn").focus();
  }

  $("lab-skip").addEventListener("click", skipShow);
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && !$("lab").hidden && $("lab-results").hidden) skipShow();
  });

  /* ---------- Résultats ---------- */

  function showResults(r) {
    stopGlitch();
    const ok = r.results.filter((res) => res && res.ok);
    const failed = r.n - ok.length;
    $("lab-frame").hidden = true;
    $("lab-finale").hidden = true;
    $("lab-boot").hidden = true;
    $("lab-skip").hidden = true;

    const head = document.querySelector(".results-head");
    head.classList.toggle("failed", ok.length === 0);
    $("results-icon").innerHTML = `<use href="#i-${ok.length ? "check" : "alert"}"/>`;
    $("results-actions").hidden = ok.length === 0;
    $("results-tip").hidden = ok.length === 0;
    $("results-hint").hidden = true;
    $("lab-close").hidden = true;
    $("lab-again-label").textContent = "Réparer d'autres photos";

    if (!ok.length) {
      $("results-title").textContent = r.n > 1 ? "Aucune photo n'a pu être réparée" : "Cette photo n'a pas pu être réparée";
      $("results-sub").textContent = "Le détail est juste en dessous. Essaie avec d'autres fichiers.";
    } else {
      $("results-title").textContent = failed
        ? `${ok.length} photos réparées sur ${r.n}`
        : ok.length === 1 ? "Ta photo est réparée !" : "Tes photos sont réparées !";
      let sub = ok.length === 1
        ? "Elle est prête pour ton logiciel de montage."
        : `Les ${ok.length} photos sont prêtes pour ton logiciel de montage.`;
      if (failed) sub += failed === 1 ? " Une photo n'a pas pu être réparée, le détail est plus bas." : ` ${failed} photos n'ont pas pu être réparées, le détail est plus bas.`;
      $("results-sub").textContent = sub;
      $("dl-all-label").textContent = ok.length === 1 ? "Télécharger la photo" : `Tout télécharger (${ok.length} photos)`;
      $("dl-zip").hidden = ok.length < 2;
    }

    const grid = $("results-grid");
    grid.innerHTML = "";
    r.results.forEach((res, i) => grid.appendChild(resultCard(r, res, r.entries[i], i)));

    $("lab-results").hidden = false;
    $("lab-results").scrollTop = 0;
    announce($("results-title").textContent);
    (ok.length ? $("dl-all") : $("lab-again")).focus();
  }

  function resultCard(r, res, entry, k) {
    const card = document.createElement("article");
    card.className = `result-photo lab-panel${res && res.ok ? "" : " bad"}`;
    card.style.animationDelay = `${Math.min(k, 12) * 70}ms`;
    if (res && res.ok) {
      card.innerHTML = `
        <div class="rp-thumb">
          <img alt="" loading="lazy">
          <span class="rp-badge"><svg class="icon" aria-hidden="true"><use href="#i-tick"/></svg> Réparée</span>
        </div>
        <div class="rp-body">
          <p class="rp-name"></p>
          <ul class="rp-fixes"></ul>
          <a class="lab-btn lab-btn-small">
            <svg class="icon" aria-hidden="true"><use href="#i-download"/></svg> Télécharger
          </a>
        </div>`;
      const img = card.querySelector("img");
      img.src = photoSrc(r, res);
      img.alt = `Photo réparée : ${res.filename}`;
      card.querySelector(".rp-name").textContent = res.filename;
      const fixes = res.issue_labels && res.issue_labels.length
        ? res.issue_labels
        : ["Aucun défaut trouvé : réenregistrée au format standard par sécurité"];
      const list = card.querySelector(".rp-fixes");
      for (const label of fixes) {
        const li = document.createElement("li");
        li.innerHTML = `<svg class="icon" aria-hidden="true"><use href="#i-tick"/></svg><span></span>`;
        li.querySelector("span").textContent = label;
        list.appendChild(li);
      }
      const link = card.querySelector("a");
      link.href = res.blobUrl || fileUrl(r, res);
      link.download = res.filename;
      link.setAttribute("aria-label", `Télécharger ${res.filename}`);
    } else {
      card.innerHTML = `
        <div class="rp-thumb"><svg class="icon" aria-hidden="true"><use href="#i-alert"/></svg></div>
        <div class="rp-body"><p class="rp-name"></p><p class="rp-error"></p></div>`;
      card.querySelector(".rp-name").textContent = entry.file.name;
      card.querySelector(".rp-error").textContent = (res && res.error) || "Cette photo n'a pas pu être réparée.";
    }
    return card;
  }

  function showFatal(r) {
    stopGlitch();
    $("lab-frame").hidden = true;
    $("lab-finale").hidden = true;
    $("lab-boot").hidden = true;
    $("lab-skip").hidden = true;
    document.querySelector(".results-head").classList.add("failed");
    $("results-icon").innerHTML = `<use href="#i-alert"/>`;
    $("results-title").textContent = "Le serveur ne répond pas";
    $("results-sub").textContent = r.fatal;
    $("results-actions").hidden = true;
    $("results-hint").hidden = true;
    $("results-tip").hidden = true;
    $("results-grid").innerHTML = "";
    $("lab-again-label").textContent = "Réessayer";
    $("lab-close").hidden = false;
    $("lab-results").hidden = false;
    $("lab-again").focus();
  }

  $("lab-again").addEventListener("click", () => {
    const r = run;
    if (r && r.fatal) {
      closeLab(false);
      startRun(r.entries);
    } else {
      closeLab(true);
    }
  });
  $("lab-close").addEventListener("click", () => closeLab(false));

  $("dl-all").addEventListener("click", async () => {
    const button = $("dl-all");
    const label = $("dl-all-label");
    const links = Array.from($("results-grid").querySelectorAll("a[download]"));
    if (!links.length) return;
    const original = label.textContent;
    if (links.length > 1) {
      $("results-hint").textContent =
        "Si ton navigateur demande s'il peut télécharger plusieurs fichiers, clique sur « Autoriser ».";
      $("results-hint").hidden = false;
    }
    button.disabled = true;
    label.textContent = "Téléchargement…";
    for (let k = 0; k < links.length; k++) {
      links[k].click();
      if (k < links.length - 1) await sleep(450);
    }
    label.textContent = links.length > 1 ? "C'est téléchargé !" : "C'est téléchargé !";
    await sleep(2600);
    label.textContent = original;
    button.disabled = false;
  });

  $("dl-zip").addEventListener("click", async () => {
    const r = run;
    try {
      const resp = await fetch(`/api/photos/${r.batchId}/zip`);
      if (!resp.ok) throw new Error(String(resp.status));
      const url = URL.createObjectURL(await resp.blob());
      const link = document.createElement("a");
      link.href = url;
      link.download = "photos-corrigees.zip";
      document.body.appendChild(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 60000);
    } catch {
      $("results-hint").textContent =
        "Le fichier ZIP n'est plus disponible : les photos sont effacées du serveur après quelques minutes. Utilise le gros bouton au-dessus.";
      $("results-hint").hidden = false;
    }
  });

  /* ======================================================================
     Effets : texte, journal, diagnostic, flux binaire
     ====================================================================== */

  function decodeText(el, text, ms) {
    const token = {};
    el._decode = token;
    if (REDUCED || (run && run.skip)) {
      el.textContent = text;
      return Promise.resolve();
    }
    const start = performance.now();
    return new Promise((resolve) => {
      let done = false;
      const finish = () => {
        if (done) return;
        done = true;
        release();
        if (el._decode === token) el.textContent = text;
        resolve();
      };
      const release = guard(ms, finish);
      function frame(now) {
        if (done) return;
        if (el._decode !== token) return finish();
        const p = Math.min(1, Math.max(0, (now - start) / ms));
        const fixed = Math.floor(p * text.length);
        let out = text.slice(0, fixed);
        for (let k = fixed; k < text.length; k++) {
          out += text[k] === " " ? " " : GLYPHS[(Math.random() * GLYPHS.length) | 0];
        }
        el.textContent = out;
        if (p < 1) requestAnimationFrame(frame);
        else finish();
      }
      requestAnimationFrame(frame);
    });
  }

  // Les effets image par image reposent sur requestAnimationFrame, que le
  // navigateur suspend quand l'onglet est caché : un minuteur de secours les
  // termine à l'heure, et « Passer l'animation » les termine tout de suite.
  function guard(ms, finish) {
    const r = run;
    if (!r) return () => {};
    const timer = { resolve: finish };
    timer.id = setTimeout(() => { r.timers.delete(timer); finish(); }, ms + 120);
    r.timers.add(timer);
    return () => { clearTimeout(timer.id); r.timers.delete(timer); };
  }

  function typeText(el, text, msPerChar) {
    if (REDUCED || (run && run.skip)) {
      el.textContent = text;
      return Promise.resolve();
    }
    // Calé sur l'horloge et non sur les ticks : un onglet en arrière-plan
    // ralentit les minuteries, le texte doit rattraper son retard d'un coup.
    const start = performance.now();
    return new Promise((resolve) => {
      const id = setInterval(() => {
        const k = Math.ceil((performance.now() - start) / msPerChar);
        el.textContent = text.slice(0, k);
        if (k >= text.length || (run && run.skip)) {
          clearInterval(id);
          el.textContent = text;
          resolve();
        }
      }, msPerChar);
    });
  }

  const setPhase = (text) => { decodeText($("core-phase"), text, 420); };
  function setPhaseNow(text) {
    const el = $("core-phase");
    el._decode = null;
    el.textContent = text;
  }

  function elapsed() {
    const s = (performance.now() - (run ? run.started : 0)) / 1000;
    const mm = String(Math.floor(s / 60)).padStart(2, "0");
    return `[${mm}:${(s % 60).toFixed(2).padStart(5, "0")}]`;
  }

  function logLine(text, kind) {
    const log = $("lab-log");
    const line = document.createElement("div");
    line.className = `log-line typing ${kind || ""}`;
    const time = document.createElement("time");
    time.textContent = elapsed();
    const body = document.createTextNode("");
    line.append(time, body);
    log.appendChild(line);
    while (log.childElementCount > 12) log.firstElementChild.remove();
    typeText({ set textContent(v) { body.data = v; } }, text, 6).then(() => line.classList.remove("typing"));
  }

  function announce(message) {
    $("lab-live").textContent = message;
  }

  function diagAdd(key, value, state) {
    const list = $("diag-list");
    const li = document.createElement("li");
    li.innerHTML = `<span class="diag-k"></span><span class="diag-v"></span><svg class="icon" aria-hidden="true"><use/></svg>`;
    const valueEl = li.querySelector(".diag-v");
    const use = li.querySelector("use");
    li.querySelector(".diag-k").textContent = key;
    const set = (v, s) => {
      li.className = `diag-row ${s}`;
      valueEl.textContent = v;
      use.setAttribute("href", s === "ok" || s === "fixed" ? "#i-tick" : "#i-alert");
    };
    set(value, state);
    list.appendChild(li);
    while (list.childElementCount > 9) list.firstElementChild.remove();
    return { fix: (v) => set(v, "fixed") };
  }

  function countTo(el, to, ms) {
    const start = performance.now();
    return new Promise((resolve) => {
      let done = false;
      const finish = () => {
        if (done) return;
        done = true;
        release();
        el.textContent = to.toLocaleString("fr-FR");
        resolve();
      };
      const release = guard(ms * SPEED, finish);
      function frame(now) {
        if (done) return;
        const p = Math.min(1, Math.max(0, (now - start) / (ms * SPEED))); // l'horodatage rAF peut précéder start
        el.textContent = Math.round(to * easeOut(p)).toLocaleString("fr-FR");
        if (p < 1) requestAnimationFrame(frame);
        else finish();
      }
      requestAnimationFrame(frame);
    });
  }

  function setGlobal(fraction) {
    const pct = Math.round(Math.min(1, fraction) * 100);
    $("lab-bar-fill").style.width = `${pct}%`;
    $("lab-percent").textContent = `${pct} %`;
  }

  function setRing(p, ms) {
    const ring = $("ring-progress");
    ring.style.transitionDuration = `${Math.round(ms * SPEED)}ms`;
    ring.style.strokeDashoffset = String(RING_LENGTH * (1 - p));
  }

  function filmAdd(src, ok) {
    const strip = $("filmstrip");
    const film = document.createElement("div");
    film.className = ok ? "film" : "film bad";
    if (src) film.style.backgroundImage = `url("${src}")`;
    strip.appendChild(film);
    const capacity = Math.max(1, Math.floor(strip.clientWidth / 46));
    while (strip.childElementCount > capacity) strip.firstElementChild.remove();
  }

  function flash() {
    const el = $("lab-flash");
    el.classList.remove("on");
    void el.offsetWidth;
    el.classList.add("on");
  }

  function stamp() {
    const el = $("core-stamp");
    el.classList.remove("show");
    void el.offsetWidth;
    el.classList.add("show");
  }

  /* ---------- Flux binaire ---------- */

  const BYTES_PER_LINE = 8;
  const hex2 = (n) => n.toString(16).toUpperCase().padStart(2, "0");

  function hexAppend(node) {
    const stream = $("hex-stream");
    stream.appendChild(node);
    while (stream.childElementCount > 90) stream.firstElementChild.remove();
  }

  function hexFile(name) {
    const el = document.createElement("div");
    el.className = "hx-file";
    el.textContent = `// ${name}`;
    hexAppend(el);
  }

  // Les lignes à montrer : celles qui contiennent les marqueurs, le reste résumé.
  function hexPlan(bytes, info) {
    const lines = new Set();
    const tags = new Map();
    const marks = new Map();
    if (info.kind === "JPEG") {
      for (const m of info.markers) {
        const line = Math.floor(m.off / BYTES_PER_LINE);
        lines.add(line);
        lines.add(line + 1);
        const bad = PROGRESSIVE_SOF.includes(m.code);
        const [short, text] = MARKERS[m.code] || [`APP${m.code - 0xe0}`, "métadonnées"];
        marks.set(m.off, bad ? "bad" : "m");
        marks.set(m.off + 1, bad ? "bad" : "m");
        if (!tags.has(line)) tags.set(line, []);
        tags.get(line).push({ text: `FF${hex2(m.code)} · ${short} — ${text}`, cls: bad ? "bad" : "" });
        if (bad) { lines.add(line + 2); lines.add(line + 3); }
      }
      if (info.sosOff !== null) {
        const line = Math.floor(info.sosOff / BYTES_PER_LINE);
        for (let k = 2; k <= 5; k++) lines.add(line + k);
      }
    } else {
      for (let k = 0; k < 6; k++) lines.add(k);
      const [a, b] = info.signature;
      for (let k = a; k < b; k++) marks.set(k, info.kind === "?" ? "bad" : "m");
      tags.set(Math.floor(a / BYTES_PER_LINE), [{
        text: info.known ? `signature ${info.label} — format non standard` : "signature inconnue",
        cls: "warn",
      }]);
      const middle = Math.floor(bytes.length / 2 / BYTES_PER_LINE);
      for (let k = 0; k < 4; k++) lines.add(middle + k);
    }
    const maxLine = Math.ceil(bytes.length / BYTES_PER_LINE) - 1;
    const sorted = [...lines].filter((l) => l >= 0 && l <= maxLine).sort((x, y) => x - y);
    const plan = [];
    let prev = -1;
    for (const line of sorted) {
      if (prev >= 0 && line - prev > 1) plan.push({ gap: (line - prev - 1) * BYTES_PER_LINE });
      plan.push({ line });
      for (const tag of tags.get(line) || []) plan.push({ tag });
      prev = line;
    }
    return { plan: plan.slice(0, 70), marks };
  }

  function hexNode(bytes, item, marks) {
    const el = document.createElement("div");
    if (item.gap) {
      el.className = "hx hx-gap";
      el.textContent = `   ··· ${formatBytes(item.gap)} ···`;
      return el;
    }
    if (item.tag) {
      el.className = `hx hx-tag ${item.tag.cls}`;
      el.textContent = item.tag.text;
      return el;
    }
    el.className = "hx";
    const off = item.line * BYTES_PER_LINE;
    const offset = document.createElement("span");
    offset.className = "hx-off";
    offset.textContent = off.toString(16).toUpperCase().padStart(6, "0");
    el.appendChild(offset);
    for (let k = 0; k < BYTES_PER_LINE && off + k < bytes.length; k++) {
      const text = hex2(bytes[off + k]);
      const mark = marks.get(off + k);
      if (mark) {
        const span = document.createElement("span");
        span.className = mark === "bad" ? "hx-m bad" : "hx-m";
        span.textContent = text;
        el.appendChild(span);
      } else {
        el.appendChild(document.createTextNode(text));
      }
      if (k < BYTES_PER_LINE - 1) el.appendChild(document.createTextNode(" "));
    }
    return el;
  }

  async function hexStream(bytes, info, duration) {
    const { plan, marks } = hexPlan(bytes, info);
    const step = duration / Math.max(1, plan.length);
    for (const item of plan) {
      if (!run || run.skip) return;
      hexAppend(hexNode(bytes, item, marks));
      await wait(step);
    }
  }

  /* ======================================================================
     Le cœur : la photo abîmée, puis reconstruite bloc par bloc
     ====================================================================== */

  const coreCanvas = $("core-canvas");
  const coreCtx = coreCanvas.getContext("2d");
  const core = { preview: null, fixed: null, aspect: 3 / 4, block: 12, dpr: 1, glitching: false };
  const pixelCache = new Map();
  let ringSize = 480;
  let glitchTimer = 0;

  function setCoreState(...states) {
    const el = $("lab-core");
    el.classList.remove("analyzing", "damaged", "rebuilding", "fixed");
    if (states.length) el.classList.add(...states);
  }

  function layoutCore() {
    const el = $("lab-core");
    if (!el.clientWidth) return;
    ringSize = Math.max(200, Math.min(el.clientWidth, el.clientHeight - 40) * 0.98);
    el.style.setProperty("--ring", `${ringSize}px`);
    fitPhoto();
  }

  function fitPhoto() {
    const maxW = ringSize * 0.6;
    const maxH = ringSize * 0.72;
    let w = maxW;
    let h = w / core.aspect;
    if (h > maxH) { h = maxH; w = h * core.aspect; }
    const box = $("core-photo");
    box.style.width = `${Math.round(w)}px`;
    box.style.height = `${Math.round(h)}px`;
    core.dpr = Math.min(window.devicePixelRatio || 1, 2);
    const cw = Math.round(w * core.dpr);
    const ch = Math.round(h * core.dpr);
    if (coreCanvas.width !== cw || coreCanvas.height !== ch) {
      coreCanvas.width = cw;
      coreCanvas.height = ch;
      pixelCache.clear();
      redrawCore();
    }
  }

  function redrawCore() {
    if (core.fixed) coreCtx.drawImage(core.fixed, 0, 0, coreCanvas.width, coreCanvas.height);
    else if (core.preview || core.glitching) drawDamaged(false);
  }

  new ResizeObserver(() => { if (!$("lab").hidden) layoutCore(); }).observe($("lab-core"));

  function pixelated(src, w, h) {
    const bw = Math.max(1, Math.round(w / (core.block * core.dpr)));
    const bh = Math.max(1, Math.round(h / (core.block * core.dpr)));
    const key = `${bw}x${bh}`;
    let small = pixelCache.get(key);
    if (!small) {
      small = document.createElement("canvas");
      small.width = bw;
      small.height = bh;
      small.getContext("2d").drawImage(src, 0, 0, bw, bh);
      pixelCache.set(key, small);
    }
    return small;
  }

  const noiseCanvas = document.createElement("canvas");

  function drawNoise(w, h) {
    noiseCanvas.width = Math.ceil(w / 5);
    noiseCanvas.height = Math.ceil(h / 5);
    const g = noiseCanvas.getContext("2d");
    const data = g.createImageData(noiseCanvas.width, noiseCanvas.height);
    for (let k = 0; k < data.data.length; k += 4) {
      const v = (Math.random() * 120) | 0;
      data.data[k] = v * 0.6;
      data.data[k + 1] = v;
      data.data[k + 2] = v * 1.05;
      data.data[k + 3] = 255;
    }
    g.putImageData(data, 0, 0);
    coreCtx.imageSmoothingEnabled = false;
    coreCtx.drawImage(noiseCanvas, 0, 0, w, h);
    coreCtx.fillStyle = "#ffb547";
    coreCtx.textAlign = "center";
    coreCtx.font = `600 ${Math.round(w * 0.05)}px "JetBrains Mono", monospace`;
    coreCtx.fillText("APERÇU IMPOSSIBLE", w / 2, h / 2);
  }

  function drawDamaged(glitch) {
    const w = coreCanvas.width;
    const h = coreCanvas.height;
    if (!w || !h) return;
    if (core.preview) {
      coreCtx.imageSmoothingEnabled = false;
      coreCtx.drawImage(pixelated(core.preview, w, h), 0, 0, w, h);
    } else {
      drawNoise(w, h);
    }
    if (glitch) {
      // Bandes décalées : le signal « décroche ».
      const slices = 1 + ((Math.random() * 4) | 0);
      for (let k = 0; k < slices; k++) {
        const sy = (Math.random() * h) | 0;
        const sh = Math.max(2, (Math.random() * h * 0.07) | 0);
        const dx = ((Math.random() - 0.5) * w * 0.09) | 0;
        coreCtx.drawImage(coreCanvas, 0, sy, w, sh, dx, sy, w, sh);
      }
    }
    coreCtx.fillStyle = "rgba(255, 70, 110, 0.16)";
    coreCtx.fillRect(0, 0, w, h);
  }

  function startGlitch() {
    stopGlitch();
    core.glitching = true;
    if (REDUCED) return drawDamaged(false);
    const tick = () => {
      drawDamaged(true);
      glitchTimer = setTimeout(tick, 60 + Math.random() * 90);
    };
    tick();
  }

  function stopGlitch() {
    clearTimeout(glitchTimer);
    glitchTimer = 0;
    core.glitching = false;
  }

  // Un JPEG progressif se dessine en passes de plus en plus nettes : on le montre.
  async function progressivePasses(ms) {
    for (const block of [26, 16, 10]) {
      await wait(ms / 3);
      if (!core.glitching) return;
      core.block = block;
    }
  }

  // Reconstruction dans l'ordre d'un JPEG standard : ligne après ligne, de
  // gauche à droite. La tête d'écriture éclaire les blocs qu'elle pose.
  function rebuild(img, duration) {
    stopGlitch();
    core.fixed = null;
    const w = coreCanvas.width;
    const h = coreCanvas.height;
    const clean = document.createElement("canvas");
    clean.width = w;
    clean.height = h;
    clean.getContext("2d").drawImage(img, 0, 0, w, h);
    const bs = Math.max(6, Math.round(Math.min(w, h) / 24));
    const cols = Math.ceil(w / bs);
    const total = cols * Math.ceil(h / bs);
    const head = $("core-write");
    const boxH = $("core-photo").clientHeight;
    const start = performance.now();
    let drawn = 0;
    let lit = [];
    const paint = (k, glow) => {
      const x = (k % cols) * bs;
      const y = Math.floor(k / cols) * bs;
      coreCtx.drawImage(clean, x, y, bs, bs, x, y, bs, bs);
      if (glow) {
        coreCtx.fillStyle = "rgba(170, 252, 255, 0.6)";
        coreCtx.fillRect(x, y, bs, bs);
      }
    };
    return new Promise((resolve) => {
      let done = false;
      const finish = () => {
        if (done) return;
        done = true;
        release();
        coreCtx.drawImage(clean, 0, 0);
        core.fixed = img;
        resolve();
      };
      const release = guard(duration * SPEED, finish);
      function frame(now) {
        if (done) return;
        const p = Math.min(1, Math.max(0, (now - start) / (duration * SPEED)));
        const target = Math.floor(total * easeInOut(p));
        for (const k of lit) paint(k, false);
        lit = [];
        for (; drawn < target; drawn++) {
          paint(drawn, true);
          lit.push(drawn);
        }
        const row = Math.floor(Math.max(0, drawn - 1) / cols) + 1;
        head.style.transform = `translateY(${Math.min(boxH, ((row * bs) / h) * boxH)}px)`;
        if (p < 1) requestAnimationFrame(frame);
        else finish();
      }
      requestAnimationFrame(frame);
    });
  }

  /* ======================================================================
     Décor vivant : champ d'étoiles, anneaux, oscilloscope, horloge
     ====================================================================== */

  const bg = $("lab-bg");
  const bgCtx = bg.getContext("2d");
  const stars = { list: [], speed: 0.07, target: 0.07, dpr: 1 };
  const scope = { chaos: 0.3, target: 0.3, phase: 0 };
  let loopId = 0;
  let lastFrame = 0;
  let ringAnims = [];

  function newStar(z) {
    return {
      x: (Math.random() * 2 - 1) * 1.3,
      y: (Math.random() * 2 - 1) * 1.3,
      z: z ?? 0.1 + Math.random() * 0.9,
      c: Math.random() < 0.82 ? "150, 245, 255" : "255, 196, 120",
    };
  }

  function seedStars() {
    const count = window.innerWidth < 700 ? 110 : 230;
    stars.list = Array.from({ length: count }, () => newStar());
  }

  function resizeBackground() {
    stars.dpr = Math.min(window.devicePixelRatio || 1, 1.5);
    bg.width = Math.round(window.innerWidth * stars.dpr);
    bg.height = Math.round(window.innerHeight * stars.dpr);
  }
  window.addEventListener("resize", () => { if (!$("lab").hidden) resizeBackground(); });

  function drawStars(dt) {
    const w = bg.width;
    const h = bg.height;
    const cx = w / 2;
    const cy = h * 0.46;
    const scale = Math.max(w, h) * 0.5;
    stars.speed += (stars.target - stars.speed) * Math.min(1, dt * 2.2);
    bgCtx.clearRect(0, 0, w, h);
    bgCtx.lineCap = "round";
    for (const s of stars.list) {
      const pz = s.z;
      s.z -= stars.speed * dt;
      const x = cx + (s.x / s.z) * scale;
      const y = cy + (s.y / s.z) * scale;
      if (s.z <= 0.03 || x < -40 || x > w + 40 || y < -40 || y > h + 40) {
        Object.assign(s, newStar(1));
        continue;
      }
      const px = cx + (s.x / pz) * scale;
      const py = cy + (s.y / pz) * scale;
      const alpha = Math.min(1, (1 - s.z) * 1.25) * 0.85;
      bgCtx.strokeStyle = `rgba(${s.c}, ${alpha})`;
      bgCtx.lineWidth = Math.max(0.7, (1 - s.z) * 2.6) * stars.dpr;
      bgCtx.beginPath();
      bgCtx.moveTo(px, py);
      bgCtx.lineTo(x + 0.1, y);
      bgCtx.stroke();
    }
  }

  function drawScope(dt) {
    const c = $("scope");
    if (!c.offsetParent) return;
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const w = Math.round(c.clientWidth * dpr);
    const h = Math.round(c.clientHeight * dpr);
    if (c.width !== w || c.height !== h) { c.width = w; c.height = h; }
    const g = c.getContext("2d");
    scope.chaos += (scope.target - scope.chaos) * Math.min(1, dt * 2.5);
    scope.phase += dt * (1.5 + scope.chaos * 5);
    const ch = scope.chaos;
    g.clearRect(0, 0, w, h);
    g.strokeStyle = "rgba(73, 242, 255, 0.12)";
    g.lineWidth = 1;
    g.beginPath();
    for (let k = 1; k < 6; k++) { g.moveTo((w * k) / 6, 0); g.lineTo((w * k) / 6, h); }
    g.moveTo(0, h / 2);
    g.lineTo(w, h / 2);
    g.stroke();
    const red = Math.round(73 + (255 - 73) * ch);
    const green = Math.round(242 - (242 - 110) * ch);
    const blue = Math.round(255 - (255 - 120) * ch);
    g.strokeStyle = `rgb(${red}, ${green}, ${blue})`;
    g.shadowColor = g.strokeStyle;
    g.shadowBlur = 8 * dpr;
    g.lineWidth = 1.6 * dpr;
    g.beginPath();
    for (let x = 0; x <= w; x += 2 * dpr) {
      const u = x / w;
      let y = Math.sin(u * 13 + scope.phase * 3) * 0.55 * (1 - ch * 0.5);
      y += ch * ((Math.random() - 0.5) * 0.8 + Math.sin(u * 53 + scope.phase * 11) * 0.35);
      const py = h / 2 + y * h * 0.4;
      if (x === 0) g.moveTo(x, py); else g.lineTo(x, py);
    }
    g.stroke();
    g.shadowBlur = 0;
  }

  function loop(now) {
    const dt = Math.min(0.05, (now - lastFrame) / 1000 || 0.016);
    lastFrame = now;
    drawStars(REDUCED ? 0 : dt);
    drawScope(dt);
    loopId = requestAnimationFrame(loop);
  }

  function startLoop() {
    cancelAnimationFrame(loopId);
    lastFrame = performance.now();
    loopId = requestAnimationFrame(loop);
  }

  function stopLoop() {
    cancelAnimationFrame(loopId);
    loopId = 0;
  }

  function startRings() {
    if (REDUCED || ringAnims.length || !Element.prototype.animate) return;
    const spin = (id, ms, dir) => $(id).animate(
      [{ transform: "rotate(0deg)" }, { transform: `rotate(${dir * 360}deg)` }],
      { duration: ms, iterations: Infinity },
    );
    ringAnims = [spin("ring-a", 70000, 1), spin("ring-b", 28000, -1), spin("ring-c", 46000, 1)];
  }

  function ringRate(rate) {
    for (const anim of ringAnims) {
      if (anim.updatePlaybackRate) anim.updatePlaybackRate(rate);
      else anim.playbackRate = rate;
    }
  }

  function tickClock() {
    $("lab-clock").textContent = new Date().toLocaleTimeString("fr-FR");
  }
})();
