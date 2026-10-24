"use strict";

/* Mode « Ne pas toucher » : le mode avancé de l'onglet Téléphone, pour Louis.
   phone.js appelle window.YayoPhoneExpert(noyau) au démarrage ; tant que
   l'interrupteur est éteint, ce module ne change rien au parcours de Yayo.
   Voir docs/superpowers/specs/2026-10-24-telephone-v2-design.md, partie 2. */

window.YayoPhoneExpert = (core) => {
  const { $, R, st, call, formatBytes } = core;

  const KEY_ON = "yayo.telephone.avance";
  const KEY_CFG = "yayo.telephone.reglages";
  const COPY_DEFAULTS = { stripMotion: true, repair: true, dedupe: true, conflict: "rename", verify: false, keepDates: true };
  const DEFAULTS = {
    dest: null, // { type: "drive", id } | { type: "folder", path }
    recents: [],
    rootName: "Photos du téléphone",
    deep: false,
    layout: R.DEFAULT_LAYOUT,
    fileName: "{nom}",
    level: "ville",
    minCount: 10,
    inheritHours: 3,
    inc: { hidden: false, live: false, dupes: false },
    period: { from: "", to: "" },
    copy: COPY_DEFAULTS,
    panel: "galerie",
  };
  const WORKSPACE = new Set(["galerie", "xp-rangement", "xp-lieux", "xp-options", "xp-stats"]);
  const GEO_URL = "/api/geo/reverse";
  const GEO_BATCH = 5000;

  /* ---------- Réglages mémorisés ---------- */

  const store = {
    get(key) {
      try { return localStorage.getItem(key); } catch { return null; }
    },
    set(key, value) {
      try { localStorage.setItem(key, value); } catch { /* navigation privée : tant pis */ }
    },
  };

  function loadCfg() {
    let saved = {};
    try { saved = JSON.parse(store.get(KEY_CFG) || "{}") || {}; } catch { saved = {}; }
    return {
      ...DEFAULTS,
      ...saved,
      inc: { ...DEFAULTS.inc, ...(saved.inc || {}) },
      period: { ...DEFAULTS.period, ...(saved.period || {}) },
      copy: { ...COPY_DEFAULTS, ...(saved.copy || {}) },
      recents: Array.isArray(saved.recents) ? saved.recents.filter((p) => typeof p === "string").slice(0, 8) : [],
    };
  }

  const cfg = loadCfg();
  const save = () => store.set(KEY_CFG, JSON.stringify(cfg));

  const xs = {
    phones: [],
    phoneId: null,
    usbPhoneSeen: false,
    geo: new Map(), // pointKey → lieu | null : cache de la session, partagé entre analyses
    geoScan: null, // analyse dont tous les points sont géocodés
    geoJob: null,
    places: null, // résultat de R.computePlaces
    copyPlaces: null,
    picking: false,
    planSig: null,
    lastSig: null,
    keys: {},
    lastField: null,
  };

  const api = { active: false };

  /* ---------- Petits outils ---------- */

  const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const nf = (n) => Number(n || 0).toLocaleString("fr-FR");
  const pad = (n) => String(n).padStart(2, "0");
  const dayOf = (d) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  const dateTimeFormat = new Intl.DateTimeFormat("fr-FR", { dateStyle: "medium", timeStyle: "short" });

  function setStatus(id, text, bad) {
    const el = $(id);
    el.textContent = text || "";
    el.classList.toggle("bad", !!bad);
  }

  /** Chemin local absolu sur une lettre de lecteur (les guillemets d'un « Copier en tant que chemin » sont retirés). */
  function normalizePath(raw) {
    let p = String(raw || "").trim().replace(/^"(.*)"$/, "$1").replace(/\//g, "\\");
    if (/^[A-Za-z]:$/.test(p)) p += "\\";
    if (!/^[A-Za-z]:\\/.test(p)) return null;
    if (p.length > 3) p = p.replace(/\\+$/, "");
    return p.charAt(0).toUpperCase() + p.slice(1);
  }

  /* ---------- Destination ---------- */

  function destination() {
    const d = cfg.dest;
    if (!d) return null;
    if (d.type === "drive") {
      const drive = st.drives.find((x) => x.id === d.id);
      if (!drive) return null;
      return {
        body: { driveId: drive.id },
        key: "d:" + drive.id,
        label: `${drive.label || "Disque"} (${drive.letter})`,
        free: drive.freeBytes,
        total: drive.totalBytes,
        isKey: drive.kind !== "fixed",
      };
    }
    if (d.type === "folder" && d.path) {
      // Espace libre connu seulement si le dossier est sur un disque listé par le pont.
      const letter = d.path.slice(0, 2).toUpperCase();
      const drive = st.drives.find((x) => String(x.letter || "").toUpperCase().startsWith(letter));
      return {
        body: { folder: d.path },
        key: "f:" + d.path.toLowerCase(),
        label: d.path,
        free: drive ? drive.freeBytes : null,
        total: drive ? drive.totalBytes : null,
        isKey: false,
      };
    }
    return null;
  }

  const scanKey = (target) => ["x", xs.phoneId, target.key, cfg.deep ? 1 : 0, cfg.rootName].join("|");
  const scanBody = (target) => ({
    ...target.body,
    phoneId: xs.phoneId || undefined,
    // Le pont range la destination dès l'analyse (doublons, photos animées) si on le laisse faire.
    options: { deep: cfg.deep, rootName: cfg.rootName, repair: cfg.copy.repair, stripMotion: cfg.copy.stripMotion },
  });

  /* ---------- Boucle (remplace la détection du mode normal) ---------- */

  async function refresh() {
    const d = await call("/devices?all=1" + (xs.phoneId ? "&phoneId=" + encodeURIComponent(xs.phoneId) : ""));
    xs.phones = Array.isArray(d.phones) ? d.phones : d.phone ? [{ id: "", ...d.phone }] : [];
    xs.usbPhoneSeen = !!d.usbPhoneSeen;
    st.drives = d.drives || [];
    let phone = xs.phones.find((p) => p.id === xs.phoneId);
    if (!phone) phone = xs.phones.find((p) => p.state === "ready") || xs.phones[0] || null;
    xs.phoneId = phone ? phone.id : null;
    st.phone = phone;
    if (!cfg.dest) {
      const usb = st.drives.find((x) => x.kind !== "fixed");
      if (usb) {
        cfg.dest = { type: "drive", id: usb.id };
        save();
      }
    }
    $("xp-bridge").textContent = st.bridge ? `pont ${st.bridge.version} · api ${st.bridge.api}` : "—";
    renderPhones();
    renderDrives();

    const target = destination();
    if (!phone || phone.state !== "ready" || !target) {
      if (st.scan) core.resetScan();
      showWait(phone, target);
      return;
    }
    const key = scanKey(target);
    if (!st.scan || st.scan.key !== key) {
      if (cfg.dest.type === "folder") addRecent(cfg.dest.path);
      $("xp-scan-note").textContent = "Analyse en cours…";
      core.startScan(scanBody(target), key);
      return;
    }
    if (!WORKSPACE.has(core.panel())) openPanel(cfg.panel);
    core.updateAction();
  }

  function showWait(phone, target) {
    let title;
    let text;
    if (!phone) {
      title = "Aucun téléphone";
      text = xs.usbPhoneSeen
        ? "Un téléphone est branché en simple recharge : notification USB → « Transfert de fichiers »."
        : "Branche un téléphone (Android en « Transfert de fichiers », iPhone déverrouillé).";
    } else if (phone.state !== "ready") {
      title = `${phone.name} : verrouillé`;
      text = "Stockage invisible : déverrouille le téléphone et touche « Autoriser ».";
    } else if (!cfg.dest) {
      title = "Choisis une destination";
      text = "Une clé, un disque ou n'importe quel dossier du PC, dans la colonne de gauche.";
    } else {
      title = "Destination absente";
      text = "Le disque choisi n'est plus branché : rebranche-le ou choisis une autre destination.";
    }
    $("xp-wait-title").textContent = title;
    $("xp-wait-text").textContent = text;
    $("xp-scan-note").textContent = "";
    core.showPanel("xp-wait");
  }

  /* ---------- Console : téléphones et destinations ---------- */

  function pickButton({ pressed, icon, title, meta, warn, onClick }) {
    const b = document.createElement("button");
    b.type = "button";
    b.className = "xp-pick" + (warn ? " warn" : "");
    b.setAttribute("aria-pressed", String(!!pressed));
    b.innerHTML = `<svg class="icon" aria-hidden="true"><use href="#${icon}"/></svg><span class="xp-pick-text"><strong></strong><small></small></span>`;
    b.querySelector("strong").textContent = title;
    b.querySelector("small").textContent = meta;
    b.addEventListener("click", onClick);
    return b;
  }

  function emptyNote(text) {
    const p = document.createElement("p");
    p.className = "xp-note";
    p.textContent = text;
    return p;
  }

  // Le sondage repasse toutes les 1,5 s : on ne redessine que ce qui a changé.
  function changed(name, value) {
    const key = JSON.stringify(value);
    if (xs.keys[name] === key) return false;
    xs.keys[name] = key;
    return true;
  }

  function renderPhones() {
    if (!changed("phones", [xs.phones, xs.phoneId, xs.usbPhoneSeen])) return;
    const box = $("xp-phones");
    box.innerHTML = "";
    if (!xs.phones.length) {
      box.appendChild(emptyNote(xs.usbPhoneSeen ? "Téléphone en simple recharge." : "Aucun téléphone branché."));
      return;
    }
    for (const p of xs.phones) {
      box.appendChild(pickButton({
        pressed: p.id === xs.phoneId,
        icon: "i-phone",
        title: p.name || "Téléphone",
        meta: `${p.manufacturer || "?"} · ${p.state === "ready" ? "prêt" : "verrouillé"}`,
        warn: p.state !== "ready",
        onClick: () => {
          if (st.scanning) return;
          xs.phoneId = p.id;
          core.kick();
        },
      }));
    }
  }

  function renderDrives() {
    if (!changed("drives", [st.drives.map((x) => [x.id, x.label, x.letter, x.freeBytes, x.kind, x.fs]), cfg.dest])) return;
    const box = $("xp-drives");
    box.innerHTML = "";
    for (const drive of st.drives) {
      const fixed = drive.kind === "fixed";
      box.appendChild(pickButton({
        pressed: cfg.dest && cfg.dest.type === "drive" && cfg.dest.id === drive.id,
        icon: fixed ? "i-archive" : "i-usb",
        title: `${drive.label || (fixed ? "Disque" : "Clé USB")} (${drive.letter})`,
        meta: `${fixed ? "disque" : "USB"}${drive.fs ? " · " + drive.fs : ""} · ${formatBytes(drive.freeBytes)} libres / ${formatBytes(drive.totalBytes)}`,
        onClick: () => setDest({ type: "drive", id: drive.id }),
      }));
    }
    if (cfg.dest && cfg.dest.type === "folder") {
      const target = destination();
      box.appendChild(pickButton({
        pressed: true,
        icon: "i-folder",
        title: cfg.dest.path,
        meta: `dossier du PC${target && target.free != null ? ` · ${formatBytes(target.free)} libres` : ""}`,
        onClick: () => {},
      }));
    }
    if (!box.children.length) box.appendChild(emptyNote("Aucune clé ni disque : choisis un dossier."));
  }

  function setDest(dest) {
    if (st.scanning) return;
    cfg.dest = dest;
    save();
    destNote("");
    core.kick();
  }

  function destNote(text, bad) {
    const note = $("xp-dest-note");
    note.textContent = text || "";
    note.hidden = !text;
    note.classList.toggle("bad", !!bad);
  }

  function useFolder(raw) {
    const path = normalizePath(raw);
    if (!path) {
      destNote("Il faut un chemin local complet, par exemple D:\\Photos.", true);
      return;
    }
    $("xp-folder").value = path;
    addRecent(path);
    setDest({ type: "folder", path });
  }

  function addRecent(path) {
    cfg.recents = [path, ...cfg.recents.filter((p) => p.toLowerCase() !== path.toLowerCase())].slice(0, 8);
    save();
    renderRecents();
  }

  function renderRecents() {
    const list = $("xp-recents");
    list.innerHTML = "";
    const chips = $("xp-recent-list");
    chips.innerHTML = "";
    for (const path of cfg.recents) {
      const opt = document.createElement("option");
      opt.value = path;
      list.appendChild(opt);
      const chip = document.createElement("button");
      chip.type = "button";
      chip.className = "xp-recent";
      chip.textContent = path;
      chip.title = `Utiliser ${path}`;
      chip.addEventListener("click", () => useFolder(path));
      chips.appendChild(chip);
    }
  }

  $("xp-folder-form").addEventListener("submit", (event) => {
    event.preventDefault();
    useFolder($("xp-folder").value);
  });

  $("xp-pick").addEventListener("click", async () => {
    if (xs.picking) return;
    xs.picking = true;
    $("xp-pick").disabled = true;
    $("xp-pick-label").textContent = "Fenêtre ouverte sur le PC…";
    destNote("Le sélecteur de dossier de Windows est ouvert (il peut être derrière le navigateur).");
    try {
      const res = await call("/pick-folder", { method: "POST", timeout: 10 * 60e3 });
      if (res && res.path) useFolder(res.path);
      else destNote("Choix annulé.");
    } catch (err) {
      destNote(err.kind === "http" ? err.message : "Pas de réponse du pont.", true);
    } finally {
      xs.picking = false;
      $("xp-pick").disabled = false;
      $("xp-pick-label").textContent = "Choisir un dossier…";
    }
  });

  $("xp-root").addEventListener("change", () => {
    cfg.rootName = $("xp-root").value.trim();
    save();
    renderRangement();
    core.kick();
  });

  $("xp-deep").addEventListener("change", () => {
    cfg.deep = $("xp-deep").checked;
    save();
    core.kick();
  });

  $("xp-rescan").addEventListener("click", () => {
    if (st.scanning || st.transfer) return;
    st.scan = null;
    core.kick();
  });

  /* ---------- Volets ---------- */

  function openPanel(name) {
    if (!WORKSPACE.has(name)) name = "galerie";
    cfg.panel = name;
    save();
    for (const tab of document.querySelectorAll(".xp-tab")) tab.setAttribute("aria-pressed", String(tab.dataset.panel === name));
    core.showPanel(name);
    if (name === "xp-rangement") renderRangement();
    else if (name === "xp-lieux") renderLieux();
    else if (name === "xp-options") renderOptions();
    else if (name === "xp-stats") renderStats();
  }

  $("xp-tabs").addEventListener("click", (event) => {
    const tab = event.target.closest(".xp-tab");
    if (tab && st.scan) openPanel(tab.dataset.panel);
  });

  /* ---------- Galerie : filtres et marques ---------- */

  const eligible = (item) => (cfg.inc.hidden || !item.hidden) && (cfg.inc.live || !item.live) && (cfg.inc.dupes || !item.duplicateOf);

  function inPeriod(item) {
    const { from, to } = cfg.period;
    if (!from && !to) return true;
    if (!item.when) return false;
    const day = dayOf(item.when);
    return (!from || day >= from) && (!to || day <= to);
  }

  function flagCounts() {
    let hidden = 0, live = 0, dupes = 0, motion = 0, gps = 0;
    for (const item of st.items) {
      if (item.hidden) hidden++;
      if (item.live) live++;
      if (item.duplicateOf) dupes++;
      if (item.motion) motion++;
      if (R.hasGps(item)) gps++;
    }
    return { hidden, live, dupes, motion, gps };
  }

  function excludedNote() {
    let n = 0;
    const why = { hidden: 0, live: 0, dupes: 0 };
    for (const item of st.items) {
      if (eligible(item)) continue;
      n++;
      if (item.hidden && !cfg.inc.hidden) why.hidden++;
      else if (item.live && !cfg.inc.live) why.live++;
      else why.dupes++;
    }
    if (!n) return "";
    const parts = [];
    if (why.hidden) parts.push(`${nf(why.hidden)} masqués`);
    if (why.live) parts.push(`${nf(why.live)} clips de photos animées`);
    if (why.dupes) parts.push(`${nf(why.dupes)} doublons du téléphone`);
    return `Exclus : ${parts.join(" · ")} (cases « Inclure »).`;
  }

  function tileFlags(item) {
    const flags = [];
    if (item.hidden) flags.push(["hidden", "Masqué"]);
    if (item.live) flags.push(["live", "Clip"]);
    if (item.duplicateOf) flags.push(["dupe", "Doublon"]);
    if (item.motion) flags.push(["motion", "Animée"]);
    return flags.map(([cls, label]) => `<span class="flag flag-${cls}">${label}</span>`).join("");
  }

  function tileTitle(item) {
    const lines = [item.name];
    if (item.path) lines.push(item.path);
    const raw = item.exifDate || item.date;
    if (item.when) lines.push(`${dateTimeFormat.format(item.when)}${item.exifDate ? " (EXIF)" : ""}`);
    else if (!raw) lines.push("Date inconnue");
    lines.push(formatBytes(item.size));
    if (R.hasGps(item)) lines.push(`GPS ${item.lat.toFixed(4)}, ${item.lon.toFixed(4)}`);
    const place = xs.places && xs.places.places.get(item.id);
    if (place) lines.push(`Lieu : ${place.lieu}`);
    if (item.duplicateOf) {
      const twin = st.byId.get(item.duplicateOf);
      lines.push(`Doublon de ${twin ? twin.name : item.duplicateOf}`);
    }
    if (item.hidden) lines.push("Dossier masqué (.nomedia ou Sent de WhatsApp)");
    if (item.live) lines.push("Clip de photo animée");
    if (item.onDrive) lines.push("Déjà sur la destination");
    return lines.join("\n");
  }

  function refreshTileTitles() {
    for (const tile of document.querySelectorAll("#gallery .tile")) {
      const item = st.byId.get(tile.dataset.id);
      if (item) tile.title = tileTitle(item);
    }
  }

  function renderFilters() {
    const c = flagCounts();
    $("xp-n-hidden").textContent = nf(c.hidden);
    $("xp-n-live").textContent = nf(c.live);
    $("xp-n-dupes").textContent = nf(c.dupes);
    $("xp-inc-hidden").checked = cfg.inc.hidden;
    $("xp-inc-live").checked = cfg.inc.live;
    $("xp-inc-dupes").checked = cfg.inc.dupes;
    $("xp-from").value = cfg.period.from;
    $("xp-to").value = cfg.period.to;
  }

  /** Après un changement de filtre ou d'option : sélection élaguée, galerie redessinée. */
  function regallery() {
    for (const id of [...st.selected]) {
      const item = st.byId.get(id);
      if (!item || !core.selectable(item)) st.selected.delete(id);
    }
    if (xs.places && xs.geoScan === (st.scan && st.scan.id)) recomputePlaces();
    core.renderChips();
    core.renderGallery();
    core.updateAction();
  }

  for (const [id, key] of [["xp-inc-hidden", "hidden"], ["xp-inc-live", "live"], ["xp-inc-dupes", "dupes"]]) {
    $(id).addEventListener("change", () => {
      cfg.inc[key] = $(id).checked;
      save();
      regallery();
    });
  }
  for (const [id, key] of [["xp-from", "from"], ["xp-to", "to"]]) {
    $(id).addEventListener("change", () => {
      cfg.period[key] = $(id).value;
      save();
      core.renderGallery();
    });
  }

  /* ---------- Rangement : modèles, exemples, aperçu ---------- */

  function initRangement() {
    const presets = $("xp-presets");
    for (const p of R.PRESETS) {
      const b = document.createElement("button");
      b.type = "button";
      b.className = "xp-preset";
      b.dataset.layout = p.layout;
      b.innerHTML = `<strong></strong><small class="xp-mono"></small>`;
      b.querySelector("strong").textContent = p.label;
      b.querySelector("small").textContent = p.layout || "(racine)";
      b.addEventListener("click", () => {
        $("xp-layout").value = p.layout;
        setTemplate("layout", p.layout);
      });
      presets.appendChild(b);
    }
    const tokens = $("xp-tokens");
    for (const token of R.TOKENS) {
      const b = document.createElement("button");
      b.type = "button";
      b.className = "xp-token xp-mono";
      b.textContent = `{${token}}`;
      b.addEventListener("mousedown", (event) => event.preventDefault()); // garde le curseur dans le champ
      b.addEventListener("click", () => insertToken(`{${token}}`));
      tokens.appendChild(b);
    }
    for (const [id, key] of [["xp-layout", "layout"], ["xp-filename", "fileName"]]) {
      const input = $(id);
      input.addEventListener("focus", () => { xs.lastField = id; });
      input.addEventListener("input", () => setTemplate(key, input.value));
    }
  }

  function insertToken(text) {
    const input = $(xs.lastField || "xp-layout");
    const start = input.selectionStart ?? input.value.length;
    const end = input.selectionEnd ?? input.value.length;
    input.setRangeText(text, start, end, "end");
    input.focus();
    setTemplate(input.id === "xp-layout" ? "layout" : "fileName", input.value);
  }

  function setTemplate(key, value) {
    cfg[key] = value;
    save();
    renderRangement(true);
  }

  function renderRangement(keepInputs) {
    if (!keepInputs) {
      $("xp-layout").value = cfg.layout;
      $("xp-filename").value = cfg.fileName;
    }
    for (const b of $("xp-presets").children) b.setAttribute("aria-pressed", String(b.dataset.layout === cfg.layout));
    const warn = $("xp-place-warn");
    if (R.usesPlaces(cfg.layout, cfg.fileName)) {
      warn.hidden = false;
      warn.textContent = !st.scan || !st.scan.deep
        ? "Le modèle utilise un lieu : lance l'analyse approfondie, sinon tout ira dans « Lieu inconnu »."
        : xs.places
          ? "Lieux calculés (volet Lieux) : ils seront envoyés au pont avec la copie."
          : "Les lieux seront calculés avant la copie (ou maintenant, volet Lieux).";
    } else {
      warn.hidden = true;
    }
    renderExamples();
    renderPlanState();
  }

  function selectedItems(limit) {
    const out = [];
    for (const item of st.items) {
      if (!st.selected.has(item.id)) continue;
      out.push(item);
      if (limit && out.length >= limit) break;
    }
    return out;
  }

  function renderExamples() {
    const list = $("xp-examples");
    list.innerHTML = "";
    const sample = selectedItems(6);
    if (!sample.length) {
      list.innerHTML = `<li class="xp-note">Aucun fichier choisi.</li>`;
      return;
    }
    const places = xs.places && xs.places.places;
    for (const item of sample) {
      const li = document.createElement("li");
      li.textContent = R.renderPath({ layout: cfg.layout, fileName: cfg.fileName, rootName: cfg.rootName }, item, places && places.get(item.id));
      list.appendChild(li);
    }
  }

  const planSig = () => JSON.stringify([st.scan && st.scan.id, st.selected.size, cfg.layout, cfg.fileName, cfg.rootName, cfg.copy, destination() && destination().key, !!xs.places]);

  function renderPlanState() {
    if (!xs.planSig) return;
    if (xs.planSig !== planSig()) setStatus("xp-plan-status", "Aperçu périmé : la sélection ou les réglages ont changé, recalcule-le.");
  }

  async function runPlan() {
    const ids = selectedItems().map((item) => item.id);
    if (!ids.length) {
      setStatus("xp-plan-status", "Choisis d'abord des fichiers dans la galerie.", true);
      return;
    }
    const button = $("xp-plan-btn");
    button.disabled = true;
    setStatus("xp-plan-status", "Calcul de l'aperçu…");
    $("xp-plan-tree").innerHTML = "";
    try {
      const body = await transferBody(ids);
      const plan = await call("/plan", { method: "POST", body, timeout: 120000 });
      xs.planSig = planSig();
      const folders = plan.folders || [];
      setStatus("xp-plan-status", `${nf(plan.toCopy)} à copier · ${nf(plan.skipped)} ignorés · ${formatBytes(plan.bytes || 0)} · ${nf(folders.length)} dossiers`);
      renderTree(R.buildTree(folders));
    } catch (err) {
      xs.planSig = null;
      const msg = err.kind === "http" && err.status === 404 && /route/i.test(err.message)
        ? "Ce pont ne connaît pas /plan (pont 1.1.0 nécessaire)."
        : err.kind === "offline" ? "Le pont ne répond plus." : err.message;
      setStatus("xp-plan-status", msg, true);
    } finally {
      button.disabled = false;
    }
  }

  function renderTree(tree) {
    const box = $("xp-plan-tree");
    const row = (node) => `<span class="xp-tree-row"><svg class="icon" aria-hidden="true"><use href="#i-folder"/></svg>`
      + `<span class="xp-tree-name">${esc(node.name)}</span><span class="xp-tree-n">${nf(node.count)}</span>`
      + `<span class="xp-tree-size">${formatBytes(node.bytes)}</span></span>`;
    const walk = (node, depth) => {
      if (!node.children.length) return `<li>${row(node)}</li>`;
      const open = depth < 2 ? " open" : "";
      return `<li><details${open}><summary>${row(node)}</summary><ul>${node.children.map((c) => walk(c, depth + 1)).join("")}</ul></details></li>`;
    };
    // Sans dossier racine ni modèle, tout va directement dans la destination (chemin vide).
    const top = tree.children.length ? tree.children
      : tree.count ? [{ ...tree, name: "(directement dans la destination)" }] : [];
    box.innerHTML = top.length
      ? `<ul class="xp-tree-root">${top.map((c) => walk(c, 0)).join("")}</ul>`
      : `<p class="xp-note">Rien à écrire : tout est déjà sur la destination.</p>`;
  }

  $("xp-plan-btn").addEventListener("click", runPlan);
  $("xp-plan-quick").addEventListener("click", () => {
    if (!st.scan) return;
    openPanel("xp-rangement");
    runPlan();
  });

  /* ---------- Lieux ---------- */

  class GeoError extends Error {}

  /** Un paquet de points → lieux, par le serveur du site (géocodage hors ligne, GeoNames). */
  async function reverse(points) {
    const controller = new AbortController();
    const abort = setTimeout(() => controller.abort(), 180000);
    const slow = setTimeout(() => setStatus("xp-geo-status", "Le serveur prépare ses données de lieux (la première fois, ça peut prendre une minute)…"), 6000);
    let resp;
    try {
      resp = await fetch(GEO_URL, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ points }),
        signal: controller.signal,
      });
    } catch {
      throw new GeoError("Le serveur du site ne répond pas.");
    } finally {
      clearTimeout(abort);
      clearTimeout(slow);
    }
    let data = null;
    try { data = await resp.json(); } catch { /* corps vide ou HTML */ }
    const detail = data && (typeof data.detail === "string" ? data.detail : data.error);
    if (resp.status === 404) throw new GeoError("Le serveur du site ne sait pas encore trouver les lieux (POST /api/geo/reverse absent).");
    if (resp.status === 503) throw new GeoError(detail || "Les données de lieux sont indisponibles sur le serveur pour l'instant.");
    if (!resp.ok) throw new GeoError(detail || `Le serveur a refusé la demande (${resp.status}).`);
    if (!data || !Array.isArray(data.places) || data.places.length !== points.length) throw new GeoError("Réponse inattendue du serveur.");
    return data.places;
  }

  /** Géocode les points de l'analyse en cours (cache compris) puis calcule les lieux. */
  function locate() {
    if (xs.geoJob) return xs.geoJob;
    const scanId = st.scan && st.scan.id;
    xs.geoJob = (async () => {
      $("xp-geo-btn").disabled = true;
      try {
        const points = R.collectPoints(st.items, xs.geo);
        const batches = R.chunk(points, GEO_BATCH);
        for (let i = 0; i < batches.length; i++) {
          setStatus("xp-geo-status", batches.length > 1
            ? `Recherche des lieux : paquet ${i + 1} sur ${batches.length}…`
            : `Recherche des lieux de ${nf(points.length)} points…`);
          const places = await reverse(batches[i]);
          batches[i].forEach((p, k) => xs.geo.set(R.pointKey(p[0], p[1]), places[k] || null));
        }
        if (!st.scan || st.scan.id !== scanId) return; // nouvelle analyse entre-temps
        xs.geoScan = scanId;
        $("copy-error").hidden = true; // une erreur « lieux introuvables » précédente n'a plus lieu d'être
        recomputePlaces();
        setStatus("xp-geo-status", points.length ? `${nf(points.length)} points géocodés.` : "Lieux à jour.");
      } catch (err) {
        setStatus("xp-geo-status", err.message, true);
        throw err;
      } finally {
        $("xp-geo-btn").disabled = false;
        xs.geoJob = null;
      }
    })();
    return xs.geoJob;
  }

  function recomputePlaces() {
    xs.places = R.computePlaces(st.items.filter(eligible), xs.geo, {
      level: cfg.level,
      minCount: cfg.minCount,
      inheritHours: cfg.inheritHours,
    });
    renderPlaces();
    refreshTileTitles();
    if (core.panel() === "xp-rangement") renderRangement(true);
    if (core.panel() === "xp-stats") renderStats();
  }

  function renderLieux() {
    $("xp-level").value = cfg.level;
    $("xp-min").value = cfg.minCount;
    $("xp-hours").value = cfg.inheritHours;
    const c = flagCounts();
    const photos = st.items.filter((item) => item.kind !== "video").length;
    const unique = new Set(st.items.filter(R.hasGps).map((item) => R.pointKey(item.lat, item.lon))).size;
    const deep = st.scan && st.scan.deep;
    $("xp-geo-summary").textContent = deep
      ? `${nf(c.gps)} éléments géolocalisés sur ${nf(st.items.length)} (${nf(photos)} photos), ${nf(unique)} points distincts à ~100 m près.`
      : "Analyse rapide : le GPS des photos n'a pas été lu.";
    $("xp-deep-btn").hidden = !!deep;
    $("xp-geo-btn").disabled = !deep || !!xs.geoJob;
    renderPlaces();
  }

  function renderPlaces() {
    const card = $("xp-places-card");
    if (!xs.places) {
      card.hidden = true;
      return;
    }
    card.hidden = false;
    const { stats } = xs.places;
    $("xp-places-sub").textContent = `${nf(stats.own)} par GPS · ${nf(stats.inherited)} hérités · ${nf(stats.unknown)} inconnus`;
    const bytesBy = new Map();
    for (const item of st.items) {
      const place = xs.places.places.get(item.id);
      if (place) bytesBy.set(place.lieu, (bytesBy.get(place.lieu) || 0) + item.size);
    }
    bars($("xp-places"), [...stats.byLieu].map(([label, n]) => [label, n, bytesBy.get(label) || 0]).sort((a, b) => b[1] - a[1]), 60);
  }

  $("xp-geo-btn").addEventListener("click", () => {
    locate().catch(() => { /* message déjà affiché */ });
  });
  $("xp-deep-btn").addEventListener("click", () => {
    cfg.deep = true;
    $("xp-deep").checked = true;
    save();
    core.kick();
  });

  for (const [id, key, parse] of [
    ["xp-level", "level", (v) => v],
    ["xp-min", "minCount", (v) => Math.max(1, Math.floor(Number(v) || 1))],
    ["xp-hours", "inheritHours", (v) => Math.max(0, Number(v) || 0)],
  ]) {
    $(id).addEventListener("change", () => {
      cfg[key] = parse($(id).value);
      $(id).value = cfg[key];
      save();
      if (xs.places) recomputePlaces();
    });
  }

  function placesFor(ids) {
    const out = {};
    const places = xs.places.places;
    for (const id of ids) {
      const p = places.get(id);
      if (p) out[id] = p;
    }
    return out;
  }

  /* ---------- Copie ---------- */

  /** Corps de /transfer et /plan : destination, ids, options (et lieux si le modèle en a besoin). */
  async function transferBody(ids) {
    const target = destination();
    if (!target) throw new Error("Choisis une destination.");
    const options = {
      rootName: cfg.rootName,
      layout: cfg.layout,
      fileName: cfg.fileName.trim() || "{nom}",
      ...cfg.copy,
    };
    xs.copyPlaces = null;
    if (R.usesPlaces(cfg.layout, cfg.fileName)) {
      if (xs.geoScan !== st.scan.id || !xs.places) {
        try {
          await locate();
        } catch (err) {
          throw new Error(`Lieux introuvables : ${err.message} Change de modèle ou réessaie.`);
        }
      }
      options.places = placesFor(ids);
      xs.copyPlaces = xs.places.places;
    }
    return { ...target.body, ids, options };
  }

  function renderOptions() {
    for (const box of document.querySelectorAll("[data-opt]")) box.checked = !!cfg.copy[box.dataset.opt];
    $("xp-conflict").value = cfg.copy.conflict;
  }

  for (const box of document.querySelectorAll("[data-opt]")) {
    box.addEventListener("change", () => {
      cfg.copy[box.dataset.opt] = box.checked;
      save();
      if (box.dataset.opt === "dedupe") regallery();
    });
  }
  $("xp-conflict").addEventListener("change", () => {
    cfg.copy.conflict = $("xp-conflict").value;
    save();
  });
  $("xp-opt-reset").addEventListener("click", () => {
    const dedupeBefore = cfg.copy.dedupe;
    cfg.copy = { ...COPY_DEFAULTS };
    save();
    renderOptions();
    if (dedupeBefore !== cfg.copy.dedupe) regallery();
  });

  $("end-csv").addEventListener("click", () => {
    const copy = st.lastCopy;
    if (!copy || !copy.job) return;
    const csv = R.toCsv(R.reportRows(copy.job, copy.items, copy.places));
    const now = new Date();
    const name = `bilan-copie-${dayOf(now)}-${pad(now.getHours())}h${pad(now.getMinutes())}.csv`;
    const url = URL.createObjectURL(new Blob([csv], { type: "text/csv;charset=utf-8" }));
    const a = document.createElement("a");
    a.href = url;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  });

  /* ---------- Statistiques ---------- */

  function bars(box, rows, limit) {
    box.innerHTML = "";
    if (!rows.length) {
      box.appendChild(emptyNote("Rien à compter."));
      return;
    }
    const max = Math.max(...rows.map((r) => r[1]));
    const shown = limit ? rows.slice(0, limit) : rows;
    for (const [label, n, bytes] of shown) {
      const row = document.createElement("div");
      row.className = "xp-bar-row";
      row.innerHTML = `<span class="xp-bar-label"></span><span class="xp-bar"><i style="width:${Math.max(1.5, (n / max) * 100)}%"></i></span>`
        + `<span class="xp-bar-n">${nf(n)}</span><span class="xp-bar-size">${formatBytes(bytes)}</span>`;
      row.querySelector(".xp-bar-label").textContent = label;
      row.querySelector(".xp-bar-label").title = label;
      box.appendChild(row);
    }
    if (limit && rows.length > limit) box.appendChild(emptyNote(`… et ${nf(rows.length - limit)} autres.`));
  }

  function groupBy(items, keyOf) {
    const m = new Map();
    for (const item of items) {
      const k = keyOf(item);
      const g = m.get(k) || [k, 0, 0];
      g[1]++;
      g[2] += item.size;
      m.set(k, g);
    }
    return [...m.values()];
  }

  function renderStats() {
    const pool = st.items.filter(eligible);
    const c = flagCounts();
    const kpis = [
      ["Éléments", nf(st.items.length)],
      ["Copiables", nf(pool.length)],
      ["Déjà là", nf(pool.filter((i) => i.onDrive).length)],
      ["Choisis", nf(st.selected.size)],
      ["Masqués", nf(c.hidden)],
      ["Clips animés", nf(c.live)],
      ["Doublons", nf(c.dupes)],
      ["Photos animées", st.scan && st.scan.deep ? nf(c.motion) : "—"],
      ["Avec GPS", st.scan && st.scan.deep ? nf(c.gps) : "—"],
      ["Volume", formatBytes(pool.reduce((s, i) => s + i.size, 0))],
    ];
    $("xp-kpis").innerHTML = kpis.map(([k, v]) => `<div class="xp-kpi"><span>${k}</span><strong>${v}</strong></div>`).join("");
    bars($("xp-stat-year"), groupBy(pool, (i) => (i.when ? String(i.when.getFullYear()) : "Sans date")).sort((a, b) => b[0].localeCompare(a[0])));
    bars($("xp-stat-source"), groupBy(pool, (i) => R.SOURCE_LABELS[i.source] || "Autres").sort((a, b) => b[1] - a[1]));
    bars($("xp-stat-kind"), groupBy(pool, (i) => (i.kind === "video" ? "Vidéos" : "Photos")).sort((a, b) => b[1] - a[1]));
    const placeBox = $("xp-stat-place");
    if (xs.places) {
      bars(placeBox, groupBy(pool, (i) => (xs.places.places.get(i.id) || {}).lieu || R.UNKNOWN_PLACE).sort((a, b) => b[1] - a[1]), 20);
    } else {
      placeBox.innerHTML = "";
      placeBox.appendChild(emptyNote(st.scan && st.scan.deep ? "Lieux pas encore calculés (volet Lieux)." : "Analyse approfondie nécessaire."));
    }
  }

  /* ---------- Branchements appelés par phone.js ---------- */

  function onGallery() {
    // Nouvelle analyse : lieux à refaire, sauf si le cache couvre déjà tous les points.
    if (xs.geoScan !== st.scan.id) {
      xs.places = null;
      if (xs.geo.size && st.scan.deep && R.collectPoints(st.items, xs.geo).length === 0) {
        xs.geoScan = st.scan.id;
        recomputePlaces();
      }
    }
    const c = flagCounts();
    $("xp-scan-note").textContent = `${nf(st.items.length)} éléments · analyse ${st.scan.deep ? `approfondie · ${nf(c.gps)} avec GPS` : "rapide"}`;
    renderFilters();
    xs.planSig = null;
    setStatus("xp-plan-status", "");
    $("xp-plan-tree").innerHTML = "";
    openPanel(cfg.panel);
  }

  function onSelection() {
    const sig = planSig();
    if (sig === xs.lastSig) return;
    xs.lastSig = sig;
    if (core.panel() === "xp-rangement") {
      renderExamples();
      renderPlanState();
    }
  }

  /* ---------- Interrupteur ---------- */

  function syncInputs() {
    $("xp-root").value = cfg.rootName;
    $("xp-deep").checked = cfg.deep;
    $("xp-folder").value = cfg.dest && cfg.dest.type === "folder" ? cfg.dest.path : "";
    renderRecents();
  }

  function setActive(on, initial) {
    api.active = on;
    $("phone-main").classList.toggle("expert", on);
    $("xp-switch").checked = on;
    store.set(KEY_ON, on ? "1" : "0");
    xs.keys = {};
    if (on) syncInputs();
    if (!initial) core.reset();
  }

  $("xp-switch").addEventListener("change", (event) => {
    if (st.transfer || st.scanning || st.preparing) {
      event.target.checked = api.active; // pas de bascule en pleine analyse ou copie
      return;
    }
    setActive(event.target.checked, false);
  });

  initRangement();
  setActive(store.get(KEY_ON) === "1", true);

  return Object.assign(api, {
    refresh,
    destination,
    eligible,
    allowOnDrive: () => !cfg.copy.dedupe,
    inPeriod,
    excludedNote,
    tileFlags,
    tileTitle,
    transferBody,
    rootName: () => cfg.rootName,
    onGallery,
    onSelection,
    onFinish(job) {
      if (st.lastCopy) st.lastCopy.places = xs.copyPlaces;
      return job;
    },
  });
};
