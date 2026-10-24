"use strict";

/* Onglet Téléphone : le site guide Yayo, le Pont Yayo (programme local sur
   127.0.0.1:47863) fait l'accès au téléphone et à la clé. Voir
   docs/superpowers/specs/2026-10-23-transfert-telephone-design.md et
   2026-10-24-telephone-v2-design.md. Le mode avancé (« Ne pas toucher »,
   phone-expert.js) se branche sur le noyau exposé en bas de ce fichier ;
   éteint, l'onglet est exactement le parcours de Yayo. */

(() => {
  const $ = (id) => document.getElementById(id);
  const R = window.YayoRules;

  // En local seulement, « ?pont=47864 » vise un autre port (faux pont de développement).
  const BRIDGE = (() => {
    const local = /^(localhost|127\.0\.0\.1)$/.test(location.hostname);
    const port = local ? new URLSearchParams(location.search).get("pont") : null;
    return /^\d{2,5}$/.test(port || "") ? `http://127.0.0.1:${port}` : "http://127.0.0.1:47863";
  })();
  const API_REQUIRED = 2;
  // 1.0.x copiait les copies WhatsApp et les clips des photos animées : des doublons sur la clé.
  const MIN_BRIDGE = "1.1.1";
  // Mise à jour automatique (pont 1.1.0+) : au-delà de ce délai, on propose de le faire à la main.
  const UPDATE_WAIT = 3 * 60e3;
  const REDUCED = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const IS_WINDOWS = /Windows NT/i.test(navigator.userAgent);
  const SOURCES = [
    ["all", "Tout"],
    ["camera", "Appareil photo"],
    ["whatsapp", "WhatsApp"],
    ["screenshots", "Captures d'écran"],
    ["other", "Autres"],
  ];
  const PHASES = {
    repair: "On range ta clé : doublons et photos animées…",
    drive: "On regarde ce qu'il y a déjà sur la clé…",
    phone: "On parcourt les photos du téléphone…",
    compare: "On compare avec la clé pour éviter les doublons…",
    details: "On lit la date et le lieu de chaque photo…",
  };
  // En galerie (et dans les volets du mode avancé), la page défile.
  const SCROLL_PANELS = new Set(["galerie", "xp-rangement", "xp-lieux", "xp-options", "xp-stats"]);

  const st = {
    bridge: null,
    token: null,
    wasMissing: false,
    needUpdate: false,
    update: null, // mise à jour automatique : { state, at }
    phone: null,
    drives: [],
    driveId: null,
    scan: null,
    scanning: false,
    preparing: false,
    items: [],
    byId: new Map(),
    selected: new Set(),
    excluded: null,
    filter: { source: "all", kind: "all", hideOnDrive: false },
    transfer: null,
    lastCopy: null,
    timer: 0,
  };

  /* ---------- Mode avancé : branchements ---------- */

  let expert = null; // fourni par phone-expert.js
  const X = () => (expert && expert.active ? expert : null);

  /** Mode normal : ni dossiers masqués, ni clips de photos animées, ni doublons du téléphone. */
  const eligible = (item) => (X() ? X().eligible(item) : !item.hidden && !item.live && !item.duplicateOf);
  const selectable = (item) => eligible(item) && (!item.onDrive || !!(X() && X().allowOnDrive()));

  /** Destination de la copie : la clé choisie (mode normal) ou ce que le mode avancé a réglé. */
  function dest() {
    if (X()) return X().destination();
    const drive = st.drives.find((x) => x.id === st.driveId);
    if (!drive) return null;
    return {
      body: { driveId: drive.id },
      label: `${drive.label} (${drive.letter})`,
      free: drive.freeBytes,
      total: drive.totalBytes,
      isKey: true,
    };
  }

  /* ---------- Petits outils ---------- */

  const plural = (n, word, many) => `${n.toLocaleString("fr-FR")} ${n > 1 ? many || word + "s" : word}`;
  const formatBytes = R.formatBytes;

  function mediaLabel(photos, videos) {
    if (photos && videos) return plural(photos + videos, "photo et vidéo", "photos et vidéos");
    if (videos) return plural(videos, "vidéo");
    return plural(photos, "photo");
  }

  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

  function olderThan(version, minimum) {
    const a = String(version || "0").split(".").map(Number);
    const b = minimum.split(".").map(Number);
    for (let i = 0; i < 3; i++) {
      if ((a[i] || 0) !== (b[i] || 0)) return (a[i] || 0) < (b[i] || 0);
    }
    return false;
  }

  class BridgeError extends Error {
    constructor(kind, message, status) {
      super(message || kind);
      this.kind = kind;
      this.status = status;
    }
  }

  async function call(path, { method = "GET", body, timeout = 5000 } = {}) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeout);
    try {
      const headers = {};
      if (st.token) headers["X-Yayo-Token"] = st.token;
      if (body) headers["Content-Type"] = "application/json";
      let resp;
      try {
        resp = await fetch(BRIDGE + path, {
          method,
          headers,
          body: body ? JSON.stringify(body) : undefined,
          signal: controller.signal,
          cache: "no-store",
        });
      } catch {
        throw new BridgeError("offline");
      }
      let data = null;
      try { data = await resp.json(); } catch { /* corps vide */ }
      if (resp.status === 401) {
        st.token = null; // le pont a redémarré : nouvelle session au prochain tour
        throw new BridgeError("token");
      }
      if (!resp.ok) throw new BridgeError("http", (data && data.error) || "Le pont a répondu une erreur.", resp.status);
      return data;
    } finally {
      clearTimeout(timer);
    }
  }

  const thumbUrl = (id) => `${BRIDGE}/thumb/${encodeURIComponent(id)}?t=${st.token}`;
  const mediaUrl = (id) => `${BRIDGE}/media/${encodeURIComponent(id)}?t=${st.token}`;

  /* ---------- Étapes (colonne de gauche) et panneaux (droite) ---------- */

  function setRail(step, state, text) {
    const li = document.querySelector(`.rail-step[data-step="${step}"]`);
    li.classList.toggle("active", state === "active");
    li.classList.toggle("done", state === "done");
    if (text !== undefined) $("rail-" + step).textContent = text;
  }

  let currentPanel = null;
  function showPanel(name) {
    if (currentPanel === name) return;
    currentPanel = name;
    for (const panel of document.querySelectorAll(".stage-panel")) panel.hidden = panel.id !== "panel-" + name;
    syncScrollMode();
  }

  // En galerie, la page défile au lieu de tout tenir sur un écran (voir phone.css).
  function syncScrollMode() {
    document.body.classList.toggle("phone-scroll", SCROLL_PANELS.has(currentPanel) && !$("phone-main").hidden);
  }

  function showPontView(view) {
    showPanel("pont");
    for (const id of ["pont-searching", "pont-install", "pont-update", "pont-updating", "pont-notwindows"]) $(id).hidden = id !== "pont-" + view;
  }

  function showUpdating(state) {
    showPontView("updating");
    $("updating-text").textContent = state === "busy"
      ? "Le pont finit ce qu'il était en train de faire, puis il se met à jour tout seul."
      : "Ça prend quelques secondes. Ne débranche rien : la page continue toute seule.";
    setRail("pont", "active", "Mise à jour en cours…");
  }

  /* ---------- Boucle de détection ---------- */

  const tabVisible = () => !$("phone-main").hidden && !document.hidden;

  function schedule(ms) {
    clearTimeout(st.timer);
    st.timer = setTimeout(tick, ms);
  }

  function kick() { schedule(0); }

  async function tick() {
    if (!tabVisible() || st.transfer || st.scanning || st.preparing) return schedule(1200);
    try {
      if (!st.bridge || !st.token) await findBridge();
      else await refreshAll();
    } catch (err) {
      if (err.kind === "offline") bridgeLost();
      else if (err.kind !== "token") console.warn("pont :", err);
    }
    schedule(st.bridge ? 1500 : 2000);
  }

  const refreshAll = () => (X() ? X().refresh() : refreshDevices());

  async function findBridge() {
    if (!IS_WINDOWS) {
      showPontView("notwindows");
      setRail("pont", "active", "Ordinateur Windows nécessaire");
      return;
    }
    if (!currentPanel) showPontView("searching");
    const status = await call("/status", { timeout: 1500 });
    if (!status || status.app !== "yayo-pont") throw new BridgeError("offline");
    if (status.api < API_REQUIRED || olderThan(status.version, MIN_BRIDGE)) {
      await bridgeTooOld(status);
      return;
    }
    st.token = (await call("/session", { method: "POST" })).token;
    st.bridge = status;
    setRail("pont", "done", `Prêt (version ${status.version})`);
    if (st.needUpdate || st.update) celebrate("Le pont est à jour !");
    else if (st.wasMissing) celebrate("Le pont est installé !");
    st.wasMissing = false;
    st.needUpdate = false;
    st.update = null;
    await refreshAll();
  }

  const updateRunning = () => !!st.update && st.update.state === "started" && Date.now() - st.update.at < UPDATE_WAIT;

  /* Pont trop vieux. S'il sait se mettre à jour (1.1.0+), on le lui demande et on
     attend la nouvelle version ; sinon (1.0.x) Yayo le retélécharge une dernière fois. */
  async function bridgeTooOld(status) {
    st.needUpdate = true;
    if (status.updating || updateRunning()) return showUpdating();
    if ((status.features || []).includes("update")) {
      const retryBusy = st.update && st.update.state === "busy" && Date.now() - st.update.at > 5000;
      if (!st.update || retryBusy) {
        let state = "error";
        try {
          st.token = (await call("/session", { method: "POST" })).token;
          state = ((await call("/update", { method: "POST", timeout: 20000 })) || {}).state || "error";
        } catch (err) {
          if (err.kind === "offline") throw err;
        } finally {
          st.token = null; // nouvelle session avec le nouveau pont
        }
        st.update = { state, at: Date.now() };
      }
      if (st.update.state === "started" || st.update.state === "busy") return showUpdating(st.update.state);
    }
    showPontView("update");
    setRail("pont", "active", "Mise à jour nécessaire");
  }

  function bridgeLost() {
    const hadBridge = !!st.bridge;
    st.bridge = null;
    st.token = null;
    st.wasMissing = true;
    resetScan();
    // Pendant une mise à jour, l'ancien pont s'arrête avant que le nouveau démarre :
    // on garde l'écran de mise à jour plutôt que de proposer une installation.
    if (updateRunning()) showUpdating();
    else if (st.needUpdate) showPontView("update");
    else {
      showPontView(hadBridge ? "searching" : "install");
      setRail("pont", "active", hadBridge ? "Le pont ne répond plus…" : "À installer");
    }
    for (const step of ["telephone", "cle", "galerie"]) setRail(step, "");
    $("rail-action").hidden = true;
    checkPermission();
  }

  async function checkPermission() {
    try {
      const perm = await navigator.permissions.query({ name: "local-network-access" });
      $("pont-denied").hidden = perm.state !== "denied";
    } catch { /* permission inconnue de ce navigateur */ }
  }

  async function refreshDevices() {
    const d = await call("/devices");
    st.phone = d.phone;
    st.drives = d.drives || [];

    if (!st.phone || st.phone.state !== "ready") {
      resetScan();
      showTelephone(d);
      setRail("cle", "", "Pas encore branchée");
      setRail("galerie", "", "Les photos s'afficheront ici");
      return;
    }
    setRail("telephone", "done", st.phone.name);
    telState = null;
    $("scene-phone").classList.add("connected");
    $("scene-phone").classList.remove("waiting-phone");

    if (!st.drives.some((x) => x.id === st.driveId)) {
      if (st.driveId) resetScan();
      st.driveId = st.drives.length === 1 ? st.drives[0].id : null;
    }
    if (!st.driveId) {
      showCle();
      return;
    }
    const drive = st.drives.find((x) => x.id === st.driveId);
    drivesKey = null;
    setRail("cle", "done", `${drive.label} (${drive.letter})`);
    $("scene-key").classList.add("connected");

    const key = `n|${st.driveId}|${st.phone.name}`;
    if (!st.scan || st.scan.key !== key) {
      startScan({ driveId: st.driveId }, key);
      return;
    }
    if (currentPanel !== "galerie") showPanel("galerie");
    updateAction();
  }

  // Le sondage repasse toutes les 1,5 s : on ne redessine que si l'état change,
  // sinon les animations d'entrée redémarrent sans cesse et l'aide clignote.
  let telState = null;
  let drivesKey = null;

  function showTelephone(d) {
    showPanel("telephone");
    const state = d.phone && d.phone.state === "locked" ? "locked:" + d.phone.name : d.usbPhoneSeen ? "charging" : "none";
    if (state === telState) return;
    telState = state;
    drivesKey = null;
    setRail("telephone", "active");
    const scene = $("scene-phone");
    scene.classList.remove("connected");
    const help = $("tel-help");
    const wait = $("tel-wait").querySelector("span:last-child");
    if (d.phone && d.phone.state === "locked") {
      scene.classList.add("waiting-phone");
      $("tel-title").textContent = "Déverrouille ton téléphone";
      $("tel-text").textContent = `${d.phone.name} est branché. Déverrouille-le : s'il te demande d'autoriser l'accès aux données, touche « Autoriser ».`;
      help.innerHTML = `
        <div class="mini-phone"><div class="mini-screen">
          <div class="mini-dialog"><b>Autoriser l'accès aux données du téléphone ?</b>
          <span class="mock-row"><span class="mock-btn">Refuser</span><span class="mock-btn mock-target">Autoriser</span></span></div>
        </div></div>`;
      help.hidden = false;
      wait.textContent = "On attend que le téléphone partage ses photos…";
      setRail("telephone", "active", "Branché, à déverrouiller");
    } else if (d.usbPhoneSeen) {
      scene.classList.add("waiting-phone");
      $("tel-title").textContent = "Ton téléphone se recharge seulement";
      $("tel-text").textContent = "Sur le téléphone : fais glisser le haut de l'écran vers le bas, touche la notification USB, puis choisis « Transfert de fichiers ».";
      help.innerHTML = `
        <div class="mini-phone"><div class="mini-screen">
          <div class="mini-notif mock-target"><b>Système Android</b>Recharge de cet appareil via USB<br>Appuyez pour plus d'options.</div>
        </div></div>
        <span class="help-arrow" aria-hidden="true"><svg class="icon"><use href="#i-arrow"/></svg></span>
        <div class="mini-phone"><div class="mini-screen">
          <b>Utiliser la connexion USB pour</b>
          <span class="mini-option on mock-target">Transfert de fichiers / Android Auto</span>
          <span class="mini-option">Recharge uniquement</span>
        </div></div>`;
      help.hidden = false;
      wait.textContent = "On attend que le téléphone partage ses photos…";
      setRail("telephone", "active", "Branché, en simple recharge");
    } else {
      scene.classList.remove("waiting-phone");
      $("tel-title").textContent = "Branche ton téléphone";
      $("tel-text").textContent = "Avec son câble de charge, sur un port USB de l'ordinateur.";
      help.hidden = true;
      help.innerHTML = "";
      wait.textContent = "On attend ton téléphone…";
      setRail("telephone", "active", "Pas encore branché");
    }
  }

  function showCle() {
    showPanel("cle");
    telState = null;
    const key = st.drives.map((drive) => `${drive.id}:${drive.label}:${drive.freeBytes}`).join("|");
    if (key === drivesKey) return;
    drivesKey = key;
    setRail("cle", "active");
    const list = $("drive-list");
    if (st.drives.length > 1) {
      $("scene-key").classList.add("connected");
      $("cle-title").textContent = "Quelle clé utiliser ?";
      $("cle-text").textContent = "Plusieurs clés ou disques sont branchés : touche celui qui doit recevoir les photos.";
      $("cle-wait").hidden = true;
      list.innerHTML = "";
      st.drives.forEach((drive, k) => {
        const card = document.createElement("button");
        card.type = "button";
        card.className = "drive-card";
        card.style.animationDelay = `${k * 70}ms`;
        card.innerHTML = `<strong><svg class="icon" aria-hidden="true"><use href="#i-usb"/></svg><span></span></strong><small></small>`;
        card.querySelector("span").textContent = `${drive.label} (${drive.letter})`;
        card.querySelector("small").textContent = `${formatBytes(drive.freeBytes)} libres sur ${formatBytes(drive.totalBytes)}`;
        card.addEventListener("click", () => {
          st.driveId = drive.id;
          kick();
        });
        list.appendChild(card);
      });
      list.hidden = false;
      setRail("cle", "active", "À choisir");
    } else {
      $("scene-key").classList.remove("connected");
      $("cle-title").textContent = "Branche ta clé USB";
      $("cle-text").textContent = "Sur un autre port USB de l'ordinateur. Un disque dur externe marche aussi.";
      $("cle-wait").hidden = false;
      list.hidden = true;
      setRail("cle", "active", "Pas encore branchée");
    }
    setRail("galerie", "", "Les photos s'afficheront ici");
    $("rail-action").hidden = true;
  }

  /* ---------- Inventaire ---------- */

  function resetScan() {
    st.scan = null;
    st.scanRepair = null;
    st.items = [];
    st.byId.clear();
    st.selected.clear();
    $("rail-action").hidden = true;
  }

  const counters = { photos: 0, videos: 0 };
  function countTo(id, key, value) {
    const from = counters[key];
    counters[key] = value;
    const el = $(id);
    if (REDUCED || from === value) {
      el.textContent = value.toLocaleString("fr-FR");
      return;
    }
    const start = performance.now();
    const frame = (now) => {
      const p = Math.min(1, Math.max(0, (now - start) / 350)); // l'horodatage rAF peut précéder start
      el.textContent = Math.round(from + (value - from) * p).toLocaleString("fr-FR");
      if (p < 1) requestAnimationFrame(frame);
    };
    requestAnimationFrame(frame);
  }

  /** Analyse : `body` = corps de POST /scan, `key` = ce qui la rend périmée si ça change. */
  async function startScan(body, key) {
    st.scanning = true;
    resetScan();
    showPanel("scan");
    setRail("galerie", "active", "Inventaire en cours…");
    counters.photos = counters.videos = 0;
    $("scan-photos").textContent = "0";
    $("scan-videos").textContent = "0";
    $("scan-phase").textContent = PHASES.phone;
    try {
      const { jobId } = await call("/scan", { method: "POST", body });
      while (true) {
        const job = await call("/scan/" + jobId, { timeout: 30000 });
        countTo("scan-photos", "photos", job.photos);
        countTo("scan-videos", "videos", job.videos);
        let phase = PHASES[job.phase] || PHASES.phone;
        if (job.phase === "details" && job.detailsTotal) {
          phase += ` (${(job.detailsDone || 0).toLocaleString("fr-FR")} sur ${job.detailsTotal.toLocaleString("fr-FR")})`;
        }
        $("scan-phase").textContent = phase;
        if (job.state === "done") {
          st.scan = { id: jobId, key, deep: !!(body.options && body.options.deep), at: Date.now() };
          loadItems(job.items || []);
          st.scanRepair = job.repair || null;
          break;
        }
        if (job.state === "error") throw new BridgeError("http", job.error);
        await sleep(400);
      }
      await sleep(REDUCED ? 0 : 450); // laisse les compteurs finir leur course
      showGallery();
    } catch (err) {
      $("scan-phase").textContent = err.message && err.kind === "http"
        ? err.message + " On réessaie dans un instant…"
        : "Le pont ne répond plus, on réessaie…";
      await sleep(2500);
    } finally {
      st.scanning = false;
      kick();
    }
  }

  function loadItems(items) {
    st.items = items.map((item) => {
      const when = new Date(item.exifDate || item.date || NaN);
      return { ...item, when: Number.isNaN(when.getTime()) ? null : when };
    });
    st.byId = new Map(st.items.map((item) => [item.id, item]));
    st.excluded = R.countExcluded(st.items);
    // Par défaut, tout ce qui n'est pas déjà sur la clé est choisi… si ça tient.
    // Sinon rien n'est coché et la colonne explique qu'il faut choisir.
    const fresh = st.items.filter(selectable);
    const need = fresh.reduce((sum, item) => sum + item.size, 0);
    const target = dest();
    st.tooBig = !!target && target.free != null && need > target.free;
    st.needBytes = need;
    st.selected = new Set(st.tooBig ? [] : fresh.map((item) => item.id));
  }

  /* ---------- Galerie ---------- */

  const monthFormat = new Intl.DateTimeFormat("fr-FR", { month: "long", year: "numeric" });

  function visibleItems() {
    const { source, kind, hideOnDrive } = st.filter;
    const x = X();
    return st.items.filter((item) =>
      eligible(item)
      && (source === "all" || item.source === source)
      && (kind === "all" || item.kind === kind)
      && !(hideOnDrive && item.onDrive)
      && (!x || x.inPeriod(item)));
  }

  function showGallery() {
    const fresh = st.items.filter(selectable).length;
    setRail("galerie", "active", fresh ? plural(fresh, "nouvelle", "nouvelles") : "Tout est déjà sur la clé");
    renderChips();
    renderGallery();
    showPanel("galerie");
    $("rail-action").hidden = false;
    updateAction();
    if (X()) X().onGallery();
  }

  /** La petite ligne « … ne seront pas copiés » sous les filtres. */
  function renderSkipped() {
    // Le rangement fait pendant l'analyse : chez Yayo, souvent tout est déjà sur la clé
    // et rien ne sera copié, c'est alors la seule trace de ce qui a été réparé.
    const repaired = $("gallery-repaired");
    const fixed = R.repairSentence(st.scanRepair, X() ? "la destination" : "ta clé", !!X(), "scan");
    repaired.lastElementChild.textContent = fixed;
    repaired.hidden = !fixed;
    const note = $("gallery-skipped");
    const text = X() ? X().excludedNote() : st.excluded ? R.excludedSentence(st.excluded) : "";
    note.textContent = text;
    note.hidden = !text;
  }

  function renderChips() {
    const box = $("source-chips");
    box.innerHTML = "";
    const pool = st.items.filter(eligible);
    for (const [key, label] of SOURCES) {
      const count = key === "all" ? pool.length : pool.filter((item) => item.source === key).length;
      if (key !== "all" && count === 0) continue;
      const chip = document.createElement("button");
      chip.type = "button";
      chip.className = "chip";
      chip.dataset.source = key;
      chip.setAttribute("aria-pressed", String(st.filter.source === key));
      chip.innerHTML = `<span></span><small></small>`;
      chip.querySelector("span").textContent = label;
      chip.querySelector("small").textContent = count.toLocaleString("fr-FR");
      box.appendChild(chip);
    }
  }

  // Limite d'affichage : un téléphone peut contenir des dizaines de milliers de
  // photos. On en montre PAGE_SIZE à la fois (« Afficher plus » pour la suite).
  // La sélection, elle, se calcule sur les données puis l'écran est mis à jour
  // en une passe : instantané même sur 20 000 éléments.
  const PAGE_SIZE = 400;
  let shown = [];
  let rendered = 0;
  let tileRank = 0;
  const monthGroups = new Map();
  const sectionByMonth = new Map();
  const tileById = new Map();

  const monthKey = (item) => (item.when
    ? `${item.when.getFullYear()}-${String(item.when.getMonth() + 1).padStart(2, "0")}`
    : "sans-date");

  function renderGallery() {
    const gallery = $("gallery");
    thumbQueue.length = 0;
    thumbObserver.disconnect();
    tileById.clear();
    sectionByMonth.clear();
    monthGroups.clear();
    tileRank = 0;
    rendered = 0;
    gallery.innerHTML = "";
    shown = visibleItems();
    for (const item of shown) {
      const key = monthKey(item);
      if (!monthGroups.has(key)) monthGroups.set(key, []);
      monthGroups.get(key).push(item);
    }
    $("gallery-empty").hidden = shown.length > 0;
    if (!shown.length) {
      $("gallery-empty").textContent = st.items.some(eligible)
        ? "Rien à afficher avec ces filtres."
        : "Aucune photo ni vidéo trouvée sur le téléphone.";
    }
    renderSkipped();
    renderMore();
  }

  function renderMore() {
    const gallery = $("gallery");
    const end = Math.min(shown.length, rendered + PAGE_SIZE);
    const batches = new Map();
    for (let i = rendered; i < end; i++) {
      const item = shown[i];
      const key = monthKey(item);
      let section = sectionByMonth.get(key);
      if (!section) {
        section = createSection(key, item);
        gallery.appendChild(section);
        sectionByMonth.set(key, section);
      }
      if (!batches.has(section)) batches.set(section, document.createDocumentFragment());
      batches.get(section).appendChild(makeTile(item, tileRank++));
    }
    for (const [section, fragment] of batches) {
      section.querySelector(".month-grid").appendChild(fragment);
      updateMonthToggle(section);
    }
    rendered = end;
    const remaining = shown.length - rendered;
    $("gallery-more").hidden = remaining <= 0;
    if (remaining > 0) {
      $("more-label").textContent = `Afficher ${Math.min(PAGE_SIZE, remaining).toLocaleString("fr-FR")} de plus`;
      $("more-count").textContent = `${remaining.toLocaleString("fr-FR")} ${remaining > 1 ? "autres, plus anciennes" : "autre, plus ancienne"}`;
    }
  }

  $("more-btn").addEventListener("click", renderMore);

  function createSection(key, first) {
    const section = document.createElement("section");
    section.className = "month";
    section.dataset.month = key;
    section.innerHTML = `
      <div class="month-head"><h4></h4><span></span><button type="button" class="btn-text month-toggle"></button></div>
      <div class="month-grid"></div>`;
    section.querySelector("h4").textContent = key === "sans-date" ? "Sans date" : capitalize(monthFormat.format(first.when));
    section.querySelector(".month-head span").textContent = plural(monthGroups.get(key).length, "élément");
    return section;
  }

  const capitalize = (s) => s.charAt(0).toUpperCase() + s.slice(1);

  function makeTile(item, rank) {
    const wrap = document.createElement("div");
    wrap.className = "tile-wrap";
    const tile = document.createElement("div");
    const locked = !selectable(item);
    tile.className = "tile" + (locked ? " on-drive" : "");
    tile.dataset.id = item.id;
    tileById.set(item.id, tile);
    tile.tabIndex = 0;
    tile.setAttribute("role", "checkbox");
    tile.setAttribute("aria-checked", String(st.selected.has(item.id)));
    if (locked) tile.setAttribute("aria-disabled", "true");
    tile.setAttribute("aria-label", `${item.name}${item.onDrive ? (X() ? ", déjà sur la destination" : ", déjà sur la clé") : ""}`);
    if (X()) tile.title = X().tileTitle(item);
    if (rank < 40) tile.style.animationDelay = `${rank * 18}ms`;
    else tile.style.animation = "none";
    const onDriveBadge = item.onDrive
      ? `<span class="badge-ok"><svg class="icon"><use href="#i-tick"/></svg>${X() ? "Déjà là" : "Sur la clé"}</span>`
      : "<span hidden></span>";
    const videoBadge = item.kind === "video" ? `<span><svg class="icon"><use href="#i-play"/></svg>Vidéo</span>` : "";
    const flags = X() ? X().tileFlags(item) : "";
    tile.innerHTML = `
      <span class="tile-icon" aria-hidden="true"><svg class="icon"><use href="#i-${item.kind === "video" ? "film" : "image"}"/></svg></span>
      <img alt="" crossorigin="anonymous" decoding="async">
      <span class="tile-check" aria-hidden="true"><svg class="icon"><use href="#i-tick"/></svg></span>
      ${flags ? `<span class="tile-flags" aria-hidden="true">${flags}</span>` : ""}
      <span class="tile-badge" aria-hidden="true">${onDriveBadge}${videoBadge}</span>`;
    const img = tile.querySelector("img");
    img.addEventListener("load", () => img.classList.add("loaded"));
    img.addEventListener("error", () => img.remove());
    thumbObserver.observe(img);
    wrap.appendChild(tile);
    const zoom = document.createElement("button");
    zoom.type = "button";
    zoom.className = "tile-zoom";
    zoom.dataset.id = item.id;
    zoom.setAttribute("aria-label", `Agrandir ${item.name}`);
    zoom.innerHTML = `<svg class="icon" aria-hidden="true"><use href="#i-zoom"/></svg>`;
    wrap.appendChild(zoom);
    return wrap;
  }

  // Vignettes chargées à l'approche de l'écran, 4 à la fois : le téléphone
  // répond une par une et les premières visibles doivent arriver d'abord.
  const thumbQueue = [];
  let thumbsLoading = 0;
  const thumbObserver = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      thumbObserver.unobserve(entry.target);
      thumbQueue.push(entry.target);
    }
    pumpThumbs();
  }, { root: null, rootMargin: "300px 0px" });

  function pumpThumbs() {
    while (thumbsLoading < 4 && thumbQueue.length) {
      const img = thumbQueue.shift();
      if (!img.isConnected) continue;
      thumbsLoading++;
      const done = () => {
        thumbsLoading--;
        pumpThumbs();
      };
      img.addEventListener("load", done, { once: true });
      img.addEventListener("error", done, { once: true });
      img.src = thumbUrl(img.closest(".tile").dataset.id);
    }
  }

  function updateMonthToggle(section) {
    let choosable = 0;
    let selected = 0;
    for (const item of monthGroups.get(section.dataset.month) || []) {
      if (!selectable(item)) continue;
      choosable++;
      if (st.selected.has(item.id)) selected++;
    }
    const button = section.querySelector(".month-toggle");
    button.hidden = choosable === 0;
    const all = choosable > 0 && selected === choosable;
    button.textContent = all ? "Retirer le mois" : "Choisir tout le mois";
    button.dataset.all = String(all);
  }

  function paintTile(id) {
    const tile = tileById.get(id);
    if (tile) tile.setAttribute("aria-checked", String(st.selected.has(id)));
  }

  /** Après une action groupée : une seule passe sur ce qui est affiché. */
  function syncSelection() {
    for (const id of tileById.keys()) paintTile(id);
    for (const section of sectionByMonth.values()) updateMonthToggle(section);
    updateAction();
  }

  $("gallery").addEventListener("click", (event) => {
    const zoom = event.target.closest(".tile-zoom");
    if (zoom) return openViewer(zoom.dataset.id);
    const toggle = event.target.closest(".month-toggle");
    if (toggle) {
      const section = toggle.closest(".month");
      const on = toggle.dataset.all !== "true";
      for (const item of monthGroups.get(section.dataset.month) || []) {
        if (!selectable(item)) continue;
        if (on) st.selected.add(item.id);
        else st.selected.delete(item.id);
        paintTile(item.id);
      }
      updateMonthToggle(section);
      updateAction();
      return;
    }
    const tile = event.target.closest(".tile");
    if (!tile || tile.classList.contains("on-drive")) return;
    const id = tile.dataset.id;
    if (st.selected.has(id)) st.selected.delete(id);
    else st.selected.add(id);
    paintTile(id);
    updateMonthToggle(tile.closest(".month"));
    updateAction();
  });

  $("gallery").addEventListener("keydown", (event) => {
    const tile = event.target.closest(".tile");
    if (!tile || (event.key !== " " && event.key !== "Enter")) return;
    event.preventDefault();
    tile.click();
  });

  $("source-chips").addEventListener("click", (event) => {
    const chip = event.target.closest(".chip");
    if (!chip) return;
    st.filter.source = chip.dataset.source;
    for (const c of $("source-chips").children) c.setAttribute("aria-pressed", String(c === chip));
    renderGallery();
  });

  $("kind-chips").addEventListener("click", (event) => {
    const chip = event.target.closest(".chip");
    if (!chip) return;
    st.filter.kind = chip.dataset.kind;
    for (const c of $("kind-chips").children) c.setAttribute("aria-pressed", String(c === chip));
    renderGallery();
  });

  $("hide-ondrive").addEventListener("change", (event) => {
    st.filter.hideOnDrive = event.target.checked;
    renderGallery();
  });

  // « Tout » = tout ce que montrent les filtres, pages pas encore affichées comprises.
  $("select-all").addEventListener("click", () => {
    for (const item of shown) if (selectable(item)) st.selected.add(item.id);
    syncSelection();
  });

  $("select-none").addEventListener("click", () => {
    st.selected.clear();
    syncSelection();
  });

  function updateAction() {
    const target = dest();
    if (!target) return;
    let bytes = 0, photos = 0, videos = 0;
    for (const id of st.selected) {
      const item = st.byId.get(id);
      if (!item) continue;
      bytes += item.size;
      if (item.kind === "video") videos++;
      else photos++;
    }
    const count = photos + videos;
    const where = X() ? "la destination" : "la clé";
    $("sel-count").textContent = count ? `${count.toLocaleString("fr-FR")} ${count > 1 ? "choisies" : "choisie"}` : "Aucune photo choisie";
    $("sel-size").textContent = count ? formatBytes(bytes) : "";
    // Dossier quelconque du PC (mode avancé) : l'espace libre peut être inconnu.
    const known = target.free != null && target.total > 0;
    const gauge = document.querySelector(".space-gauge");
    gauge.hidden = !known;
    let over = false;
    if (known) {
      const used = target.total - target.free;
      const usedPct = (used / target.total) * 100;
      const selPct = Math.min(100 - usedPct, (bytes / target.total) * 100);
      $("space-used").style.width = `${usedPct}%`;
      $("space-sel").style.left = `${usedPct}%`;
      $("space-sel").style.width = `${selPct}%`;
      over = bytes > target.free;
      gauge.classList.toggle("over", over);
      $("space-text").textContent = over
        ? `Pas assez de place : il manque ${formatBytes(bytes - target.free)} sur ${where}.`
        : !count && st.tooBig
          ? `Tout ne tient pas sur ${where} (${formatBytes(st.needBytes)} pour ${formatBytes(target.free)} de libre) : choisis un mois ou quelques photos.`
          : count
          ? `Il restera ${formatBytes(target.free - bytes)} de libre sur ${where}.`
          : `${formatBytes(target.free)} de libre sur ${where}.`;
    } else {
      $("space-text").textContent = "Espace libre inconnu pour ce dossier.";
    }
    $("copy-btn").disabled = !count || over || st.preparing;
    const anyNew = st.items.some(selectable);
    $("copy-label").textContent = st.preparing ? "Préparation…" : count
      ? `Copier ${mediaLabel(photos, videos)}`
      : anyNew ? "Choisis des photos" : `Tout est déjà sur ${where}`;
    if (X()) X().onSelection();
  }

  /* ---------- Aperçu ---------- */

  function openViewer(id) {
    const item = st.byId.get(id);
    if (!item) return;
    const img = $("viewer-img");
    const note = $("viewer-note");
    img.alt = item.name;
    if (item.kind === "video") {
      img.src = thumbUrl(id);
      note.textContent = "Aperçu de la vidéo (l'image seulement)";
      note.hidden = false;
    } else {
      img.src = mediaUrl(id);
      note.hidden = true;
    }
    $("viewer").hidden = false;
    $("viewer-close").focus();
  }

  function closeViewer() {
    $("viewer").hidden = true;
    $("viewer-img").removeAttribute("src");
  }

  $("viewer-close").addEventListener("click", closeViewer);
  $("viewer").addEventListener("click", (event) => {
    if (event.target === $("viewer")) closeViewer();
  });
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && !$("viewer").hidden) closeViewer();
  });

  /* ---------- Copie ---------- */

  $("copy-btn").addEventListener("click", startTransfer);

  async function startTransfer() {
    if (st.transfer || st.preparing) return;
    const chosen = st.items.filter((item) => st.selected.has(item.id));
    const ids = chosen.map((item) => item.id);
    const target = dest();
    if (!ids.length || !target) return;
    $("copy-error").hidden = true;
    let body = { ...target.body, ids };
    const x = X();
    if (x) {
      // Mode avancé : options de copie, et lieux à calculer avant si le modèle en a besoin.
      st.preparing = true;
      updateAction();
      try {
        body = await x.transferBody(ids);
      } catch (err) {
        $("copy-error").textContent = err.message || "Impossible de préparer la copie.";
        $("copy-error").hidden = false;
        return;
      } finally {
        st.preparing = false;
        updateAction();
      }
    }
    st.transferKinds = {
      photos: chosen.filter((item) => item.kind !== "video").length,
      videos: chosen.filter((item) => item.kind === "video").length,
    };
    st.lastCopy = { items: chosen, target, rootName: x ? x.rootName() : "Photos du téléphone", expert: !!x, job: null };
    openXfer(ids.length);
    try {
      const { jobId } = await call("/transfer", { method: "POST", body, timeout: 30000 });
      st.transfer = { id: jobId, lastId: null, lastFly: 0, speed: 0, lastBytes: 0, lastAt: performance.now() };
      pollTransfer();
    } catch (err) {
      finishXfer({ state: "error", error: err.message && err.kind === "http" ? err.message : "Le pont ne répond plus.", copied: 0, skipped: 0, failed: [] });
    }
  }

  function openXfer(total) {
    const copy = st.lastCopy;
    const xfer = $("xfer");
    xfer.hidden = false;
    $("xfer-hud").hidden = false;
    $("xfer-end").hidden = true;
    $("xfer-end").classList.remove("failed");
    $("xfer-done").textContent = "0";
    $("xfer-total").textContent = total.toLocaleString("fr-FR");
    $("xfer-fill").style.width = "0%";
    $("xfer-key-fill").style.width = "0%";
    $("xfer-current").textContent = "Préparation…";
    $("xfer-eta").textContent = "";
    $("xfer-current-img").removeAttribute("src");
    $("xfer-cancel").disabled = false;
    $("xfer-cancel").textContent = "Arrêter la copie";
    $("xfer-title").textContent = "Copie en cours";
    xfer.classList.remove("repairing");
    $("end-msg").hidden = true;
    $("end-eject").hidden = !copy.target.isKey;
    $("end-open").hidden = false;
    $("end-open-label").textContent = copy.expert ? "Ouvrir le dossier" : "Ouvrir la clé";
    $("end-csv").hidden = !copy.expert;
    document.body.style.overflow = "hidden";
    requestAnimationFrame(layoutArc);
    startSparks();
  }

  async function pollTransfer() {
    const t = st.transfer;
    while (st.transfer === t) {
      let job;
      try {
        job = await call("/transfer/" + t.id, { timeout: 10000 });
      } catch (err) {
        await sleep(800);
        continue; // raté réseau passager : la copie continue côté pont
      }
      renderXfer(job);
      if (job.state !== "running") {
        finishXfer(job);
        return;
      }
      await sleep(350);
    }
  }

  function renderXfer(job) {
    const t = st.transfer;
    // Avant de copier, le pont range la destination (doublons, photos animées…).
    const repairing = job.phase === "repair";
    $("xfer").classList.toggle("repairing", repairing);
    if (repairing) {
      const expertCopy = st.lastCopy && st.lastCopy.expert;
      $("xfer-title").textContent = expertCopy ? "Rangement de la destination" : "On range ta clé…";
      $("xfer-current").textContent = expertCopy
        ? "Doublons, photos animées, copies interrompues…"
        : "On enlève les doublons avant de copier.";
      $("xfer-eta").textContent = "";
      t.lastAt = performance.now(); // le débit ne compte que la copie
      $("xfer-live").textContent = $("xfer-title").textContent;
      return;
    }
    $("xfer-title").textContent = "Copie en cours";
    $("xfer-done").textContent = Math.min(job.done + (job.current ? 1 : 0), job.total).toLocaleString("fr-FR");
    const pct = job.bytesTotal ? (job.bytesDone / job.bytesTotal) * 100 : 100;
    $("xfer-fill").style.width = `${pct}%`;
    $("xfer-key-fill").style.width = `${pct}%`;
    if (job.current) {
      $("xfer-current").textContent = job.current.name;
      if (job.current.id !== t.lastId) {
        t.lastId = job.current.id;
        const now = performance.now();
        const fly = now - t.lastFly > 260;
        if (fly) t.lastFly = now;
        // Vignette préchargée : une vidéo sans vignette ne doit pas afficher d'image cassée.
        const preload = new Image();
        preload.crossOrigin = "anonymous";
        preload.onload = () => {
          $("xfer-current-img").src = preload.src;
          if (fly) launchFlyer(preload.src);
        };
        preload.onerror = () => {
          $("xfer-current-img").removeAttribute("src");
          if (fly) launchFlyer(null);
        };
        preload.src = thumbUrl(job.current.id);
      }
    }
    // Débit lissé pour un temps restant qui ne saute pas dans tous les sens.
    const now = performance.now();
    const dt = (now - t.lastAt) / 1000;
    if (dt > 0.2) {
      const instant = (job.bytesDone - t.lastBytes) / dt;
      t.speed = t.speed ? t.speed * 0.8 + instant * 0.2 : instant;
      t.lastBytes = job.bytesDone;
      t.lastAt = now;
    }
    const left = job.bytesTotal - job.bytesDone;
    if (t.speed > 1024 && left > 0) {
      const secs = left / t.speed;
      $("xfer-eta").textContent = secs < 60 ? "moins d'une minute" : `environ ${Math.ceil(secs / 60)} min`;
    }
    $("xfer-live").textContent = `${job.done} sur ${job.total}`;
  }

  function finishXfer(job) {
    st.transfer = null;
    stopSparks();
    $("xfer").classList.remove("repairing");
    $("xfer-hud").hidden = true;
    const end = $("xfer-end");
    end.hidden = false;
    const failedList = $("end-failed");
    failedList.innerHTML = "";
    const failed = job.failed || [];
    const copy = st.lastCopy || { expert: false, rootName: "Photos du téléphone" };
    copy.job = job;
    const into = copy.expert
      ? (copy.rootName ? `sur la destination, dans « ${copy.rootName} »` : "sur la destination")
      : "sur la clé, dans « Photos du téléphone »";
    const repaired = R.repairSentence(job.repair, copy.expert ? "la destination" : "ta clé", copy.expert);
    if (job.state === "done") {
      $("end-title").textContent = failed.length ? "C'est presque fini" : "C'est fini !";
      const kinds = st.transferKinds || { photos: job.copied, videos: 0 };
      const what = kinds.photos && kinds.videos ? "photos et vidéos" : kinds.videos ? "vidéos" : "photos";
      const parts = [job.copied
        ? (job.copied > 1
          ? `${job.copied.toLocaleString("fr-FR")} ${what} sont maintenant ${into}.`
          : `C'est copié ${into}.`)
        : "Rien de nouveau à copier."];
      if (job.skipped) parts.push(`${job.skipped.toLocaleString("fr-FR")} ${job.skipped > 1 ? "étaient" : "était"} déjà dessus : pas de doublon.`);
      if (repaired) parts.push(repaired);
      $("end-sub").textContent = parts.join(" ");
    } else if (job.state === "cancelled") {
      $("end-title").textContent = "Copie arrêtée";
      $("end-sub").textContent = `${plural(job.copied || 0, "fichier")} copié${job.copied > 1 ? "s" : ""} avant l'arrêt. Tu peux relancer quand tu veux : rien ne sera copié deux fois.${repaired ? " " + repaired : ""}`;
    } else {
      end.classList.add("failed");
      $("end-title").textContent = "La copie s'est arrêtée";
      $("end-sub").textContent = job.error || "Un problème est survenu.";
      $("end-icon").innerHTML = `<use href="#i-alert"/>`;
    }
    if (job.state !== "error") $("end-icon").innerHTML = `<use href="#i-check"/>`;
    if (failed.length) {
      for (const f of failed.slice(0, 5)) {
        const li = document.createElement("li");
        li.textContent = `${f.name} : ${f.error}`;
        failedList.appendChild(li);
      }
      if (failed.length > 5) {
        const li = document.createElement("li");
        li.textContent = `… et ${failed.length - 5} autres.`;
        failedList.appendChild(li);
      }
      failedList.hidden = false;
    } else {
      failedList.hidden = true;
    }
    if (copy.expert && expert) expert.onFinish(job);
    ($("end-eject").hidden ? $("end-open") : $("end-eject")).focus();
  }

  $("xfer-cancel").addEventListener("click", async () => {
    if (!st.transfer) return;
    $("xfer-cancel").disabled = true;
    $("xfer-cancel").textContent = "Arrêt en cours…";
    try { await call(`/transfer/${st.transfer.id}/cancel`, { method: "POST" }); } catch { /* la boucle verra l'état */ }
  });

  $("end-open").addEventListener("click", () => {
    const copy = st.lastCopy;
    if (!copy) return;
    const body = copy.expert ? { ...copy.target.body, rootName: copy.rootName } : copy.target.body;
    call("/open", { method: "POST", body }).catch(() => {});
  });

  $("end-eject").addEventListener("click", async () => {
    const button = $("end-eject");
    const msg = $("end-msg");
    const driveId = st.lastCopy && st.lastCopy.target.body.driveId;
    if (!driveId) return;
    button.disabled = true;
    try {
      const res = await call("/eject", { method: "POST", body: { driveId }, timeout: 15000 });
      msg.textContent = res.message;
      msg.classList.toggle("bad", !res.ok);
      if (res.ok) {
        button.hidden = true;
        $("end-open").hidden = true;
      }
    } catch (err) {
      msg.textContent = err.message && err.kind === "http" ? err.message : "Le pont ne répond plus.";
      msg.classList.add("bad");
    } finally {
      msg.hidden = false;
      button.disabled = false;
    }
  });

  $("end-close").addEventListener("click", () => {
    $("xfer").hidden = true;
    document.body.style.overflow = "";
    // Nouvel inventaire : ce qui vient d'être copié apparaîtra « sur la clé ».
    st.scan = null;
    kick();
  });

  /* ---------- Animation de la copie ---------- */

  function anchors() {
    const scene = $("xfer-scene").getBoundingClientRect();
    const phone = $("xfer-phone").getBoundingClientRect();
    const key = $("xfer-key").querySelector(".xfer-key-body").getBoundingClientRect();
    const a = { x: phone.left + phone.width / 2 - scene.left, y: phone.top + phone.height * 0.42 - scene.top };
    const b = { x: key.left + key.width / 2 - scene.left, y: key.top + key.height / 2 - scene.top };
    // L'arc monte, sans jamais sortir par le haut de l'écran (le sommet d'une
    // courbe de Bézier culmine aux 3/4 de la hauteur de ses points de contrôle).
    const top = Math.min(a.y, b.y);
    const lift = Math.min(Math.max(90, (b.x - a.x) * 0.3), (top - 24) / 0.75);
    return {
      a,
      c1: { x: a.x + (b.x - a.x) * 0.25, y: top - lift },
      c2: { x: a.x + (b.x - a.x) * 0.75, y: top - lift },
      b,
    };
  }

  function layoutArc() {
    if ($("xfer").hidden) return;
    const { a, c1, c2, b } = anchors();
    const d = `M ${a.x} ${a.y} C ${c1.x} ${c1.y}, ${c2.x} ${c2.y}, ${b.x} ${b.y}`;
    $("xfer-path").setAttribute("d", d);
    $("xfer-path-glow").setAttribute("d", d);
  }
  window.addEventListener("resize", layoutArc);

  function bezier(p, t) {
    const u = 1 - t;
    return {
      x: u * u * u * p.a.x + 3 * u * u * t * p.c1.x + 3 * u * t * t * p.c2.x + t * t * t * p.b.x,
      y: u * u * u * p.a.y + 3 * u * u * t * p.c1.y + 3 * u * t * t * p.c2.y + t * t * t * p.b.y,
    };
  }

  function launchFlyer(src) {
    if (REDUCED || $("xfer").hidden) return;
    const path = anchors();
    let img;
    if (src) {
      img = document.createElement("img");
      img.alt = "";
      img.crossOrigin = "anonymous";
      img.src = src;
      img.className = "flyer";
    } else {
      img = document.createElement("span");
      img.className = "flyer flyer-video";
      img.innerHTML = `<svg class="icon" aria-hidden="true"><use href="#i-film"/></svg>`;
    }
    $("xfer-flyers").appendChild(img);
    const half = img.offsetWidth / 2 || 32;
    const frames = [];
    for (let i = 0; i <= 24; i++) {
      const t = i / 24;
      const eased = t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;
      const pos = bezier(path, eased);
      const scale = 1 + Math.sin(eased * Math.PI) * 0.35 - eased * 0.55;
      frames.push({
        transform: `translate(${pos.x - half}px, ${pos.y - half}px) rotate(${(eased - 0.5) * 24}deg) scale(${scale})`,
        opacity: t > 0.92 ? (1 - t) * 12 : Math.min(1, t * 8),
      });
    }
    const anim = img.animate(frames, { duration: 1150, easing: "linear" });
    anim.onfinish = () => {
      img.remove();
      const body = document.querySelector(".xfer-key-body");
      body.classList.add("gulp");
      setTimeout(() => body.classList.remove("gulp"), 220);
    };
  }

  let sparkTimer = 0;
  function startSparks() {
    stopSparks();
    if (REDUCED) return;
    sparkTimer = setInterval(() => {
      if ($("xfer").hidden) return;
      const path = anchors();
      const dot = document.createElement("span");
      dot.className = "spark-dot";
      $("xfer-flyers").appendChild(dot);
      const jitter = (Math.random() - 0.5) * 24;
      const frames = [];
      for (let i = 0; i <= 12; i++) {
        const pos = bezier(path, i / 12);
        frames.push({ transform: `translate(${pos.x - 3}px, ${pos.y - 3 + jitter}px)`, opacity: i === 0 || i === 12 ? 0 : 1 });
      }
      dot.animate(frames, { duration: 900 + Math.random() * 500, easing: "ease-in" }).onfinish = () => dot.remove();
    }, 110);
  }

  function stopSparks() {
    clearInterval(sparkTimer);
    sparkTimer = 0;
  }

  /* ---------- Petite fête quand le pont apparaît ---------- */

  function celebrate(text) {
    const stage = $("phone-stage");
    const burst = document.createElement("div");
    burst.className = "celebrate";
    burst.innerHTML = `<svg class="icon" aria-hidden="true"><use href="#i-check"/></svg><span></span>`;
    burst.querySelector("span").textContent = text;
    stage.appendChild(burst);
    setTimeout(() => burst.remove(), 2600);
  }

  /* ---------- Noyau pour le mode avancé ---------- */

  /** Repart de zéro (changement de mode) : le prochain tour redessine tout. */
  function resetView() {
    resetScan();
    st.phone = null;
    st.driveId = null;
    currentPanel = null;
    telState = null;
    drivesKey = null;
    $("copy-error").hidden = true;
    kick();
  }

  if (typeof window.YayoPhoneExpert === "function") {
    expert = window.YayoPhoneExpert({
      $, R, st, BRIDGE, BridgeError, call, plural, formatBytes, mediaLabel, sleep, thumbUrl,
      dest, eligible, selectable, visibleItems: () => visibleItems(),
      showPanel, panel: () => currentPanel, setRail, kick, reset: resetView,
      startScan, resetScan, showGallery, renderGallery, renderChips, renderSkipped, syncSelection, updateAction,
    });
  }

  /* ---------- Démarrage ---------- */

  // L'onglet devient visible (clic, lien #telephone) : on relance tout de suite.
  new MutationObserver(() => {
    syncScrollMode(); // les autres onglets gardent leur mise en page sur un écran
    if (!$("phone-main").hidden) kick();
  }).observe($("phone-main"), { attributes: true, attributeFilter: ["hidden"] });
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden) kick();
  });
  kick();
})();
