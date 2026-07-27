"use strict";

const $ = (id) => document.getElementById(id);

const RING_CIRCUMFERENCE = 754; // 2 * PI * r(120), en unités du viewBox

let currentFormat = "mp3";
let currentJobId = null;
let pollTimer = null;
let seeking = false;

/* ---------- Thème clair / sombre ---------- */

// Le thème initial est déjà posé par le script inline du <head>.
const themeQuery = window.matchMedia("(prefers-color-scheme: dark)");

function applyTheme(theme, persist) {
  document.documentElement.dataset.theme = theme;
  $("theme-label").textContent = theme === "dark" ? "Mode clair" : "Mode sombre";
  if (persist) {
    try { localStorage.setItem("yayo-theme", theme); } catch { /* navigation privée */ }
  }
}

applyTheme(document.documentElement.dataset.theme === "dark" ? "dark" : "light", false);

$("theme-toggle").addEventListener("click", () => {
  const next = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
  applyTheme(next, true);
});

// Suit le thème du système tant que l'utilisateur n'a pas choisi lui-même.
themeQuery.addEventListener("change", (event) => {
  let chosen = null;
  try { chosen = localStorage.getItem("yayo-theme"); } catch { /* ignore */ }
  if (!chosen) applyTheme(event.matches ? "dark" : "light", false);
});

/* ---------- Recherche ---------- */

$("search-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const query = $("search-input").value.trim();
  const hint = $("search-hint");
  const results = $("results");
  if (!query) {
    hint.textContent = "Écris le nom d'une chanson ou d'un artiste, puis appuie sur Rechercher.";
    hint.hidden = false;
    return;
  }
  const btn = $("search-btn");
  btn.disabled = true;
  $("search-btn-label").textContent = "Recherche…";
  hint.hidden = false;
  hint.classList.remove("error");
  hint.textContent = "On cherche sur YouTube…";
  results.hidden = true;

  try {
    const resp = await fetch(`/api/search?q=${encodeURIComponent(query)}`);
    const data = await resp.json();
    if (!resp.ok) throw new Error(data.detail || "La recherche a échoué.");
    renderResults(data.results);
    hint.hidden = true;
  } catch (err) {
    hint.classList.add("error");
    hint.textContent = err.message || "La recherche a échoué, réessaie.";
    hint.hidden = false;
  } finally {
    btn.disabled = false;
    $("search-btn-label").textContent = "Rechercher";
  }
});

function formatDuration(seconds) {
  // Un flux en cours de chargement annonce parfois une durée infinie ou NaN.
  if (seconds === null || seconds === undefined || !isFinite(seconds) || seconds < 0) return "";
  const s = Math.round(seconds);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = String(s % 60).padStart(2, "0");
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${sec}` : `${m}:${sec}`;
}

function renderResults(items) {
  const results = $("results");
  results.innerHTML = "";
  if (!items.length) {
    const hint = $("search-hint");
    hint.textContent = "Aucun résultat… essaie avec d'autres mots.";
    hint.hidden = false;
    $("results-placeholder").hidden = false;
    return;
  }
  $("results-placeholder").hidden = true;
  for (const item of items) {
    // Une div plutôt qu'un <button> : Chromium calcule mal la hauteur
    // intrinsèque des boutons conteneurs flex dans une grille (cartes écrasées).
    const card = document.createElement("div");
    card.className = "result";
    card.setAttribute("role", "button");
    card.tabIndex = 0;
    card.innerHTML = `
      <div class="thumb-wrap">
        <img src="${item.thumbnail}" alt="" loading="lazy">
        ${item.duration ? `<span class="duration">${formatDuration(item.duration)}</span>` : ""}
      </div>
      <div class="result-info">
        <p class="result-title"></p>
        <p class="result-channel"></p>
      </div>`;
    card.querySelector(".result-title").textContent = item.title;
    card.querySelector(".result-channel").textContent = item.channel;
    card.addEventListener("click", () => selectResult(card, item));
    card.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        selectResult(card, item);
      }
    });
    results.appendChild(card);
  }
  results.hidden = false;
}

function selectResult(card, item) {
  document.querySelectorAll(".result.selected").forEach((el) => el.classList.remove("selected"));
  card.classList.add("selected");
  const urlInput = $("url-input");
  urlInput.value = item.url;
  urlInput.classList.remove("flash");
  void urlInput.offsetWidth; // relance l'animation
  urlInput.classList.add("flash");
  $("inline-error").hidden = true;
  $("download-card").scrollIntoView({ behavior: "smooth", block: "center" });
}

/* ---------- Choix du format ---------- */

document.querySelectorAll(".format-pill").forEach((pill) => {
  pill.addEventListener("click", () => {
    document.querySelectorAll(".format-pill").forEach((p) => p.classList.remove("selected"));
    pill.classList.add("selected");
    currentFormat = pill.dataset.format;
  });
});

/* ---------- Lancement ---------- */

$("go-btn").addEventListener("click", startJob);
$("url-input").addEventListener("keydown", (event) => {
  if (event.key === "Enter") startJob();
});

async function startJob() {
  const url = $("url-input").value.trim();
  const inlineError = $("inline-error");
  if (!url) {
    inlineError.textContent = "Choisis d'abord une vidéo au-dessus, ou colle un lien YouTube ici.";
    inlineError.hidden = false;
    return;
  }
  inlineError.hidden = true;

  try {
    const resp = await fetch("/api/jobs", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ url, format: currentFormat }),
    });
    const data = await resp.json();
    if (!resp.ok) throw new Error(data.detail || "Impossible de lancer la conversion.");
    currentJobId = data.id;
    openOverlay();
    pollTimer = setInterval(pollJob, 600);
  } catch (err) {
    inlineError.textContent = err.message || "Impossible de lancer la conversion, réessaie.";
    inlineError.hidden = false;
  }
}

/* ---------- Suivi de la progression ---------- */

function openOverlay() {
  showStage("stage-progress");
  $("percent").textContent = "…";
  $("status-text").textContent = "Préparation…";
  $("job-title").textContent = "";
  $("vinyl-label").style.backgroundImage = "";
  $("ring").classList.add("indeterminate");
  setRing(0);
  $("overlay").hidden = false;
  document.body.style.overflow = "hidden";
}

function closeOverlay() {
  clearInterval(pollTimer);
  pollTimer = null;
  currentJobId = null;
  stopPlayer();
  $("overlay").hidden = true;
  document.body.style.overflow = "";
}

function showStage(id) {
  for (const stage of ["stage-progress", "stage-done", "stage-error"]) {
    $(stage).hidden = stage !== id;
  }
}

function setRing(percent) {
  $("ring-fg").style.strokeDashoffset = String(RING_CIRCUMFERENCE * (1 - percent / 100));
}

async function pollJob() {
  if (!currentJobId) return;
  let job;
  try {
    const resp = await fetch(`/api/jobs/${currentJobId}`);
    if (!resp.ok) throw new Error();
    job = await resp.json();
  } catch {
    return; // petit raté réseau : on retentera au prochain tick
  }

  if (job.title) $("job-title").textContent = job.title;
  if (job.thumbnail) $("vinyl-label").style.backgroundImage = `url("${job.thumbnail}")`;

  const ring = $("ring");
  if (job.status === "queued" || job.status === "fetching") {
    ring.classList.add("indeterminate");
    $("percent").textContent = "…";
    $("status-text").textContent = "On va chercher ta vidéo…";
  } else if (job.status === "retrying") {
    ring.classList.add("indeterminate");
    $("percent").textContent = "…";
    $("status-text").textContent = "YouTube fait des siennes, on réessaie…";
  } else if (job.status === "downloading") {
    ring.classList.remove("indeterminate");
    setRing(job.progress || 0);
    $("percent").textContent = `${Math.floor(job.progress || 0)}%`;
    $("status-text").textContent = "Téléchargement en cours…";
  } else if (job.status === "converting") {
    ring.classList.remove("indeterminate");
    setRing(100);
    $("percent").textContent = "100%";
    $("status-text").textContent =
      job.format === "mp3" ? "Transformation en musique MP3…" : "Préparation de la vidéo…";
  } else if (job.status === "done") {
    clearInterval(pollTimer);
    pollTimer = null;
    $("done-filename").textContent = job.filename || "";
    setupPlayer(job);
    showStage("stage-done");
    launchConfetti();
  } else if (job.status === "error") {
    clearInterval(pollTimer);
    pollTimer = null;
    $("error-text").textContent = job.error || "Une erreur s'est produite, réessaie.";
    showStage("stage-error");
  }
}

function triggerDownload() {
  if (!currentJobId) return;
  const link = document.createElement("a");
  link.href = `/api/jobs/${currentJobId}/file`;
  link.download = "";
  document.body.appendChild(link);
  link.click();
  link.remove();
}

function launchConfetti() {
  const colors = ["#ff6b35", "#ffa245", "#ffd166", "#06d6a0", "#118ab2", "#ef476f"];
  const cardEl = document.querySelector(".overlay-card");
  for (let i = 0; i < 60; i++) {
    const piece = document.createElement("div");
    piece.className = "confetti";
    piece.style.left = `${Math.random() * 100}%`;
    piece.style.background = colors[i % colors.length];
    piece.style.animationDuration = `${1.6 + Math.random() * 1.6}s`;
    piece.style.animationDelay = `${Math.random() * 0.5}s`;
    piece.style.transform = `rotate(${Math.random() * 360}deg)`;
    cardEl.appendChild(piece);
    setTimeout(() => piece.remove(), 4000);
  }
}

/* ---------- Lecteur : écouter avant de télécharger ---------- */

function setupPlayer(job) {
  const src = `/api/jobs/${currentJobId}/stream`;
  const player = $("player");
  const video = $("video-player");
  const audio = $("audio-player");

  player.classList.remove("playing", "started");
  $("player-hint").textContent = "Appuie pour écouter";
  $("seek").value = 0;
  $("time-current").textContent = "0:00";
  $("time-total").textContent = "0:00";

  if (job.format === "mp4") {
    // Une vidéo a besoin d'une surface : les contrôles natifs font le travail.
    player.hidden = true;
    video.hidden = false;
    video.src = src;
  } else {
    video.hidden = true;
    video.removeAttribute("src");
    player.hidden = false;
    audio.src = src;
  }
}

function stopPlayer() {
  const audio = $("audio-player");
  const video = $("video-player");
  audio.pause();
  audio.removeAttribute("src");
  audio.load();
  video.pause();
  video.removeAttribute("src");
  video.load();
  $("player").classList.remove("playing", "started");
}

const audioEl = $("audio-player");

$("play-btn").addEventListener("click", () => {
  if (audioEl.paused) {
    audioEl.play().catch(() => {
      $("player-hint").textContent = "Lecture impossible ici, mais le téléchargement fonctionne.";
    });
  } else {
    audioEl.pause();
  }
});

audioEl.addEventListener("play", () => {
  $("player").classList.add("playing", "started");
  $("play-btn").setAttribute("aria-label", "Mettre en pause");
  $("player-hint").textContent = "Lecture en cours…";
});

audioEl.addEventListener("pause", () => {
  $("player").classList.remove("playing");
  $("play-btn").setAttribute("aria-label", "Écouter la musique");
  $("player-hint").textContent = "En pause";
});

audioEl.addEventListener("ended", () => {
  $("player").classList.remove("playing");
  $("player-hint").textContent = "Terminé — tu peux le télécharger";
  $("seek").value = 0;
  $("time-current").textContent = "0:00";
});

audioEl.addEventListener("loadedmetadata", () => {
  $("time-total").textContent = formatDuration(audioEl.duration) || "0:00";
});

audioEl.addEventListener("timeupdate", () => {
  if (seeking || !isFinite(audioEl.duration)) return;
  $("seek").value = String((audioEl.currentTime / audioEl.duration) * 1000);
  $("time-current").textContent = formatDuration(audioEl.currentTime);
});

// Pendant le glissement on n'écrase pas la position choisie par l'utilisateur.
$("seek").addEventListener("input", () => {
  seeking = true;
  if (isFinite(audioEl.duration)) {
    $("time-current").textContent = formatDuration(($("seek").value / 1000) * audioEl.duration);
  }
});

$("seek").addEventListener("change", () => {
  if (isFinite(audioEl.duration)) {
    audioEl.currentTime = ($("seek").value / 1000) * audioEl.duration;
  }
  seeking = false;
});

/* ---------- Boutons de l'overlay ---------- */

$("save-btn").addEventListener("click", triggerDownload);
$("again-btn").addEventListener("click", () => {
  closeOverlay();
  $("search-input").focus();
});
$("retry-btn").addEventListener("click", () => {
  closeOverlay();
  startJob();
});
$("close-btn").addEventListener("click", closeOverlay);
