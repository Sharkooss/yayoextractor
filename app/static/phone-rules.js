"use strict";

/* Règles pures de l'onglet Téléphone, sans DOM : lieux (seuil, héritage),
   modèles de rangement, arborescence de l'aperçu, phrases de bilan, CSV.
   Chargé par le site (window.YayoRules) et testable sous Node (require). */

(function (root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  else root.YayoRules = api;
})(typeof self !== "undefined" ? self : this, () => {
  /* ---------- Lieux ---------- */

  const LEVELS = ["ville", "departement", "region", "pays"];
  const LEVEL_LABELS = { ville: "Ville", departement: "Département", region: "Région", pays: "Pays" };
  const UNKNOWN_PLACE = "Lieu inconnu";
  const PLACE_TOKENS = ["lieu", "ville", "departement", "region", "pays"];

  const round3 = (x) => Math.round(x * 1000) / 1000;

  /** Clé d'un point arrondi à 3 décimales (~100 m) : sert de cache et de dédoublonnage. */
  const pointKey = (lat, lon) => `${round3(lat).toFixed(3)},${round3(lon).toFixed(3)}`;

  const hasGps = (item) => Number.isFinite(item.lat) && Number.isFinite(item.lon)
    && Math.abs(item.lat) <= 90 && Math.abs(item.lon) <= 180
    && !(item.lat === 0 && item.lon === 0); // 0,0 = GPS vide dans beaucoup d'EXIF

  /** Points à géocoder : arrondis, dédoublonnés, sans ceux déjà dans `known` (Map ou Set de clés). */
  function collectPoints(items, known) {
    const seen = new Set();
    const points = [];
    for (const item of items) {
      if (!hasGps(item)) continue;
      const key = pointKey(item.lat, item.lon);
      if (seen.has(key) || (known && known.has(key))) continue;
      seen.add(key);
      points.push([round3(item.lat), round3(item.lon)]);
    }
    return points;
  }

  function chunk(list, size) {
    const out = [];
    for (let i = 0; i < list.length; i += size) out.push(list.slice(i, i + size));
    return out;
  }

  /** Date de l'élément en ms : prise de vue EXIF d'abord, sinon date du téléphone. */
  function itemTime(item) {
    const raw = item.exifDate || item.date;
    const t = raw ? Date.parse(raw) : NaN;
    return Number.isFinite(t) ? t : null;
  }

  /** Repère (photo géolocalisée) le plus proche dans le temps ; `anchors` trié par t. */
  function nearestAnchor(anchors, t) {
    let lo = 0;
    let hi = anchors.length - 1;
    while (lo < hi) {
      const mid = (lo + hi) >> 1;
      if (anchors[mid].t < t) lo = mid + 1;
      else hi = mid;
    }
    let best = anchors[lo];
    if (lo > 0 && Math.abs(anchors[lo - 1].t - t) <= Math.abs(best.t - t)) best = anchors[lo - 1];
    return best;
  }

  /**
   * Lieu de chaque élément, d'après le géocodage de ses coordonnées.
   * - level : "ville" | "departement" | "region" | "pays" (niveau de {lieu}) ;
   * - minCount : un lieu qui regroupe moins de minCount fichiers remonte au
   *   niveau supérieur (ville → département → région → pays ; le pays ne remonte pas) ;
   * - inheritHours : un élément sans lieu (pas de GPS, ou GPS loin de tout lieu
   *   connu) prend celui de la photo géolocalisée la plus proche dans le temps si
   *   elle est à moins de inheritHours heures ; sinon « Lieu inconnu ».
   * `geo` : Map pointKey → {ville, departement, region, pays} | null.
   * Renvoie { places: Map id → {lieu, ville, departement, region, pays}, stats }.
   */
  function computePlaces(items, geo, settings = {}) {
    const start = Math.max(0, LEVELS.indexOf(settings.level || "ville"));
    const minCount = Math.max(1, Math.floor(Number(settings.minCount) || 1));
    const maxGap = Math.max(0, Number(settings.inheritHours) || 0) * 3600e3;

    const found = new Map(); // id → { place, inherited }
    const anchors = [];
    for (const item of items) {
      if (!hasGps(item)) continue;
      const place = geo.get(pointKey(item.lat, item.lon));
      if (!place) continue;
      found.set(item.id, { place, inherited: false });
      const t = itemTime(item);
      if (t !== null) anchors.push({ t, place });
    }
    anchors.sort((a, b) => a.t - b.t);
    if (maxGap > 0 && anchors.length) {
      for (const item of items) {
        if (found.has(item.id)) continue;
        const t = itemTime(item);
        if (t === null) continue;
        const near = nearestAnchor(anchors, t);
        if (Math.abs(near.t - t) <= maxGap) found.set(item.id, { place: near.place, inherited: true });
      }
    }

    // Lieux fréquents : on remonte niveau par niveau les groupes trop petits.
    // Un groupe = même nom à ce niveau ET mêmes niveaux supérieurs (homonymes séparés).
    const levelOf = new Map();
    for (const id of found.keys()) levelOf.set(id, start);
    const last = LEVELS.length - 1;
    for (let lv = start; lv < last; lv++) {
      const groups = new Map();
      for (const [id, entry] of found) {
        if (levelOf.get(id) !== lv) continue;
        if (!entry.place[LEVELS[lv]]) { // valeur absente (pas de département…) : niveau au-dessus
          levelOf.set(id, lv + 1);
          continue;
        }
        const key = LEVELS.slice(lv).map((k) => entry.place[k] || "").join("|");
        if (!groups.has(key)) groups.set(key, []);
        groups.get(key).push(id);
      }
      for (const ids of groups.values()) {
        if (ids.length < minCount) for (const id of ids) levelOf.set(id, lv + 1);
      }
    }

    const places = new Map();
    const byLieu = new Map();
    let inherited = 0;
    let unknown = 0;
    for (const item of items) {
      const entry = found.get(item.id);
      let value;
      if (!entry) {
        value = { lieu: UNKNOWN_PLACE, ville: null, departement: null, region: null, pays: null };
        unknown++;
      } else {
        const p = entry.place;
        const lieu = p[LEVELS[levelOf.get(item.id)]] || UNKNOWN_PLACE;
        value = { lieu, ville: p.ville || null, departement: p.departement || null, region: p.region || null, pays: p.pays || null };
        if (lieu === UNKNOWN_PLACE) unknown++;
        if (entry.inherited) inherited++;
      }
      places.set(item.id, value);
      byLieu.set(value.lieu, (byLieu.get(value.lieu) || 0) + 1);
    }
    return { places, stats: { byLieu, inherited, own: found.size - inherited, unknown } };
  }

  /* ---------- Modèles de rangement ---------- */

  const MONTHS = ["Janvier", "Février", "Mars", "Avril", "Mai", "Juin", "Juillet", "Août",
    "Septembre", "Octobre", "Novembre", "Décembre"];
  const SOURCE_LABELS = { camera: "Appareil photo", whatsapp: "WhatsApp", screenshots: "Captures d'écran", other: "Autres" };
  const TOKENS = ["annee", "mois", "mois_nom", "jour", "date", "heure", "source", "type",
    "lieu", "ville", "departement", "region", "pays", "nom"];
  const PRESETS = [
    { id: "annee-mois", label: "Année / Mois", layout: "{annee}/{mois} - {mois_nom}" },
    { id: "annee", label: "Année", layout: "{annee}" },
    { id: "annee-mois-jour", label: "Année / Mois / Jour", layout: "{annee}/{mois} - {mois_nom}/{jour}" },
    { id: "plat", label: "Tout dans un dossier", layout: "" },
    { id: "lieu-annee", label: "Lieu / Année", layout: "{lieu}/{annee}" },
    { id: "annee-lieu", label: "Année / Lieu", layout: "{annee}/{lieu}" },
    { id: "pays-ville", label: "Pays / Ville", layout: "{pays}/{ville}" },
    { id: "source-annee", label: "Source / Année", layout: "{source}/{annee}" },
    { id: "type-annee-mois", label: "Type / Année / Mois", layout: "{type}/{annee}/{mois} - {mois_nom}" },
  ];
  const DEFAULT_LAYOUT = PRESETS[0].layout;

  const usesPlaces = (...templates) => templates.some((t) => PLACE_TOKENS.some((k) => String(t || "").includes(`{${k}}`)));

  const pad = (n) => String(n).padStart(2, "0");

  function splitName(name) {
    const dot = name.lastIndexOf(".");
    return dot > 0 ? [name.slice(0, dot), name.slice(dot)] : [name, ""];
  }

  /** Valeurs des jetons pour un élément (date locale, comme le pont). */
  function tokenValues(item, place) {
    const t = itemTime(item);
    const d = t === null ? null : new Date(t);
    const noDate = "Date inconnue";
    const p = place || {};
    return {
      annee: d ? String(d.getFullYear()) : noDate,
      mois: d ? pad(d.getMonth() + 1) : noDate,
      mois_nom: d ? MONTHS[d.getMonth()] : noDate,
      jour: d ? pad(d.getDate()) : noDate,
      date: d ? `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}` : noDate,
      heure: d ? `${pad(d.getHours())}h${pad(d.getMinutes())}` : noDate,
      source: SOURCE_LABELS[item.source] || SOURCE_LABELS.other,
      type: item.kind === "video" ? "Vidéos" : "Photos",
      lieu: p.lieu || UNKNOWN_PLACE,
      ville: p.ville || UNKNOWN_PLACE,
      departement: p.departement || UNKNOWN_PLACE,
      region: p.region || UNKNOWN_PLACE,
      pays: p.pays || UNKNOWN_PLACE,
      nom: splitName(item.name || "")[0],
    };
  }

  const RESERVED = /^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\..*)?$/i;

  /** Un segment de chemin acceptable par Windows (même règle que le pont). */
  function cleanSegment(text) {
    let s = String(text).replace(/[<>:"/\\|?*\u0000-\u001f]/g, "_").trim();
    s = s.replace(/[. ]+$/, "");
    if (RESERVED.test(s)) s = "_" + s;
    if (s.length > 80) s = s.slice(0, 80).replace(/[. ]+$/, "");
    return s;
  }

  function fillTemplate(template, values) {
    return String(template || "").replace(/\{([a-z_]+)\}/g, (all, key) => (key in values ? values[key].replace(/[/\\]/g, "-") : all));
  }

  /** Chemin relatif (séparateur « / ») d'un élément, racine comprise si `rootName`. */
  function renderPath({ layout, fileName, rootName }, item, place) {
    const values = tokenValues(item, place);
    const folders = fillTemplate(layout, values).split("/").map(cleanSegment).filter(Boolean);
    const [, ext] = splitName(item.name || "");
    const base = cleanSegment(fillTemplate(fileName || "{nom}", values)) || splitName(item.name || "fichier")[0];
    const root = rootName ? cleanSegment(rootName) : "";
    return [root, ...folders, base + ext].filter(Boolean).join("/");
  }

  /* ---------- Aperçu du rangement : arborescence ---------- */

  /** {folders:[{path,count,bytes}]} → arbre {name, count, bytes, children[]} trié par nom. */
  function buildTree(folders) {
    const root = { name: "", count: 0, bytes: 0, children: new Map() };
    for (const f of folders || []) {
      const parts = String(f.path || "").split(/[/\\]+/).filter(Boolean);
      let node = root;
      root.count += f.count || 0;
      root.bytes += f.bytes || 0;
      for (const part of parts) {
        if (!node.children.has(part)) node.children.set(part, { name: part, count: 0, bytes: 0, children: new Map() });
        node = node.children.get(part);
        node.count += f.count || 0;
        node.bytes += f.bytes || 0;
      }
    }
    const finish = (node) => ({
      name: node.name,
      count: node.count,
      bytes: node.bytes,
      children: [...node.children.values()]
        .map(finish)
        .sort((a, b) => a.name.localeCompare(b.name, "fr", { numeric: true })),
    });
    return finish(root);
  }

  /* ---------- Phrases ---------- */

  const nf = (n) => Number(n).toLocaleString("fr-FR");
  const many = (n, one, more) => `${nf(n)} ${n > 1 ? more : one}`;

  function formatBytes(bytes) {
    if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(1).replace(".", ",")} Go`;
    if (bytes >= 1024 ** 2) return `${Math.round(bytes / 1024 ** 2)} Mo`;
    return `${Math.max(1, Math.round(bytes / 1024))} Ko`;
  }

  function joinFr(parts) {
    if (parts.length <= 1) return parts.join("");
    return `${parts.slice(0, -1).join(", ")} et ${parts[parts.length - 1]}`;
  }

  /** Éléments écartés d'office (mode normal), chacun compté une fois : masqué > clip > doublon. */
  function countExcluded(items) {
    const c = { whatsapp: 0, hidden: 0, live: 0, duplicates: 0 };
    for (const item of items) {
      if (item.hidden) {
        if (item.source === "whatsapp") c.whatsapp++;
        else c.hidden++;
      } else if (item.live) c.live++;
      else if (item.duplicateOf) c.duplicates++;
    }
    return c;
  }

  /** « 312 copies WhatsApp, 48 clips de photos animées et 20 doublons ne seront pas copiés. » */
  function excludedSentence(c) {
    const parts = [];
    if (c.whatsapp) parts.push(many(c.whatsapp, "copie WhatsApp", "copies WhatsApp"));
    if (c.hidden) parts.push(many(c.hidden, "fichier caché", "fichiers cachés"));
    if (c.live) parts.push(many(c.live, "clip de photo animée", "clips de photos animées"));
    if (c.duplicates) parts.push(many(c.duplicates, "doublon", "doublons"));
    if (!parts.length) return "";
    const total = c.whatsapp + c.hidden + c.live + c.duplicates;
    return `${joinFr(parts)} ${total > 1 ? "ne seront pas copiés" : "ne sera pas copié"} (des doubles, ou ce que la Galerie du téléphone ne montre pas).`;
  }

  /** Bilan du rangement de la destination ; "" si rien n'a été fait.
      `detailed` (mode avancé) ajoute les copies interrompues et la place libérée. */
  function repairSentence(repair, where = "ta clé", detailed = false, moment = "copy") {
    if (!repair) return "";
    const scan = moment === "scan";
    const parts = [];
    if (repair.duplicates) parts.push(`${many(repair.duplicates, "doublon enlevé", "doublons enlevés")}`);
    if (repair.motionStripped) parts.push(`${many(repair.motionStripped, "photo animée remise", "photos animées remises")} en photo`);
    if (repair.liveRemoved) parts.push(`${many(repair.liveRemoved, "clip de photo animée enlevé", "clips de photos animées enlevés")}`);
    if (repair.brokenRemoved) parts.push(`${many(repair.brokenRemoved, "vidéo incomplète enlevée", "vidéos incomplètes enlevées")} (${scan ? "à recopier" : `recopiée${repair.brokenRemoved > 1 ? "s" : ""} juste après`})`);
    if (repair.sentRemoved) parts.push(`${many(repair.sentRemoved, "copie WhatsApp en double enlevée", "copies WhatsApp en double enlevées")}`);
    if (detailed && repair.partsRemoved) parts.push(`${many(repair.partsRemoved, "copie interrompue effacée", "copies interrompues effacées")}`);
    if (!parts.length) return "";
    const freed = detailed && repair.bytesFreed > 0 ? ` (${formatBytes(repair.bytesFreed)} libérés)` : "";
    return `${scan ? "On a rangé" : "On a aussi rangé"} ${where} : ${joinFr(parts)}${freed}.`;
  }

  /* ---------- CSV (Excel français : « ; » et BOM UTF-8) ---------- */

  function csvCell(value) {
    if (value === null || value === undefined) return "";
    const s = String(value);
    return /[;"\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  }

  const toCsv = (rows) => "﻿" + rows.map((row) => row.map(csvCell).join(";")).join("\r\n") + "\r\n";

  /**
   * Lignes du bilan de copie. `job` = état final de /transfer ; `items` = éléments
   * envoyés (dans l'ordre) ; `places` = Map id → lieu (facultatif).
   */
  function reportRows(job, items, places) {
    const copied = new Map((job.copiedFiles || []).map((f) => [f.id, f]));
    const failed = new Map((job.failed || []).map((f) => [f.id, f]));
    const rows = [["statut", "nom", "chemin sur la destination", "taille (octets)", "date", "source", "type", "lieu", "chemin sur le téléphone", "erreur", "id"]];
    for (const item of items) {
      const c = copied.get(item.id);
      const f = failed.get(item.id);
      const status = c ? "copié" : f ? "échec" : job.state === "done" ? "ignoré (déjà présent)" : "non copié";
      const place = places && places.get(item.id);
      rows.push([
        status,
        item.name,
        c ? c.path : "",
        item.size,
        item.exifDate || item.date || "",
        SOURCE_LABELS[item.source] || item.source || "",
        item.kind === "video" ? "vidéo" : "photo",
        place ? place.lieu : "",
        item.path || "",
        f ? f.error : "",
        item.id,
      ]);
    }
    return rows;
  }

  return {
    LEVELS, LEVEL_LABELS, UNKNOWN_PLACE, PLACE_TOKENS, TOKENS, PRESETS, DEFAULT_LAYOUT, MONTHS, SOURCE_LABELS,
    round3, pointKey, hasGps, collectPoints, chunk, itemTime, computePlaces,
    usesPlaces, tokenValues, cleanSegment, renderPath, buildTree,
    formatBytes, countExcluded, excludedSentence, repairSentence, toCsv, reportRows,
  };
});
