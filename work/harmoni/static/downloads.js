/* تحميل الأغاني للتشغيل بدون نت (في البرنامج على الكمبيوتر):
   - زر ⬇ على كل أغنية (البطاقات + صفوف البلاي لست)
   - «تحميل البلاي لست» بصفحة البلاي لست، و«تحميل المفضلة» بصفحة المفضلة
   - قسم «الأغاني المحمّلة» بالإعدادات (العدد، الحجم، التقدم، الحذف)
   الأغنية المحمّلة تنحفظ بملفات البرنامج (HarmoniData\downloads) وتشتغل حتى لو النت مقطوع. */
(function () {
  "use strict";
  if (["localhost", "127.0.0.1"].indexOf(location.hostname) < 0) return;
  function L(ar, en) { return (typeof settings !== "undefined" && settings.lang === "en") ? en : ar; }
  function e(s) { return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]; }); }
  function say(m) { try { toast(m); } catch (x) {} }
  function norm(s) { return String(s || "").replace(/\s+/g, " ").trim().toLowerCase(); }
  function tkey(t) { return norm(t && t.artist) + "|" + norm(t && t.title); }

  var st = { items: [], queue: [], current: null, count: 0, total_mb: 0 };
  var byUrl = {}, byTk = {}, thumbs = {}, pending = {};
  var timer = 0;

  function index() {
    byUrl = {}; byTk = {}; thumbs = {}; pending = {};
    (st.items || []).forEach(function (it) {
      if (it.web_url) byUrl[it.web_url] = it;
      if (it.orig_url) byUrl[it.orig_url] = it;
      byTk[tkey(it)] = it;
      if (it.thumb_local && it.thumbnail) thumbs[it.thumbnail] = "/api/downloads/thumb?k=" + it.key;
    });
    (st.queue || []).forEach(function (q) { pending[q.web_url || tkey(q)] = "queued"; });
    if (st.current) pending[st.current.web_url || tkey(st.current)] = "downloading";
  }
  function stateOf(t) {
    if (!t) return "";
    if ((t.web_url && byUrl[t.web_url]) || byTk[tkey(t)]) return "done";
    var p = pending[t.web_url || ""] || pending[tkey(t)];
    return p || "";
  }
  function downloadable(t) {
    var u = String((t && t.web_url) || "");
    return !!(t && t.title && !/^local:/.test(u) && (!u || /^https?:/.test(u)));
  }

  function load() {
    return fetch("/api/downloads").then(function (r) { return r.json(); }).then(apply).catch(function () {});
  }
  function apply(j) {
    if (!j || !j.items) return j;
    var was = st.current ? (st.current.web_url || st.current.title) : "";
    var before = st.count;
    st = j;
    index();
    paintAll();
    if (before && j.count > before && !j.current && !(j.queue || []).length && was) say(L("✓ خلص التحميل — الأغاني تشتغل بدون نت", "✓ Downloads finished — songs play offline"));
    clearTimeout(timer);
    timer = setTimeout(load, (j.current || (j.queue || []).length) ? 1200 : 30000);
    return j;
  }
  function post(url, body) {
    return fetch(url, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body || {}) })
      .then(function (r) { return r.json(); }).then(apply);
  }
  function add(tracks, label) {
    var list = (tracks || []).filter(downloadable).map(function (t) {
      return { web_url: t.web_url || "", title: t.title, artist: t.artist || "", duration: t.duration || 0,
               thumbnail: t.orig_thumbnail || t.thumbnail || "", source: t.source || "youtube" };
    });
    if (!list.length) { say(L("ما فيه أغاني تنحمّل هنا", "Nothing to download here")); return Promise.resolve(); }
    return post("/api/downloads/add", { tracks: list }).then(function (j) {
      if (!j) return;
      if (j.added) say(L("⬇ بدأ تحميل ", "⬇ Downloading ") + j.added + L(" أغنية", " songs") + (label ? " — " + label : ""));
      else if (j.already) say(L("✓ محمّلة من قبل", "✓ Already downloaded"));
    }).catch(function () { say(L("ما قدرنا نبدأ التحميل", "Couldn't start the download")); });
  }
  function remove(tracks) {
    var urls = [];
    (tracks || []).forEach(function (t) {
      var it = (t.web_url && byUrl[t.web_url]) || byTk[tkey(t)];
      if (it) urls.push(it.web_url);
      else if (t.web_url) urls.push(t.web_url);
    });
    return post("/api/downloads/remove", { web_urls: urls }).catch(function () {});
  }

  /* ---------- زر الأغنية ---------- */
  var ICON = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 4v11m0 0 4.5-4.5M12 15l-4.5-4.5M5 19h14"/></svg>';
  var ICON_OK = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 12.5l4.5 4.5L19 7.5"/></svg>';
  function paintBtn(b, t) {
    var s = stateOf(t);
    b.classList.toggle("dl-done", s === "done");
    b.classList.toggle("dl-busy", s === "queued" || s === "downloading");
    var pct = (s === "downloading" && st.current) ? Math.round(st.current.pct || 0) : 0;
    var title = s === "done" ? L("محمّلة (تشتغل بدون نت)", "Downloaded (plays offline)")
      : s === "downloading" ? L("جاري التحميل ", "Downloading ") + pct + "%"
      : s === "queued" ? L("بالانتظار للتحميل", "Waiting to download")
      : L("تحميل الأغنية (تشتغل بدون نت)", "Download (play offline)");
    if (b.title !== title) { b.title = title; b.setAttribute("aria-label", title); }
    var html = s === "done" ? ICON_OK : s === "downloading" ? '<span class="dl-pct">' + pct + '</span>' : ICON;
    if (b.__html !== html) { b.innerHTML = html; b.__html = html; }
  }
  function onBtn(b, getTrack) {
    b.onclick = function (ev) {
      ev.stopPropagation();
      var t = getTrack();
      if (!t) return;
      var s = stateOf(t);
      if (s === "done") { say(L("✓ محمّلة وتشتغل بدون نت", "✓ Downloaded — plays offline")); return; }
      if (s) return;
      add([t]);
    };
  }
  function wireCard(card) {
    if (card.__dlWired || !card.__trk) return;
    var acts = card.querySelector(".track-actions");
    if (!acts || !downloadable(card.__trk)) return;
    card.__dlWired = true;
    var b = document.createElement("button");
    b.type = "button";
    b.className = "btn sq-btn act-dl";
    var like = acts.querySelector(".act-like");
    if (like) acts.insertBefore(b, like); else acts.appendChild(b);
    onBtn(b, function () { return card.__trk; });
    paintBtn(b, card.__trk);
  }
  function rowTrack(row) {
    try {
      var i = parseInt(row.dataset.i, 10);
      return (typeof plvCurrent !== "undefined" && plvCurrent && plvCurrent.tracks) ? plvCurrent.tracks[i] : null;
    } catch (x) { return null; }
  }
  function wireRow(row) {
    if (row.__dlWired) return;
    var t = rowTrack(row);
    if (!t || !downloadable(t)) return;
    row.__dlWired = true;
    var b = document.createElement("button");
    b.type = "button";
    b.className = "btn plv-dl";
    var play = row.querySelector(".plv-play");
    if (play) row.insertBefore(b, play); else row.appendChild(b);
    onBtn(b, function () { return rowTrack(row); });
    paintBtn(b, t);
  }

  /* ---------- أزرار القوائم ---------- */
  function listState(tracks) {
    var all = (tracks || []).filter(downloadable), done = 0, busy = 0;
    all.forEach(function (t) { var s = stateOf(t); if (s === "done") done++; else if (s) busy++; });
    return { total: all.length, done: done, busy: busy };
  }
  function paintListBtn(b, tracks, what) {
    var s = listState(tracks);
    var txt;
    if (!s.total) txt = "⬇ " + (what === "likes" ? L("تحميل المفضلة", "Download likes") : L("تحميل البلاي لست", "Download playlist"));
    else if (s.done === s.total) txt = "✓ " + L("محمّلة كلها (بدون نت)", "All downloaded (offline)");
    else if (s.busy) txt = "⬇ " + L("جاري التحميل ", "Downloading ") + s.done + "/" + s.total;
    else txt = "⬇ " + (what === "likes" ? L("تحميل المفضلة", "Download likes") : L("تحميل البلاي لست", "Download playlist")) +
      (s.done ? " (" + s.done + "/" + s.total + ")" : "");
    if (b.textContent !== txt) b.textContent = txt;
    b.classList.toggle("dl-done", !!s.total && s.done === s.total);
    var tip = s.done === s.total && s.total ? L("محمّلة كلها وتشتغل بدون نت", "All downloaded — plays offline") : L("تنحفظ على جهازك وتشتغل بدون نت", "Saved on your PC, plays offline");
    if (b.title !== tip) b.title = tip;
  }
  function wireListBtn(host, cls, what, getTracks) {
    if (!host) return;
    var b = host.querySelector("." + cls);   // (يدوّر داخل .likes-actions بعد)
    if (!b) {
      b = document.createElement("button");
      b.type = "button";
      b.className = "btn " + cls + (what === "likes" ? " likes-act" : "");
      if (what === "likes") {   // جنب «أضف من جهازك» وبنفس الشكل
        var box = host.querySelector(".likes-actions");
        if (!box) { box = document.createElement("div"); box.className = "likes-actions"; host.appendChild(box); }
        box.appendChild(b);
      } else host.appendChild(b);
      b.onclick = function () {
        var tr = getTracks() || [];
        var s = listState(tr);
        if (s.total && s.done === s.total) { say(L("✓ محمّلة كلها وتشتغل بدون نت", "✓ All downloaded — plays offline")); return; }
        add(tr, what === "likes" ? L("المفضلة", "Likes") : ((typeof plvCurrent !== "undefined" && plvCurrent && plvCurrent.name) || ""));
      };
    }
    paintListBtn(b, getTracks(), what);
  }
  function likesTracks() { try { return getLikes(); } catch (x) { return []; } }
  function plTracks() { try { return (plvCurrent && plvCurrent.tracks) || []; } catch (x) { return []; } }

  function paintAll() {
    document.querySelectorAll(".track-card").forEach(function (c) {
      if (!c.__dlWired) wireCard(c);
      var b = c.querySelector(".act-dl");
      if (b && c.__trk) paintBtn(b, c.__trk);
    });
    document.querySelectorAll(".plv-row").forEach(function (r) {
      if (!r.__dlWired) wireRow(r);
      var b = r.querySelector(".plv-dl"), t = rowTrack(r);
      if (b && t) paintBtn(b, t);
    });
    wireListBtn(document.getElementById("plv-actions"), "plv-download", "playlist", plTracks);
    var lh = document.querySelector("#view-likes .queue-head");
    if (lh) wireListBtn(lh, "likes-download", "likes", likesTracks);
    paintSettings();
  }

  function setText(el, txt) { if (el && el.textContent !== txt) el.textContent = txt; }   // بدون تغيير = بدون إعادة رسم

  /* ---------- الإعدادات ---------- */
  function paintSettings() {
    var body = document.getElementById("settings-body");
    if (!body) return;
    var g = document.getElementById("downloads-settings");
    if (!g) {
      g = document.createElement("div");
      g.className = "settings-group";
      g.id = "downloads-settings";
      g.dataset.key = "downloads";
      g.innerHTML = '<div class="settings-head"></div><p class="sp-link-desc dls-desc"></p><p class="dls-use duck-live" role="status"></p>' +
        '<div class="settings-options"><button type="button" class="opt-btn dls-cancel"></button><button type="button" class="opt-btn dls-clear"></button></div>';
      var store = document.getElementById("storage-settings");
      if (store && store.parentNode === body) body.insertBefore(g, store); else body.appendChild(g);
      g.querySelector(".dls-cancel").onclick = function () { post("/api/downloads/cancel").catch(function () {}); };
      g.querySelector(".dls-clear").onclick = function () {
        var b = this;
        if (!b.__confirm || Date.now() - b.__confirm > 4000) { b.__confirm = Date.now(); b.textContent = L("متأكد؟ اضغط مرة ثانية", "Sure? Click again"); return; }
        b.__confirm = 0;
        post("/api/downloads/remove", { all: true }).catch(function () {});
      };
    }
    setText(g.querySelector(".settings-head"), L("الأغاني المحمّلة (بدون نت)", "Downloaded songs (offline)"));
    setText(g.querySelector(".dls-desc"), L(
      "حمّل أي أغنية أو بلاي لست أو المفضلة من زر ⬇، وتنحفظ بملفات البرنامج على جهازك وتشتغل حتى لو النت مقطوع. ما تنمسح لحالها مثل الأغاني المحفوظة تحت.",
      "Download any song, playlist or your likes with ⬇. They're stored in Harmoni's files on this PC and play even without internet. Unlike the saved songs below, they're never removed automatically."));
    var t = st.count + L(" أغنية · ", " songs · ") + st.total_mb + L(" ميجا", " MB");
    var q = (st.queue || []).length + (st.current ? 1 : 0);
    if (st.current) t += L(" — جاري تحميل «", " — downloading “") + (st.current.title || "") + "» " + Math.round(st.current.pct || 0) + "%" + (q > 1 ? L(" (باقي ", " (") + (q - 1) + L(")", " left)") : "");
    if (st.error && !q) t += L(" — آخر خطأ: ", " — last error: ") + st.error;
    setText(g.querySelector(".dls-use"), t);
    var c = g.querySelector(".dls-cancel");
    setText(c, L("وقّف التحميل", "Stop downloading"));
    if (c.hidden !== !q) c.hidden = !q;
    var cl = g.querySelector(".dls-clear");
    if (!cl.__confirm) setText(cl, L("احذف كل المحمّل", "Remove all downloads"));
    if (cl.hidden !== !st.count) cl.hidden = !st.count;
  }
  var orig = window.renderSettings;
  if (typeof orig === "function") {
    window.renderSettings = function () { var r = orig.apply(this, arguments); try { paintSettings(); } catch (x) {} return r; };
  }

  /* ---------- الغلاف بدون نت: لو صورة أغنية محمّلة ما انحملت نعرض النسخة المحفوظة ---------- */
  document.addEventListener("error", function (ev) {
    var img = ev.target;
    if (!img || img.tagName !== "IMG") return;
    var src = img.getAttribute("src") || "";
    var local = thumbs[src];
    if (local && img.dataset.dlThumb !== "1") { img.dataset.dlThumb = "1"; img.classList.remove("no-art"); img.style.visibility = ""; img.src = local; }
  }, true);

  /* ---------- نراقب الصفحة: أي بطاقة/صف جديد ياخذ زر التحميل ---------- */
  var queued = false;
  new MutationObserver(function (muts) {
    if (queued) return;
    // تحديثات الوقت (نصوص) ما تهمنا — بس لو انضافت عناصر (بطاقات/صفوف/أزرار)
    var el = false;
    for (var i = 0; i < muts.length && !el; i++) {
      var a = muts[i].addedNodes;
      for (var j = 0; j < a.length; j++) if (a[j].nodeType === 1) { el = true; break; }
    }
    if (!el) return;
    queued = true;
    requestAnimationFrame(function () { queued = false; paintAll(); });
  }).observe(document.body || document.documentElement, { childList: true, subtree: true });

  var css = document.createElement("style");
  css.textContent =
    ".track-actions .act-dl{flex:0 0 32px!important;min-width:32px;display:inline-flex;align-items:center;justify-content:center;padding:0}" +
    ".act-dl svg,.plv-dl svg{width:16px;height:16px;fill:none;stroke:currentColor;stroke-width:2;stroke-linecap:round;stroke-linejoin:round}" +
    ".act-dl.dl-done,.plv-dl.dl-done{color:var(--accent,#1db954)!important}" +
    ".act-dl.dl-busy,.plv-dl.dl-busy{opacity:.85}" +
    ".dl-pct{font-size:10px;font-weight:700;font-variant-numeric:tabular-nums}" +
    ".plv-dl{width:34px;height:34px;min-width:34px;padding:0;border-radius:50%;display:inline-flex;align-items:center;justify-content:center;flex-shrink:0}" +
    ".plv-download.dl-done{color:var(--accent,#1db954)}" +
    "#view-likes .queue-head{gap:10px;flex-wrap:wrap}";
  document.head.appendChild(css);

  window.harmoniDownloads = { add: add, remove: remove, state: stateOf, refresh: load };
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", load); else load();
})();
