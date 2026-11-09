/* فلاتر البحث (الأكثر شهرة/الأقدم/بلاي لست) + كروت البلاي لست في النتائج.
   مفصول من app.js — يعتمد على دوال app.js العامة (esc, L, t, doSearch, ...). */
/* ================= فلاتر البحث + فتح البلاي لست ================= */
function sortLabels() {
  return {
    "": L("الأكثر صلة", "Relevance"),
    popular: L("الأكثر شهرة", "Most popular"),
    oldest: L("الأقدم أولاً", "Oldest first"),
    playlists: L("بلاي لست", "Playlists")
  };
}
function syncSearchFilters() {
  const labels = sortLabels();
  document.querySelectorAll("#search-filters .sf-chip").forEach(chip => {
    const on = (chip.dataset.sort || "") === searchSort;
    chip.classList.toggle("active", on);
    chip.setAttribute("aria-checked", String(on));
    chip.textContent = labels[chip.dataset.sort || ""];
  });
  const lt = document.getElementById("sf-label-text");
  if (lt) lt.textContent = L("فلاتر البحث", "Search filters");
}
function setSearchSort(sort) {
  if (sort === searchSort) return;
  searchSort = sort;
  try { localStorage.setItem("mu_search_sort", sort); } catch (e) {}
  syncSearchFilters();
  const q = document.getElementById("search-input").value.trim();
  if (q) doSearch();
}
if (!window.__filtersWired) {
  window.__filtersWired = true;
  const wire = () => {
    document.querySelectorAll("#search-filters .sf-chip").forEach(chip => {
      chip.onclick = () => setSearchSort(chip.dataset.sort || "");
    });
    syncSearchFilters();
  };
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", wire); else wire();
}

let openedPlaylist = null;
let playlistSeq = 0;
function playlistResultCard(p) {
  const div = document.createElement("div");
  div.className = "track-card pl-result";
  const count = p.count ? (p.count + " " + L("أغنية", "tracks")) : "";
  div.innerHTML =
    '<div class="pl-thumb-wrap"><img class="track-thumb" src="' + esc(p.thumbnail || "") + '" alt="">' +
    '<span class="pl-badge">☰ ' + esc(L("بلاي لست", "Playlist")) + '</span></div>' +
    '<div class="track-title">' + esc(p.title) + '</div>' +
    '<div class="track-artist">' + esc(p.artist || "") + (count ? " • " + esc(count) : "") + '</div>' +
    '<div class="track-actions"><button class="btn btn-primary act-open-pl">' + esc(L("فتح القائمة", "Open")) + '</button></div>';
  const open = () => openSearchPlaylist(p);
  div.querySelector(".act-open-pl").onclick = open;
  div.querySelector(".track-thumb").onclick = open;
  return div;
}
async function openSearchPlaylist(p) {
  const seq = ++playlistSeq;
  const message = document.getElementById("search-msg");
  message.textContent = L("جاري فتح البلاي لست...", "Opening playlist...");
  try {
    const res = await fetchWithTimeout("/api/playlist_tracks", {
      method: "POST", headers: {"Content-Type": "application/json"},
      body: JSON.stringify({ url: p.web_url })
    }, 50000);
    const j = await res.json();
    if (seq !== playlistSeq) return;
    if (!res.ok || j.error || !(j.tracks || []).length) throw new Error(j.error || L("تعذر فتح البلاي لست", "Could not open playlist"));
    const tracks = j.tracks.map(trk => Object.assign({}, trk, { thumbnail: trk.thumbnail || j.img || p.thumbnail || "" }));
    message.textContent = "";
    if (j.note) toast(j.note);
    openPlaylistView({ name: j.name || p.title, img: j.img || p.thumbnail || "", tracks, saved: false });
    preResolve(tracks.slice(0, 2));
  } catch (e) {
    if (seq === playlistSeq) message.textContent = e.message || L("تعذر فتح البلاي لست", "Could not open playlist");
  }
}
function renderOpenedPlaylistBar() {
  const bar = document.getElementById("pl-open-bar");
  if (!bar || !openedPlaylist) return;
  const p = openedPlaylist;
  bar.innerHTML =
    '<button class="btn pl-back">→ ' + esc(L("رجوع للنتائج", "Back to results")) + '</button>' +
    (p.img ? '<img class="pl-open-cover" src="' + esc(p.img) + '" alt="">' : "") +
    '<div class="pl-open-meta"><b>' + esc(p.name) + '</b><small>' + p.tracks.length + " " + esc(L("أغنية", "tracks")) + '</small></div>' +
    '<div class="pl-open-actions">' +
    '<button class="btn btn-primary pl-listen">▶ ' + esc(L("استماع", "Listen")) + '</button>' +
    '<button class="btn pl-queue">+ ' + esc(L("أضف الكل للغرفة", "Add all to room")) + '</button>' +
    '<button class="btn pl-save">♫ ' + esc(L("حفظ في مكتبتي", "Save to library")) + '</button></div>';
  bar.classList.remove("hidden");
  bar.querySelector(".pl-back").onclick = () => closeOpenedPlaylist(false);
  bar.querySelector(".pl-listen").onclick = () => playSolo(p.tracks[0]);
  bar.querySelector(".pl-queue").onclick = () => {
    if (!inRoom) { toast(t("room.join_first")); return; }
    if (!canIAdd()) { toast(t("room.owner_only")); return; }
    playPlaylist({ name: p.name, tracks: p.tracks });
  };
  bar.querySelector(".pl-save").onclick = () => {
    const pls = getPlaylists();
    pls.unshift({ id: plId(), name: p.name, img: p.img || "", tracks: p.tracks.slice(), created: Date.now() });
    savePlaylists(pls);
    toast(L("تم حفظ البلاي لست في مكتبتك", "Playlist saved to your library") + " — " + p.name);
    try { renderProfileSections(true); } catch (e) {}
  };
}
function closeOpenedPlaylist(silent) {
  playlistSeq++;
  const bar = document.getElementById("pl-open-bar");
  if (bar) { bar.classList.add("hidden"); bar.innerHTML = ""; }
  if (!openedPlaylist) return;
  const p = openedPlaylist;
  openedPlaylist = null;
  if (silent) return;
  renderResults(p.prev || [], false);
  searchHasMore = p.prevMore; updateMoreResults();
}

try { document.documentElement.setAttribute("data-filters-ready", "1"); } catch (e) {}
