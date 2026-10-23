"use strict";

/* Onglet Téléphone : le site guide Yayo, le Pont Yayo (programme local sur
   127.0.0.1:47863) fait l'accès au téléphone et à la clé. Voir
   docs/superpowers/specs/2026-10-23-transfert-telephone-design.md. */

(() => {
  const $ = (id) => document.getElementById(id);

  const BRIDGE = "http://127.0.0.1:47863";
  const API_REQUIRED = 1;
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
    drive: "On regarde ce qu'il y a déjà sur la clé…",
    phone: "On parcourt les photos du téléphone…",
    compare: "On compare avec la clé pour éviter les doublons…",
  };

  const st = {
    bridge: null,
    token: null,
    wasMissing: false,
    phone: null,
    drives: [],
    driveId: null,
    scan: null,
    scanning: false,
    items: [],
    byId: new Map(),
    selected: new Set(),
    filter: { source: "all", kind: "all", hideOnDrive: false },
    transfer: null,
    timer: 0,
  };

  /* ---------- Petits outils ---------- */

  const plural = (n, word, many) => `${n.toLocaleString("fr-FR")} ${n > 1 ? many || word + "s" : word}`;

  function formatBytes(bytes) {
    if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(1).replace(".", ",")} Go`;
    if (bytes >= 1024 ** 2) return `${Math.round(bytes / 1024 ** 2)} Mo`;
    return `${Math.max(1, Math.round(bytes / 1024))} Ko`;
  }

  function mediaLabel(photos, videos) {
    if (photos && videos) return plural(photos + videos, "photo et vidéo", "photos et vidéos");
    if (videos) return plural(videos, "vidéo");
    return plural(photos, "photo");
  }

  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

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
  }

  function showPontView(view) {
    showPanel("pont");
    for (const id of ["pont-searching", "pont-install", "pont-update", "pont-notwindows"]) $(id).hidden = id !== "pont-" + view;
  }

  /* ---------- Boucle de détection ---------- */

  const tabVisible = () => !$("phone-main").hidden && !document.hidden;

  function schedule(ms) {
    clearTimeout(st.timer);
    st.timer = setTimeout(tick, ms);
  }

  function kick() { schedule(0); }

  async function tick() {
    if (!tabVisible() || st.transfer || st.scanning) return schedule(1200);
    try {
      if (!st.bridge || !st.token) await findBridge();
      else await refreshDevices();
    } catch (err) {
      if (err.kind === "offline") bridgeLost();
      else if (err.kind !== "token") console.warn("pont :", err);
    }
    schedule(st.bridge ? 1500 : 2000);
  }

  async function findBridge() {
    if (!IS_WINDOWS) {
      showPontView("notwindows");
      setRail("pont", "active", "Ordinateur Windows nécessaire");
      return;
    }
    if (!currentPanel) showPontView("searching");
    const status = await call("/status", { timeout: 1500 });
    if (!status || status.app !== "yayo-pont") throw new BridgeError("offline");
    if (status.api < API_REQUIRED) {
      showPontView("update");
      setRail("pont", "active", "Mise à jour nécessaire");
      return;
    }
    st.token = (await call("/session", { method: "POST" })).token;
    st.bridge = status;
    setRail("pont", "done", `Prêt (version ${status.version})`);
    if (st.wasMissing) celebrate("Le pont est installé !");
    st.wasMissing = false;
    await refreshDevices();
  }

  function bridgeLost() {
    const hadBridge = !!st.bridge;
    st.bridge = null;
    st.token = null;
    st.wasMissing = true;
    resetScan();
    showPontView(hadBridge ? "searching" : "install");
    setRail("pont", "active", hadBridge ? "Le pont ne répond plus…" : "À installer");
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

    if (!st.scan || st.scan.driveId !== st.driveId || st.scan.phone !== st.phone.name) {
      startScan();
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

  async function startScan() {
    st.scanning = true;
    resetScan();
    showPanel("scan");
    setRail("galerie", "active", "Inventaire en cours…");
    counters.photos = counters.videos = 0;
    $("scan-photos").textContent = "0";
    $("scan-videos").textContent = "0";
    $("scan-phase").textContent = PHASES.drive;
    const driveId = st.driveId;
    const phone = st.phone.name;
    try {
      const { jobId } = await call("/scan", { method: "POST", body: { driveId } });
      while (true) {
        const job = await call("/scan/" + jobId, { timeout: 30000 });
        countTo("scan-photos", "photos", job.photos);
        countTo("scan-videos", "videos", job.videos);
        $("scan-phase").textContent = PHASES[job.phase] || PHASES.phone;
        if (job.state === "done") {
          st.scan = { id: jobId, driveId, phone };
          loadItems(job.items);
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
    st.items = items.map((item) => ({ ...item, when: item.date ? new Date(item.date) : null }));
    st.byId = new Map(st.items.map((item) => [item.id, item]));
    // Par défaut, tout ce qui n'est pas déjà sur la clé est choisi.
    st.selected = new Set(st.items.filter((item) => !item.onDrive).map((item) => item.id));
  }

  /* ---------- Galerie ---------- */

  const monthFormat = new Intl.DateTimeFormat("fr-FR", { month: "long", year: "numeric" });

  function visibleItems() {
    const { source, kind, hideOnDrive } = st.filter;
    return st.items.filter((item) =>
      (source === "all" || item.source === source)
      && (kind === "all" || item.kind === kind)
      && !(hideOnDrive && item.onDrive));
  }

  function showGallery() {
    const fresh = st.items.filter((item) => !item.onDrive).length;
    const onDrive = st.items.length - fresh;
    setRail("galerie", "active", fresh ? plural(fresh, "nouvelle", "nouvelles") : "Tout est déjà sur la clé");
    renderChips();
    renderGallery();
    showPanel("galerie");
    $("rail-action").hidden = false;
    updateAction();
  }

  function renderChips() {
    const box = $("source-chips");
    box.innerHTML = "";
    for (const [key, label] of SOURCES) {
      const count = key === "all" ? st.items.length : st.items.filter((item) => item.source === key).length;
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

  function renderGallery() {
    const gallery = $("gallery");
    thumbQueue.length = 0;
    thumbObserver.disconnect();
    gallery.innerHTML = "";
    const items = visibleItems();
    $("gallery-empty").hidden = items.length > 0;
    if (!items.length) {
      $("gallery-empty").textContent = st.items.length
        ? "Rien à afficher avec ces filtres."
        : "Aucune photo ni vidéo trouvée sur le téléphone.";
      return;
    }
    const months = new Map();
    for (const item of items) {
      const key = item.when ? `${item.when.getFullYear()}-${String(item.when.getMonth() + 1).padStart(2, "0")}` : "sans-date";
      if (!months.has(key)) months.set(key, []);
      months.get(key).push(item);
    }
    let rank = 0;
    for (const [key, list] of months) {
      const section = document.createElement("section");
      section.className = "month";
      section.dataset.month = key;
      const title = key === "sans-date" ? "Sans date" : capitalize(monthFormat.format(list[0].when));
      section.innerHTML = `
        <div class="month-head"><h4></h4><span></span><button type="button" class="btn-text month-toggle"></button></div>
        <div class="month-grid"></div>`;
      section.querySelector("h4").textContent = title;
      section.querySelector(".month-head span").textContent = plural(list.length, "élément");
      const grid = section.querySelector(".month-grid");
      for (const item of list) grid.appendChild(makeTile(item, rank++));
      gallery.appendChild(section);
      updateMonthToggle(section);
    }
  }

  const capitalize = (s) => s.charAt(0).toUpperCase() + s.slice(1);

  function makeTile(item, rank) {
    const wrap = document.createElement("div");
    wrap.className = "tile-wrap";
    const tile = document.createElement("div");
    tile.className = "tile" + (item.onDrive ? " on-drive" : "");
    tile.dataset.id = item.id;
    tile.tabIndex = 0;
    tile.setAttribute("role", "checkbox");
    tile.setAttribute("aria-checked", String(st.selected.has(item.id)));
    tile.setAttribute("aria-pressed", String(st.selected.has(item.id)));
    if (item.onDrive) tile.setAttribute("aria-disabled", "true");
    tile.setAttribute("aria-label", `${item.name}${item.onDrive ? ", déjà sur la clé" : ""}`);
    if (rank < 40) tile.style.animationDelay = `${rank * 18}ms`;
    else tile.style.animation = "none";
    tile.innerHTML = `
      <span class="tile-icon" aria-hidden="true"><svg class="icon"><use href="#i-${item.kind === "video" ? "film" : "image"}"/></svg></span>
      <img alt="" crossorigin="anonymous" decoding="async">
      <span class="tile-check" aria-hidden="true"><svg class="icon"><use href="#i-tick"/></svg></span>
      <span class="tile-badge" aria-hidden="true"></span>`;
    const badge = tile.querySelector(".tile-badge");
    if (item.onDrive) badge.insertAdjacentHTML("beforeend", `<span class="badge-ok"><svg class="icon"><use href="#i-tick"/></svg>Sur la clé</span>`);
    else badge.insertAdjacentHTML("beforeend", "<span hidden></span>");
    if (item.kind === "video") badge.insertAdjacentHTML("beforeend", `<span><svg class="icon"><use href="#i-play"/></svg>Vidéo</span>`);
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
  }, { root: null, rootMargin: "400px 0px" });

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
    const ids = [...section.querySelectorAll(".tile:not(.on-drive)")].map((tile) => tile.dataset.id);
    const button = section.querySelector(".month-toggle");
    button.hidden = ids.length === 0;
    const all = ids.length > 0 && ids.every((id) => st.selected.has(id));
    button.textContent = all ? "Retirer le mois" : "Choisir tout le mois";
    button.dataset.all = String(all);
  }

  function setSelected(id, on) {
    const item = st.byId.get(id);
    if (!item || item.onDrive) return;
    if (on) st.selected.add(id);
    else st.selected.delete(id);
    const tile = document.querySelector(`.tile[data-id="${CSS.escape(id)}"]`);
    if (tile) {
      tile.setAttribute("aria-checked", String(on));
      tile.setAttribute("aria-pressed", String(on));
    }
  }

  $("gallery").addEventListener("click", (event) => {
    const zoom = event.target.closest(".tile-zoom");
    if (zoom) return openViewer(zoom.dataset.id);
    const toggle = event.target.closest(".month-toggle");
    if (toggle) {
      const section = toggle.closest(".month");
      const on = toggle.dataset.all !== "true";
      for (const tile of section.querySelectorAll(".tile:not(.on-drive)")) setSelected(tile.dataset.id, on);
      updateMonthToggle(section);
      updateAction();
      return;
    }
    const tile = event.target.closest(".tile");
    if (!tile || tile.classList.contains("on-drive")) return;
    setSelected(tile.dataset.id, !st.selected.has(tile.dataset.id));
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

  $("select-all").addEventListener("click", () => {
    for (const item of visibleItems()) setSelected(item.id, true);
    document.querySelectorAll(".month").forEach(updateMonthToggle);
    updateAction();
  });

  $("select-none").addEventListener("click", () => {
    for (const id of [...st.selected]) setSelected(id, false);
    document.querySelectorAll(".month").forEach(updateMonthToggle);
    updateAction();
  });

  function updateAction() {
    const drive = st.drives.find((x) => x.id === st.driveId);
    if (!drive) return;
    let bytes = 0, photos = 0, videos = 0;
    for (const id of st.selected) {
      const item = st.byId.get(id);
      if (!item) continue;
      bytes += item.size;
      if (item.kind === "video") videos++;
      else photos++;
    }
    const count = photos + videos;
    $("sel-count").textContent = count ? `${count.toLocaleString("fr-FR")} ${count > 1 ? "choisies" : "choisie"}` : "Aucune photo choisie";
    $("sel-size").textContent = count ? formatBytes(bytes) : "";
    const used = drive.totalBytes - drive.freeBytes;
    const usedPct = drive.totalBytes ? (used / drive.totalBytes) * 100 : 0;
    const selPct = drive.totalBytes ? Math.min(100 - usedPct, (bytes / drive.totalBytes) * 100) : 0;
    $("space-used").style.width = `${usedPct}%`;
    $("space-sel").style.left = `${usedPct}%`;
    $("space-sel").style.width = `${selPct}%`;
    const over = bytes > drive.freeBytes;
    document.querySelector(".space-gauge").classList.toggle("over", over);
    $("space-text").textContent = over
      ? `Pas assez de place : il manque ${formatBytes(bytes - drive.freeBytes)} sur la clé.`
      : count
        ? `Il restera ${formatBytes(drive.freeBytes - bytes)} de libre sur la clé.`
        : `${formatBytes(drive.freeBytes)} de libre sur la clé.`;
    $("copy-btn").disabled = !count || over;
    const anyNew = st.items.some((item) => !item.onDrive);
    $("copy-label").textContent = count
      ? `Copier ${mediaLabel(photos, videos)}`
      : anyNew ? "Choisis des photos" : "Tout est déjà sur la clé";
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
    const chosen = st.items.filter((item) => st.selected.has(item.id));
    const ids = chosen.map((item) => item.id);
    if (!ids.length) return;
    st.transferKinds = {
      photos: chosen.filter((item) => item.kind !== "video").length,
      videos: chosen.filter((item) => item.kind === "video").length,
    };
    const drive = st.drives.find((x) => x.id === st.driveId);
    openXfer(ids.length);
    try {
      const { jobId } = await call("/transfer", { method: "POST", body: { driveId: drive.id, ids } });
      st.transfer = { id: jobId, driveId: drive.id, lastId: null, lastFly: 0, speed: 0, lastBytes: 0, lastAt: performance.now() };
      pollTransfer();
    } catch (err) {
      finishXfer({ state: "error", error: err.message && err.kind === "http" ? err.message : "Le pont ne répond plus.", copied: 0, skipped: 0, failed: [] });
    }
  }

  function openXfer(total) {
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
    $("end-msg").hidden = true;
    $("end-eject").hidden = false;
    $("end-open").hidden = false;
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
    $("xfer-hud").hidden = true;
    const end = $("xfer-end");
    end.hidden = false;
    const failedList = $("end-failed");
    failedList.innerHTML = "";
    const failed = job.failed || [];
    if (job.state === "done") {
      $("end-title").textContent = failed.length ? "C'est presque fini" : "C'est fini !";
      const kinds = st.transferKinds || { photos: job.copied, videos: 0 };
      const what = kinds.photos && kinds.videos ? "photos et vidéos" : kinds.videos ? "vidéos" : "photos";
      const parts = [job.copied
        ? (job.copied > 1
          ? `${job.copied.toLocaleString("fr-FR")} ${what} sont maintenant sur la clé, dans « Photos du téléphone ».`
          : `C'est copié sur la clé, dans « Photos du téléphone ».`)
        : "Rien de nouveau à copier."];
      if (job.skipped) parts.push(`${job.skipped.toLocaleString("fr-FR")} ${job.skipped > 1 ? "étaient" : "était"} déjà dessus : pas de doublon.`);
      $("end-sub").textContent = parts.join(" ");
    } else if (job.state === "cancelled") {
      $("end-title").textContent = "Copie arrêtée";
      $("end-sub").textContent = `${plural(job.copied || 0, "fichier")} copié${job.copied > 1 ? "s" : ""} avant l'arrêt. Tu peux relancer quand tu veux : rien ne sera copié deux fois.`;
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
    $("end-eject").focus();
  }

  $("xfer-cancel").addEventListener("click", async () => {
    if (!st.transfer) return;
    $("xfer-cancel").disabled = true;
    $("xfer-cancel").textContent = "Arrêt en cours…";
    try { await call(`/transfer/${st.transfer.id}/cancel`, { method: "POST" }); } catch { /* la boucle verra l'état */ }
  });

  $("end-open").addEventListener("click", () => {
    call("/open", { method: "POST", body: { driveId: st.driveId } }).catch(() => {});
  });

  $("end-eject").addEventListener("click", async () => {
    const button = $("end-eject");
    const msg = $("end-msg");
    button.disabled = true;
    try {
      const res = await call("/eject", { method: "POST", body: { driveId: st.driveId }, timeout: 15000 });
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

  /* ---------- Démarrage ---------- */

  // L'onglet devient visible (clic, lien #telephone) : on relance tout de suite.
  new MutationObserver(() => {
    if (!$("phone-main").hidden) kick();
  }).observe($("phone-main"), { attributes: true, attributeFilter: ["hidden"] });
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden) kick();
  });
  kick();
})();
