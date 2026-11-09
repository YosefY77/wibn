/* صفحة البلاي لست الكاملة (من المكتبة أو من البحث).
   مفصول من app.js — يعتمد على دوال app.js العامة. */
/* ===== صفحة البلاي لست (بدل النافذة المنبثقة) ===== */
let playlistBackView = "home";
let plvCurrent = null;   // { id?, name, img, tracks, saved }
function openPlaylist(pid) {
  const p = getPlaylists().find(x => x.id === pid);
  if (!p) return;
  openPlaylistView({ id: p.id, name: p.name, img: p.img || "", tracks: p.tracks || [], saved: true });
}
function openPlaylistView(p) {
  try { hideModal(); } catch (e) {}
  const activeView = document.querySelector(".view.active");
  if (activeView && activeView.id !== "view-playlist") playlistBackView = activeView.id.replace("view-", "");
  plvCurrent = p;
  window.__plPool = (p.tracks || []).slice();
  renderPlaylistView();
  showView("playlist");
  const main = document.querySelector(".main");
  if (main) main.scrollTop = 0;
}
function renderPlaylistView() {
  const p = plvCurrent;
  if (!p) return;
  if (p.saved && p.id) {
    const fresh = getPlaylists().find(x => x.id === p.id);
    if (!fresh) { showView(playlistBackView || "home"); return; }
    p.name = fresh.name; p.img = fresh.img || ""; p.desc = fresh.desc || ""; p.tracks = fresh.tracks || [];
    window.__plPool = p.tracks.slice();
  }
  const tracks = p.tracks || [];
  const cover = document.getElementById("plv-cover");
  const img = p.img || (tracks.find(t => t.thumbnail) || {}).thumbnail || "";
  cover.innerHTML = img ? '<img src="' + esc(img) + '" alt="">' : "♫";
  document.getElementById("plv-kind").textContent = p.saved ? L("بلاي لست", "Playlist") : L("بلاي لست من البحث", "Playlist from search");
  document.getElementById("plv-name").textContent = p.name || L("بلاي لست", "Playlist");
  document.getElementById("plv-meta").textContent = tracks.length + " " + t("pl.count") + (totalDur(tracks) ? " · " + totalDur(tracks) : "");
  let descEl = document.getElementById("plv-desc");
  if (!descEl) {
    descEl = document.createElement("p");
    descEl.id = "plv-desc"; descEl.className = "plv-desc";
    document.getElementById("plv-meta").insertAdjacentElement("beforebegin", descEl);
  }
  descEl.textContent = p.desc || "";
  descEl.hidden = !p.desc;
  const acts = document.getElementById("plv-actions");
  acts.innerHTML =
    '<button class="btn btn-primary plv-listen">▶ ' + esc(L("استماع", "Listen")) + '</button>' +
    '<button class="btn plv-room">+ ' + esc(L("أضف الكل للغرفة", "Add all to room")) + '</button>' +
    (p.saved
      ? '<button class="btn plv-edit">✎ ' + esc(L("تعديل", "Edit")) + '</button>' +
        '<button class="btn btn-fav-artist plv-delete">' + esc(t("pl.delete")) + '</button>'
      : '<button class="btn plv-save">♫ ' + esc(L("حفظ في مكتبتي", "Save to library")) + '</button>');
  acts.querySelector(".plv-listen").onclick = () => { if (tracks.length) { window.__plPool = tracks.slice(); window.__plPlaying = true; playSolo(tracks[0]); } else toast(t("pl.empty_list")); };
  acts.querySelector(".plv-room").onclick = () => {
    if (!inRoom) { toast(t("room.join_first")); return; }
    if (!canIAdd()) { toast(t("room.owner_only")); return; }
    playPlaylist({ name: p.name, tracks });
  };
  const ed = acts.querySelector(".plv-edit");
  if (ed) ed.onclick = () => editPlaylistModal(p.id);
  cover.classList.toggle("plv-cover-edit", !!p.saved);
  cover.title = p.saved ? L("تعديل البلاي لست", "Edit playlist") : "";
  cover.onclick = p.saved ? () => editPlaylistModal(p.id) : null;
  const del = acts.querySelector(".plv-delete");
  if (del) del.onclick = () => {
    if (!confirm(L("متأكد تبغى تحذف البلاي لست؟", "Delete this playlist?"))) return;
    savePlaylists(getPlaylists().filter(x => x.id !== p.id));
    toast(t("pl.deleted"));
    try { renderProfileSections(true); } catch (e) {}
    plvCurrent = null;
    showView(playlistBackView || "home");
  };
  const save = acts.querySelector(".plv-save");
  if (save) save.onclick = () => {
    const pls = getPlaylists();
    const pl = { id: plId(), name: p.name, img: p.img || "", tracks: tracks.slice(), created: Date.now() };
    pls.unshift(pl);
    savePlaylists(pls);
    toast(L("تم حفظ البلاي لست في مكتبتك", "Playlist saved to your library") + " — " + p.name);
    try { renderProfileSections(true); } catch (e) {}
    plvCurrent = Object.assign({}, p, { id: pl.id, saved: true });
    renderPlaylistView();
  };
  const box = document.getElementById("plv-tracks");
  if (!tracks.length) { box.innerHTML = '<div class="msg">' + esc(t("pl.empty_list")) + '</div>'; return; }
  box.innerHTML = tracks.map((trk, i) =>
    '<div class="plv-row" data-i="' + i + '"' + (p.saved ? ' draggable="true"' : "") + '>' +
    (p.saved ? '<span class="plv-grip" title="' + esc(L("اسحب لتغيير الترتيب", "Drag to reorder")) + '">⋮⋮</span>' : "") +
    '<span class="plv-num">' + (i + 1) + '</span>' +
    (trk.thumbnail ? '<img class="plv-thumb" src="' + esc(trk.thumbnail) + '" alt="" onerror="this.style.visibility=\'hidden\'">' : '<span class="plv-thumb"></span>') +
    '<div class="plv-main"><div class="plv-title">' + esc(trk.title) + '</div><div class="plv-artist">' + esc(trk.artist || "") + '</div></div>' +
    '<span class="plv-dur">' + (trk.duration ? fmt(trk.duration) : "") + '</span>' +
    '<button class="btn plv-play" title="' + esc(t("track.listen")) + '">▶</button>' +
    (p.saved ? '<button class="btn plv-rm" title="' + esc(t("pl.remove")) + '">✕</button>' : "") +
    '</div>').join("");
  box.querySelectorAll(".plv-row").forEach(row => {
    const i = parseInt(row.dataset.i, 10);
    const play = () => { window.__plPool = tracks.slice(); window.__plPlaying = true; playSolo(tracks[i]); };
    row.querySelector(".plv-play").onclick = e => { e.stopPropagation(); play(); };
    row.ondblclick = play;
    const rm = row.querySelector(".plv-rm");
    if (rm) rm.onclick = e => {
      e.stopPropagation();
      const list = getPlaylists();
      const cur = list.find(x => x.id === p.id);
      if (!cur) return;
      cur.tracks.splice(i, 1);
      savePlaylists(list);
      try { renderProfileSections(true); } catch (e) {}
      renderPlaylistView();
    };
  });
  if (p.saved) enableReorder(box, p.id);
}

function totalDur(tracks) {
  const s = (tracks || []).reduce((a, x) => a + (+x.duration || 0), 0);
  if (!s) return "";
  const h = Math.floor(s / 3600), m = Math.round((s % 3600) / 60);
  return h ? h + L(" س ", " h ") + m + L(" د", " min") : m + L(" دقيقة", " min");
}

/* ===== ترتيب الأغاني بالسحب ===== */
function enableReorder(box, pid) {
  let from = -1;
  const rows = box.querySelectorAll(".plv-row");
  const clear = () => rows.forEach(r => r.classList.remove("drop-above", "drop-below", "dragging"));
  rows.forEach(row => {
    row.addEventListener("dragstart", e => {
      from = parseInt(row.dataset.i, 10);
      row.classList.add("dragging");
      try { e.dataTransfer.effectAllowed = "move"; e.dataTransfer.setData("text/plain", String(from)); } catch (er) {}
    });
    row.addEventListener("dragend", () => { from = -1; clear(); });
    row.addEventListener("dragover", e => {
      if (from < 0) return;
      e.preventDefault();
      const r = row.getBoundingClientRect();
      const below = e.clientY > r.top + r.height / 2;
      rows.forEach(x => { if (x !== row) x.classList.remove("drop-above", "drop-below"); });
      row.classList.toggle("drop-below", below);
      row.classList.toggle("drop-above", !below);
    });
    row.addEventListener("drop", e => {
      e.preventDefault();
      if (from < 0) return;
      const r = row.getBoundingClientRect();
      let to = parseInt(row.dataset.i, 10) + (e.clientY > r.top + r.height / 2 ? 1 : 0);
      const list = getPlaylists();
      const cur = list.find(x => x.id === pid);
      clear();
      if (!cur || !cur.tracks) return;
      if (to > from) to--;
      if (to === from) return;
      const [moved] = cur.tracks.splice(from, 1);
      cur.tracks.splice(to, 0, moved);
      savePlaylists(list);
      from = -1;
      try { window.dispatchEvent(new Event("harmoni-library")); } catch (er) {}
      renderPlaylistView();
    });
  });
}

/* ===== تعديل البلاي لست: الاسم والوصف والصورة ===== */
function plImageToDataUrl(file) {
  return new Promise((resolve, reject) => {
    if (!file || !/^image\//.test(file.type)) return reject(new Error("type"));
    const fr = new FileReader();
    fr.onerror = () => reject(new Error("read"));
    fr.onload = () => {
      const im = new Image();
      im.onerror = () => reject(new Error("decode"));
      im.onload = () => {
        const S = 400, c = document.createElement("canvas");
        c.width = c.height = S;
        const k = Math.max(S / im.width, S / im.height);
        const w = im.width * k, h = im.height * k;
        c.getContext("2d").drawImage(im, (S - w) / 2, (S - h) / 2, w, h);   // قص مربع من النص
        resolve(c.toDataURL("image/jpeg", 0.86));
      };
      im.src = fr.result;
    };
    fr.readAsDataURL(file);
  });
}

function editPlaylistModal(pid) {
  const pl = getPlaylists().find(x => x.id === pid);
  if (!pl) return;
  const firstCover = ((pl.tracks || []).find(x => x.thumbnail) || {}).thumbnail || "";
  let img = pl.img || "";
  showModal(L("تعديل البلاي لست", "Edit playlist"),
    '<div class="ple">' +
      '<div class="ple-cover-wrap"><div class="ple-cover" id="ple-cover"></div>' +
        '<div class="ple-cover-btns">' +
          '<label class="btn ple-upload">' + esc(L("تغيير الصورة", "Change image")) + '<input type="file" id="ple-file" accept="image/*" hidden></label>' +
          '<button type="button" class="btn ple-reset" id="ple-reset">' + esc(L("صورة أول أغنية", "Use first song")) + '</button>' +
        '</div></div>' +
      '<div class="ple-fields">' +
        '<label class="ple-lab" for="ple-name">' + esc(L("الاسم", "Name")) + '</label>' +
        '<input id="ple-name" class="search-input" maxlength="80" autocomplete="off">' +
        '<label class="ple-lab" for="ple-desc">' + esc(L("الوصف (اختياري)", "Description (optional)")) + '</label>' +
        '<textarea id="ple-desc" class="search-input ple-desc" maxlength="300" rows="3"></textarea>' +
        '<p class="ple-msg" id="ple-msg"></p>' +
      '</div>' +
      '<div class="ple-actions"><button type="button" class="btn" id="ple-cancel">' + esc(L("إلغاء", "Cancel")) + '</button>' +
        '<button type="button" class="btn btn-primary" id="ple-save">' + esc(L("حفظ", "Save")) + '</button></div>' +
    '</div>');
  const mc = document.querySelector("#modal .modal-content");
  if (mc) mc.classList.add("ple-modal");
  const $ = id => document.getElementById(id);
  const paintCover = () => {
    const src = img || firstCover;
    $("ple-cover").innerHTML = src ? '<img src="' + esc(src) + '" alt="">' : "♫";
    $("ple-reset").hidden = !img;
  };
  $("ple-name").value = pl.name || "";
  $("ple-desc").value = pl.desc || "";
  paintCover();
  $("ple-file").onchange = e => {
    const f = e.target.files && e.target.files[0];
    if (!f) return;
    $("ple-msg").textContent = "";
    plImageToDataUrl(f).then(url => { img = url; paintCover(); })
      .catch(() => { $("ple-msg").textContent = L("ما قدرنا نقرا الصورة، جرب صورة ثانية", "Couldn't read that image, try another"); });
  };
  $("ple-reset").onclick = () => { img = ""; paintCover(); };
  $("ple-cancel").onclick = hideModal;
  const save = () => {
    const name = $("ple-name").value.trim();
    if (!name) { $("ple-msg").textContent = L("اكتب اسم للبلاي لست", "Give the playlist a name"); $("ple-name").focus(); return; }
    const list = getPlaylists();
    const cur = list.find(x => x.id === pid);
    if (!cur) { hideModal(); return; }
    if (name !== cur.name) cur.custom_name = !!cur.spotify_id;
    if (img !== (cur.img || "")) cur.custom_img = !!cur.spotify_id && !!img;
    cur.name = name;
    cur.desc = $("ple-desc").value.trim();
    cur.img = img;
    savePlaylists(list);
    hideModal();
    try { window.dispatchEvent(new Event("harmoni-library")); } catch (e) {}
    try { renderProfileSections(true); } catch (e) {}
    renderPlaylistView();
    toast(L("تم حفظ التعديلات", "Changes saved"));
  };
  $("ple-save").onclick = save;
  $("ple-name").onkeydown = e => { if (e.key === "Enter") save(); };
  setTimeout(() => { try { $("ple-name").focus(); $("ple-name").select(); } catch (e) {} }, 50);
}


try { document.documentElement.setAttribute("data-playlist-ready", "1"); } catch (e) {}
