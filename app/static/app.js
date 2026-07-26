"use strict";

const $ = (id) => document.getElementById(id);

const RING_CIRCUMFERENCE = 754; // 2 * PI * r(120), en unités du viewBox

let currentFormat = "mp3";
let currentJobId = null;
let pollTimer = null;
let autoDownloadDone = false;

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
  btn.textContent = "⏳ Recherche…";
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
    btn.textContent = "🔍 Rechercher";
  }
});

function formatDuration(seconds) {
  if (!seconds && seconds !== 0) return "";
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
    return;
  }
  for (const item of items) {
    const card = document.createElement("button");
    card.type = "button";
    card.className = "result";
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
    autoDownloadDone = false;
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
    $("status-text").textContent = "On va chercher ta vidéo… 🎬";
  } else if (job.status === "retrying") {
    ring.classList.add("indeterminate");
    $("percent").textContent = "…";
    $("status-text").textContent = "YouTube fait des siennes, on réessaie… 🔄";
  } else if (job.status === "downloading") {
    ring.classList.remove("indeterminate");
    setRing(job.progress || 0);
    $("percent").textContent = `${Math.floor(job.progress || 0)}%`;
    $("status-text").textContent = "Téléchargement en cours… 🚀";
  } else if (job.status === "converting") {
    ring.classList.remove("indeterminate");
    setRing(100);
    $("percent").textContent = "100%";
    $("status-text").textContent =
      job.format === "mp3" ? "Transformation en musique MP3… 🎶" : "Préparation de la vidéo… 🎬";
  } else if (job.status === "done") {
    clearInterval(pollTimer);
    pollTimer = null;
    $("done-filename").textContent = job.filename || "";
    showStage("stage-done");
    launchConfetti();
    if (!autoDownloadDone) {
      autoDownloadDone = true;
      triggerDownload();
    }
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
