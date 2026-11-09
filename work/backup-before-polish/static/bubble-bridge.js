/* جسر فقاعة التشغيل — ملف مستقل عن app.js ويقرأ كل شيء من الصفحة نفسها،
   عشان يشتغل حتى لو صار خطأ في أي سكربت ثاني. */
(function () {
  "use strict";
  var errors = [];
  var root = document.documentElement;
  root.setAttribute("data-js-errors", "0");
  window.addEventListener("error", function (e) {
    if (errors.length < 5) errors.push(String(e.message || e.error || "error") + " @" + (e.filename || "").split("/").pop() + ":" + (e.lineno || 0));
    root.setAttribute("data-js-errors", String(errors.length));
    root.setAttribute("data-js-error-list", errors.join(" || "));
  });
  // اختبار تلقائي قبل البناء (test_ui_smoke.py): بيانات تجريبية في متصفح مؤقت
  if (/[?&]selftest=1/.test(location.search)) {
    try {
      localStorage.setItem("mu_history", JSON.stringify([
        { title: "Selftest Song A", artist: "Tester", web_url: "https://www.youtube.com/watch?v=selftestA", source: "youtube" },
        { title: "Selftest Song B", artist: "Tester", web_url: "https://www.youtube.com/watch?v=selftestB", source: "youtube" }]));
    } catch (e) {}
  }

  function $(id) { return document.getElementById(id); }
  function players() { return [$("solo-audio"), $("audio")].filter(Boolean); }
  function activePlayer() {
    var list = players();
    for (var i = 0; i < list.length; i++) if (!list[i].paused) return list[i];
    for (i = 0; i < list.length; i++) if (list[i].currentSrc && list[i].currentTime > 0) return list[i];
    for (i = 0; i < list.length; i++) if (list[i].currentSrc) return list[i];
    return list[0] || null;
  }
  function parseTime(text) {
    var p = String(text || "").trim().split(":").map(Number);
    if (p.some(isNaN)) return 0;
    return p.reduce(function (acc, v) { return acc * 60 + v; }, 0);
  }
  function themeVars() {
    var cs = getComputedStyle(document.documentElement);
    function v(n) { return (cs.getPropertyValue(n) || "").trim(); }
    return { bg: v("--bg"), bg2: v("--bg2"), bg3: v("--bg3"), fg: v("--fg"), muted: v("--muted"),
             border: v("--border"), accent: v("--accent"), onAccent: v("--on-accent"),
             accentText: v("--accent-text") || v("--accent"), grain: v("--glass-grain") };
  }
  function snapshot() {
    var main = $("audio");
    var out = { title: "", volume: main ? Math.round(main.volume * 100) : 50, muted: !!(main && main.muted), theme: themeVars(),
                glass: document.documentElement.getAttribute("data-theme") === "glass" };
    if (errors.length) out.errors = errors.slice();
    var a = activePlayer();
    var hasTrack = players().some(function (p) { return !!p.currentSrc; });
    var titleEl = $("np-title");
    if (!a || !hasTrack || !titleEl) return out;
    out.title = (titleEl.textContent || "").trim();
    out.artist = (($("np-artist") || {}).textContent || "").trim();
    var th = $("np-thumb");
    out.thumbnail = th && th.getAttribute("src") ? th.src : "";
    var dur = (isFinite(a.duration) && a.duration > 0) ? a.duration : parseTime(($("dur-time") || {}).textContent);
    out.duration = dur;
    out.position = a.currentTime || 0;
    out.playing = !a.paused;
    try {
      var cur = currentTrack();
      if (cur && typeof isLiked === "function") out.liked = !!isLiked(cur);
      if (typeof state !== "undefined" && state) { out.loop = !!state.loop; out.shuffle = !!state.shuffle; }
      out.inRoom = typeof inRoom !== "undefined" ? !!inRoom : false;
    } catch (e) {}
    return out;
  }

  var done = [];
  function run(action, value, id) {
    if (id) {
      if (done.indexOf(id) >= 0) return true;
      done.push(id);
      if (done.length > 60) done.shift();
    }
    try {
      if (action === "play_pause") $("btn-play").click();
      else if (action === "skip") $("btn-skip").click();
      else if (action === "prev") $("btn-prev").click();
      else if (action === "mute") $("btn-volume").click();
      else if (action === "seek" && typeof value === "number") {
        var sk = $("seek");
        if (+sk.max < value) sk.max = value + 1;
        sk.value = value;
        sk.dispatchEvent(new Event("input"));
        sk.dispatchEvent(new Event("change"));
      } else if (action === "volume" && typeof value === "number") {
        var v = Math.max(0, Math.min(100, value)) / 100;
        players().forEach(function (p) { p.volume = v; if (p.muted && v > 0) p.muted = false; });
      }
    } catch (e) {}
    setTimeout(push, 80);
    return true;
  }

  var pushing = false;
  function push() {
    if (pushing) return;
    pushing = true;
    var body;
    try { body = JSON.stringify({ state: snapshot() }); } catch (e) { pushing = false; return; }
    fetch("/api/bubble_state", { method: "POST", headers: { "Content-Type": "application/json" }, body: body })
      .then(function (r) { return r.json(); })
      .then(function (j) { ((j && j.cmds) || []).forEach(function (c) { run(c.action, c.value, c.id); }); })
      .catch(function () {})
      .then(function () { pushing = false; });
  }

  /* ===== القائمة الجانبية (Alt+P): البلاي لست الحالية + المفضلة + تحكم كامل ===== */
  function currentTrack() {
    try {
      if (typeof solo !== "undefined" && solo.active && solo.track) return solo.track;
      if (typeof state !== "undefined" && state && state.current) return state.current;
    } catch (e) {}
    return null;
  }
  function slim(t) {
    return { title: t.title || "", artist: t.artist || "", thumbnail: t.thumbnail || "", duration: t.duration || 0,
             web_url: t.web_url || "", source: t.source || "", id: t.id || "" };
  }
  function keyOf(t) { try { return trackKey(t); } catch (e) { return (t && (t.web_url || t.title)) || ""; } }
  function currentList() {
    try {
      if (typeof inRoom !== "undefined" && inRoom && typeof state !== "undefined") {
        var items = (state.current ? [state.current] : []).concat(state.queue || []);
        return { name: (state.name || "") || L("طابور الغرفة", "Room queue"), items: items, room: true };
      }
      if (window.__plPlaying && Array.isArray(window.__plPool) && window.__plPool.length) {
        var nm = (typeof plvCurrent !== "undefined" && plvCurrent && plvCurrent.name) || L("البلاي لست", "Playlist");
        return { name: nm, items: window.__plPool };
      }
      if (typeof soloPool === "function") return { name: L("قائمة التشغيل الحالية", "Now playing list"), items: soloPool() || [] };
    } catch (e) {}
    return { name: "", items: [] };
  }
  function myPlaylists() { try { return (typeof getPlaylists === "function" ? getPlaylists() : []) || []; } catch (e) { return []; } }
  function findPl(id) { var pls = myPlaylists(); for (var i = 0; i < pls.length; i++) if (String(pls[i].id) === String(id)) return pls[i]; return null; }
  window.__harmoniPanel = {
    lists: function () {
      var cur = currentTrack(), ck = cur ? keyOf(cur) : "";
      var likes = [];
      try { likes = (getLikes() || []).slice(0, 300); } catch (e) {}
      var idx = function (list) { for (var i = 0; i < list.length; i++) if (ck && keyOf(list[i]) === ck) return i; return -1; };
      return JSON.stringify({
        likes: { name: L("المفضلة", "Favorites"), index: idx(likes), items: likes.map(slim) },
        playlists: myPlaylists().map(function (p) {
          var tr = p.tracks || [];
          return { id: String(p.id), name: p.name || "", img: p.img || ((tr[0] && tr[0].thumbnail) || ""), count: tr.length };
        }),
        current: ck,
        lang: (typeof settings !== "undefined" && settings.lang) || "ar"
      });
    },
    playlist: function (id) {
      var p = findPl(id);
      if (!p) return "";
      var cur = currentTrack(), ck = cur ? keyOf(cur) : "";
      var items = (p.tracks || []).slice(0, 500);
      var index = -1;
      for (var i = 0; i < items.length; i++) if (ck && keyOf(items[i]) === ck) { index = i; break; }
      return JSON.stringify({ id: String(p.id), name: p.name || "", index: index, items: items.map(slim) });
    },
    play: function (kind, i) {
      try {
        var list = null;
        if (kind === "likes") list = getLikes() || [];
        else if (String(kind).indexOf("pl:") === 0) { var p = findPl(String(kind).slice(3)); list = p ? (p.tracks || []) : null; }
        if (!list || !list[i]) return false;
        window.__plPool = list.slice(); window.__plPlaying = true;
        playSolo(list[i]);
        return true;
      } catch (e) { return false; }
    },
    playTrack: function (t) {
      try {
        if (!t || !t.title) return false;
        window.__plPlaying = false;
        playSolo({ title: t.title, artist: t.artist || "", thumbnail: t.thumbnail || "", duration: t.duration || 0,
                   web_url: t.web_url || "", source: t.source || "youtube" });
        return true;
      } catch (e) { return false; }
    },
    cmd: function (action, value) {
      try {
        if (action === "loop") { var lb = $("btn-loop"); if (lb) lb.click(); }
        else if (action === "shuffle") { var sb = $("btn-shuffle"); if (sb) sb.click(); }
        else if (action === "like") { var cur = currentTrack(); if (cur && typeof toggleLike === "function") toggleLike(cur); }
        else return run(action, value);
      } catch (e) {}
      setTimeout(push, 80);
      return true;
    }
  };

  window.__harmoniBubble = {
    state: function () { return JSON.stringify(snapshot()); },
    cmd: function (action, value, id) { return run(action, value, id); }
  };

  function start() {
    setInterval(push, 1000);
    players().forEach(function (p) {
      ["play", "pause", "loadedmetadata", "volumechange"].forEach(function (ev) {
        p.addEventListener(ev, function () { setTimeout(push, 60); });
      });
    });
    push();
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start);
  else start();
})();
