/* إحصائيات الاستماع (صفحة حسابي) + حفظ الأغاني المفضلة على الجهاز للتشغيل الفوري */
(function () {
  "use strict";
  var KEY = "mu_stats";
  var MAP_KEY = "mu_artist_map";

  function today(d) {
    d = d || new Date();
    return d.getFullYear() + "-" + String(d.getMonth() + 1).padStart(2, "0") + "-" + String(d.getDate()).padStart(2, "0");
  }
  function blank() { return { v: 1, total: 0, secs: 0, first: Date.now(), tracks: {}, days: {}, hours: new Array(24).fill(0) }; }

  function getStats() {
    var s = null;
    try { s = JSON.parse(localStorage.getItem(KEY) || "null"); } catch (e) {}
    if (!s || typeof s !== "object" || !s.tracks) {
      s = blank();
      // أول مرة: نبدأ من سجل "استمعت مؤخراً" عشان ما تكون الصفحة فاضية
      try {
        (getHistory() || []).slice().reverse().forEach(function (t) {
          var k = trackKey(t);
          if (!k) return;
          s.tracks[k] = { t: t.title || "", a: t.artist || "", th: t.thumbnail || "", u: t.web_url || "", s: t.source || "", c: 1, l: Date.now() };
          s.total += 1;
        });
        s.seeded = true;
      } catch (e) {}
      saveStats(s);
    }
    if (!Array.isArray(s.hours) || s.hours.length !== 24) s.hours = new Array(24).fill(0);
    s.days = s.days || {};
    return s;
  }
  function saveStats(s) { try { localStorage.setItem(KEY, JSON.stringify(s)); } catch (e) {} }

  var lastPlay = { k: "", ts: 0 };
  window.recordPlay = function (t) {
    if (!t) return;
    var k = trackKey(t);
    if (!k) return;
    var now = Date.now();
    if (lastPlay.k === k && now - lastPlay.ts < 45000) return;   // نفس الأغنية خلال 45 ثانية = تشغيلة وحدة
    lastPlay = { k: k, ts: now };
    var s = getStats();
    var e = s.tracks[k] || { t: t.title || "", a: t.artist || "", th: t.thumbnail || "", u: t.web_url || "", s: t.source || "", c: 0 };
    e.c += 1; e.l = now;
    if (t.thumbnail) e.th = t.thumbnail;
    s.tracks[k] = e;
    s.total += 1;
    var d = today();
    s.days[d] = (s.days[d] || 0) + 1;
    s.hours[new Date().getHours()] += 1;
    var keys = Object.keys(s.tracks);
    if (keys.length > 1500) {
      keys.sort(function (a, b) { return (s.tracks[a].c - s.tracks[b].c) || (s.tracks[a].l - s.tracks[b].l); });
      keys.slice(0, keys.length - 1500).forEach(function (x) { delete s.tracks[x]; });
    }
    var dk = Object.keys(s.days).sort();
    if (dk.length > 400) dk.slice(0, dk.length - 400).forEach(function (x) { delete s.days[x]; });
    saveStats(s);
  };
  window.getStatsData = function () { return getStats(); };
  window.mergeRemoteStats = function (remote) {
    if (!remote || typeof remote !== "object" || !remote.tracks) return false;
    var local = getStats();
    if ((remote.total || 0) > (local.total || 0)) { saveStats(remote); return true; }
    return false;
  };

  // وقت الاستماع الفعلي (كل 5 ثواني والصوت شغال)
  var pendingSecs = 0;
  setInterval(function () {
    try {
      var a = document.getElementById("audio"), b = document.getElementById("solo-audio");
      if ((a && !a.paused) || (b && !b.paused)) pendingSecs += 5;
      if (pendingSecs >= 30) { var s = getStats(); s.secs = (s.secs || 0) + pendingSecs; pendingSecs = 0; saveStats(s); }
    } catch (e) {}
  }, 5000);

  /* ---------- المغني الحقيقي ---------- */
  function getMap() { try { return JSON.parse(localStorage.getItem(MAP_KEY) || "{}") || {}; } catch (e) { return {}; } }
  function saveMap(m) { try { localStorage.setItem(MAP_KEY, JSON.stringify(m)); } catch (e) {} }
  var resolving = false;
  function resolveArtists(entries, done) {
    var map = getMap();
    var missing = entries.filter(function (e) { return !map[e.k]; }).slice(0, 40);
    if (!missing.length || resolving) { done(map); return; }
    resolving = true;
    fetch("/api/artist_resolve", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ items: missing.map(function (e) { return { title: e.t, artist: e.a, source: e.s }; }) })
    }).then(function (r) { return r.json(); }).then(function (j) {
      (j.items || []).forEach(function (it, i) { if (it && it.artist) map[missing[i].k] = it; });
      saveMap(map);
    }).catch(function () {}).then(function () { resolving = false; done(map); });
  }

  /* ---------- الواجهة ---------- */
  function fmtDuration(secs) {
    secs = Math.round(secs || 0);
    var h = Math.floor(secs / 3600), m = Math.floor((secs % 3600) / 60);
    if (h) return h + L(" س ", "h ") + m + L(" د", "m");
    return m + L(" دقيقة", " min");
  }
  function streak(days) {
    var n = 0, d = new Date();
    if (!days[today(d)]) d.setDate(d.getDate() - 1);
    while (days[today(d)]) { n++; d.setDate(d.getDate() - 1); }
    return n;
  }
  function timeOfDay(hours) {
    var parts = [
      [L("الفجر", "Early morning"), [3, 4, 5, 6]], [L("الصباح", "Morning"), [7, 8, 9, 10, 11]],
      [L("الظهر والعصر", "Afternoon"), [12, 13, 14, 15, 16]], [L("المغرب", "Evening"), [17, 18, 19, 20]],
      [L("الليل", "Night"), [21, 22, 23, 0, 1, 2]]
    ];
    var best = null, bestN = 0;
    parts.forEach(function (p) {
      var n = p[1].reduce(function (a, h) { return a + (hours[h] || 0); }, 0);
      if (n > bestN) { bestN = n; best = p[0]; }
    });
    return best;
  }
  function initials(name) { return esc(((name || "?").trim()[0] || "?").toUpperCase()); }
  function artistImg(pic, name, cls) {
    return pic ? '<img class="' + cls + '" src="' + esc(pic) + '" alt="" onerror="this.outerHTML=\'<span class=&quot;' + cls + ' st-fallback&quot;>' + initials(name) + '</span>\'">'
               : '<span class="' + cls + ' st-fallback">' + initials(name) + '</span>';
  }

  function render(el, s, map) {
    var entries = Object.keys(s.tracks).map(function (k) { var e = s.tracks[k]; e.k = k; return e; });
    entries.sort(function (a, b) { return (b.c - a.c) || (b.l - a.l); });
    var top = entries.slice(0, 5);

    var artists = {};
    entries.forEach(function (e) {
      var r = map[e.k];
      var name = (r && r.artist) || e.a || L("غير معروف", "Unknown");
      var id = name.toLowerCase().trim();
      var a = artists[id] || (artists[id] = { name: name, pic: "", c: 0, songs: 0 });
      a.c += e.c; a.songs += 1;
      if (!a.pic && r && r.picture) a.pic = r.picture;
    });
    var artistList = Object.keys(artists).map(function (k) { return artists[k]; }).sort(function (a, b) { return b.c - a.c; });
    var ta = artistList[0];

    var week = 0, d = new Date();
    for (var i = 0; i < 7; i++) { week += s.days[today(d)] || 0; d.setDate(d.getDate() - 1); }
    var bars = [], maxDay = 1;
    d = new Date(); d.setDate(d.getDate() - 6);
    for (i = 0; i < 7; i++) {
      var n = s.days[today(d)] || 0;
      maxDay = Math.max(maxDay, n);
      bars.push({ n: n, label: i === 6 ? L("اليوم", "Today") : d.toLocaleDateString(settings.lang === "ar" ? "ar" : "en", { weekday: "short" }),
                  full: d.toLocaleDateString(settings.lang === "ar" ? "ar" : "en", { weekday: "long", day: "numeric", month: "short" }), today: i === 6 });
      d.setDate(d.getDate() + 1);
    }
    var tod = timeOfDay(s.hours);
    var since = new Date(s.first || Date.now()).toLocaleDateString(settings.lang === "ar" ? "ar" : "en", { year: "numeric", month: "long", day: "numeric" });

    var card = function (val, label, sub) {
      return '<div class="st-card"><b>' + val + '</b><span>' + label + '</span>' + (sub ? '<small>' + sub + '</small>' : "") + '</div>';
    };
    var html = '<div class="section-head"><h3>' + esc(L("إحصائياتي", "My stats")) + '</h3><span class="st-since">' + esc(L("منذ ", "Since ") + since) + '</span></div>';
    html += '<div class="st-cards">' +
      card(s.total, L("تشغيلة", "plays")) +
      card(entries.length, L("أغنية مختلفة", "different songs")) +
      card(fmtDuration(s.secs), L("وقت الاستماع", "listening time")) +
      card(artistList.length, L("مغني", "artists")) +
      card(week, L("هذا الأسبوع", "this week")) +
      card(streak(s.days), L("أيام متتالية", "day streak")) +
      '</div>';

    if (ta) {
      var share = s.total ? Math.round(ta.c / s.total * 100) : 0;
      html += '<div class="st-hero">' + artistImg(ta.pic, ta.name, "st-hero-img") +
        '<div class="st-hero-info"><span class="st-kicker">' + esc(L("المغني الأكثر استماعاً", "Top artist")) + '</span>' +
        '<h2 class="st-hero-name">' + esc(ta.name) + '</h2>' +
        '<div class="st-hero-meta">' + ta.c + ' ' + esc(L("تشغيلة", "plays")) + ' • ' + ta.songs + ' ' + esc(L("أغنية", "songs")) + ' • ' + share + '% ' + esc(L("من استماعك", "of your listening")) + '</div>' +
        '<button class="btn st-hero-open">' + esc(L("صفحة المغني", "Artist page")) + '</button></div></div>';
    }

    html += '<div class="st-grid"><div class="st-box"><h4>' + esc(L("أكثر 5 أغاني سمعتها", "Your top 5 songs")) + '</h4><ol class="st-top">';
    top.forEach(function (e, i) {
      var r = map[e.k];
      html += '<li data-k="' + esc(e.k) + '"><span class="st-rank">' + (i + 1) + '</span>' +
        (e.th ? '<img class="st-thumb" src="' + esc(e.th) + '" alt="">' : '<span class="st-thumb"></span>') +
        '<div class="st-main"><div class="st-title">' + esc(e.t) + '</div><div class="st-artist">' + esc((r && r.artist) || e.a || "") + '</div></div>' +
        '<span class="st-count">' + e.c + '×</span><button class="btn st-play" title="' + esc(L("تشغيل", "Play")) + '">▶</button></li>';
    });
    if (!top.length) html += '<li class="st-empty">' + esc(L("شغّل أغاني وتطلع هنا", "Play some songs to see them here")) + '</li>';
    html += '</ol></div>';

    html += '<div class="st-box"><h4>' + esc(L("آخر أسبوع", "Last 7 days")) + '</h4><div class="st-bars">';
    bars.forEach(function (b) {
      html += '<div class="st-bar' + (b.today ? " today" : "") + '" title="' + esc(b.full) + ': ' + b.n + ' ' + esc(L("أغنية", "songs")) + '">' +
        '<b class="st-bar-n">' + b.n + '</b>' +
        '<i style="height:' + (b.n ? Math.max(6, Math.round(b.n / maxDay * 84)) : 3) + 'px"></i>' +
        '<span>' + esc(b.label) + '</span></div>';
    });
    html += '</div>' + (tod ? '<div class="st-note">' + esc(L("أكثر وقت تسمع فيه: ", "You listen most in the ")) + '<b>' + esc(tod) + '</b></div>' : "") + '</div></div>';

    if (artistList.length > 1) {
      html += '<div class="st-box"><h4>' + esc(L("أكثر المغنيين", "Top artists")) + '</h4><div class="st-artists">';
      artistList.slice(0, 5).forEach(function (a, i) {
        html += '<div class="st-artist-chip" data-name="' + esc(a.name) + '">' + artistImg(a.pic, a.name, "st-artist-img") +
          '<b>' + (i + 1) + '. ' + esc(a.name) + '</b><small>' + a.c + ' ' + esc(L("تشغيلة", "plays")) + '</small></div>';
      });
      html += '</div></div>';
    }
    if (s.seeded) html += '<div class="st-foot">' + esc(L("الأرقام بدأت تنحسب من هالتحديث، والأغاني اللي قبله محسوبة مرة وحدة.", "Counting started with this update; earlier songs count once.")) + '</div>';

    el.innerHTML = html;
    el.classList.remove("hidden");
    el.querySelectorAll(".st-top li[data-k]").forEach(function (li) {
      var e = s.tracks[li.dataset.k];
      var trk = { title: e.t, artist: e.a, thumbnail: e.th, web_url: e.u, source: e.s };
      li.querySelector(".st-play").onclick = function () { playSolo(trk); };
    });
    var open = el.querySelector(".st-hero-open");
    if (open && ta) open.onclick = function () { openArtist(ta.name); };
    el.querySelectorAll(".st-artist-chip").forEach(function (c) { c.onclick = function () { openArtist(c.dataset.name); }; });
  }

  function renderInto() {
    var el = document.getElementById("my-stats");
    if (!el) return;
    var s = getStats();
    var entries = Object.keys(s.tracks).map(function (k) { var e = s.tracks[k]; e.k = k; return e; })
      .sort(function (a, b) { return b.c - a.c; }).slice(0, 40);
    render(el, s, getMap());
    resolveArtists(entries, function (map) {
      var view = document.getElementById("view-stats");
      if (view && view.classList.contains("active")) render(el, getStats(), map);
    });
  }
  // صفحة الحساب: زر يفتح صفحة الإحصائيات (بدل ما تظهر مباشرة)
  window.renderMyStats = function (isMine) {
    var btn = document.getElementById("my-stats-btn");
    if (!btn) return;
    btn.classList.toggle("hidden", !isMine);
    var label = btn.querySelector("span");
    if (label) label.textContent = L("إحصائياتي", "My stats");
    btn.onclick = window.openMyStats;
  };
  window.openMyStats = function () {
    showView("stats");
    renderInto();
    var main = document.querySelector(".main");
    if (main) main.scrollTop = 0;
  };
  var back = document.getElementById("stats-back");
  if (back) back.onclick = function () { viewingProfile = null; showView("profile"); };

  /* حفظ المفضلة وآخر الأغاني على الجهاز بالخلفية (بعد ما يفتح البرنامج بشوي) */
  setTimeout(function () {
    try {
      if (typeof cacheAudioOnDisk === "function") cacheAudioOnDisk((getLikes() || []).slice(0, 30).concat((getHistory() || []).slice(0, 15)));
    } catch (e) {}
  }, 15000);
})();

try { document.documentElement.setAttribute("data-stats-ready", "1"); } catch (e) {}
